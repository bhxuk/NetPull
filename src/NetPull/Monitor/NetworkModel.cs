using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using NetPull.Core;

namespace NetPull.Monitor
{
    /// <summary>One box on the network map.</summary>
    public sealed class NetDevice
    {
        public string Key;
        public string Mac;
        public string Ip;
        public string Name;
        public string Hostname;
        public string Vendor;
        public string Model;
        public string Os;
        public string Kind = "device";   // gateway, switch, ap, computer, phone, tv, camera, printer, server, smart, web, device, tailscale
        public string Group = "lan";     // lan or tailscale
        public string ParentKey;
        public bool IsInfra;
        public bool IsGateway;
        public bool IsThisPc;
        public bool Online;
        public int? PingMs;
        public bool Wired;
        public string Connection;
        public string Ssid;
        public string Band;
        public int Signal;
        public long Uptime;
        public long DownRate;
        public long UpRate;
        public long LinkDown;            // counters since it connected (UniFi) or with this PC (Tailscale)
        public long LinkUp;
        public List<int> Ports = new List<int>();
        public bool PortsKnown;
        public List<(string category, long down, long up)> Dpi;
        public DeviceRecord Record;
        public bool Trusted => Record?.Trusted ?? true;

        public string DisplayName =>
            IsThisPc ? Environment.MachineName + " (this PC)" :
            !string.IsNullOrWhiteSpace(Record?.Name) ? Record.Name :
            !string.IsNullOrWhiteSpace(Name) ? Name :
            !string.IsNullOrWhiteSpace(Hostname) ? Hostname :
            !string.IsNullOrWhiteSpace(Vendor) ? Vendor + " device" : (Ip ?? Mac ?? "Unknown device");
    }

    /// <summary>Ten seconds (or so) of traffic for each device, from UniFi.</summary>
    public sealed class NetSample
    {
        public long Time;
        public int Seconds;
        public Dictionary<string, (long down, long up)> Devices = new Dictionary<string, (long, long)>();
        public long Down => Devices.Values.Sum(v => v.down);
        public long Up => Devices.Values.Sum(v => v.up);
    }

    /// <summary>Brings together the local scan, UniFi and Tailscale into one picture of the whole network.</summary>
    public static class NetworkModel
    {
        private static readonly object Gate = new object();
        private static List<NetDevice> _devices = new List<NetDevice>();
        private static List<UniClient> _clients = new List<UniClient>();
        private static List<UniDevice> _infra = new List<UniDevice>();
        private static List<TailPeer> _peers = new List<TailPeer>();
        private static Dictionary<string, List<(string, long, long)>> _dpi = new Dictionary<string, List<(string, long, long)>>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, int> Pings = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, (long down, long up)> LastCounters = new Dictionary<string, (long, long)>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, (long down, long up)> PeerRates = new Dictionary<string, (long, long)>();
        private static readonly Dictionary<string, (long rx, long tx, DateTime at)> PeerLast = new Dictionary<string, (long, long, DateTime)>();
        private static readonly Dictionary<string, Totals> Minute = new Dictionary<string, Totals>(StringComparer.OrdinalIgnoreCase);
        private static long _minute = -1;
        private static readonly List<NetSample> _samples = new List<NetSample>();
        private static DateTime _lastPoll = DateTime.MinValue;
        private static UniFiClient _uni;
        private static Timer _uniTimer, _tsTimer, _portTimer;
        private static readonly Dictionary<string, int> Misses = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> Offline = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, DateTime> SpikeCooldown = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> CapAlerted = new HashSet<string>();
        private static int _dpiCounter;

        public static string UniFiStatus { get; private set; } = "Not set up";
        public static bool UniFiOk { get; private set; }
        public static DateTime UniFiLastPoll { get; private set; } = DateTime.MinValue;

        /// <summary>Raised on a background thread when the map should redraw.</summary>
        public static event Action Changed;

        public static List<NetDevice> Devices
        {
            get { lock (Gate) return _devices.ToList(); }
        }

        public static List<NetSample> Samples(int seconds)
        {
            long from = Format.NowUnix - seconds;
            lock (_samples) return _samples.Where(s => s.Time > from).ToList();
        }

        public static void Start()
        {
            LanWatch.ScanFinished += () => Task.Run(AfterScan);
            Restart();
            _tsTimer = new Timer(_ => PollTailscale(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(20));
            _portTimer = new Timer(_ => _ = ScanNextUnscanned(), null, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(30));
            Task.Run(Rebuild);
        }

        /// <summary>Re-reads the UniFi settings (after they change).</summary>
        public static void Restart()
        {
            _uniTimer?.Dispose();
            _uniTimer = null;
            _uni?.Dispose();
            _uni = null;
            lock (LastCounters) LastCounters.Clear();
            var s = Settings.Current;
            if (!s.UniFi || string.IsNullOrWhiteSpace(s.UniFiHost) || string.IsNullOrWhiteSpace(s.UniFiUser))
            {
                UniFiOk = false;
                UniFiStatus = s.UniFi ? "Add the console address and a user" : "Not set up";
                lock (Gate) { _clients = new List<UniClient>(); _infra = new List<UniDevice>(); }
                Task.Run(Rebuild);
                return;
            }
            _uni = new UniFiClient(s.UniFiHost, s.UniFiUser, Secret.Unprotect(s.UniFiPasswordProtected), s.UniFiSite);
            UniFiStatus = "Connecting...";
            _uniTimer = new Timer(_ => _ = PollUniFi(), null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10));
        }

        public static async Task<string> TestUniFi(string host, string user, string pass, string site)
        {
            try
            {
                using var c = new UniFiClient(host, user, pass, site);
                await c.Login();
                var clients = await c.Clients();
                var infra = await c.Devices();
                return "Connected. " + clients.Count + " devices, " + infra.Count + " UniFi gateways, switches and access points.";
            }
            catch (Exception ex) { return "Couldn't connect: " + ex.Message; }
        }

        private static int _polling;

        private static async Task PollUniFi()
        {
            var uni = _uni;
            if (uni == null || Interlocked.Exchange(ref _polling, 1) == 1) return;
            try
            {
                var clients = await uni.Clients();
                var infra = await uni.Devices();
                if (_dpiCounter++ % 30 == 0)
                {
                    try { var d = await uni.Dpi(); lock (Gate) _dpi = d; } catch (Exception ex) { Log.Write("UniFi DPI: " + ex.Message); }
                }
                var now = DateTime.UtcNow;
                int secs = _lastPoll == DateTime.MinValue ? 10 : (int)Math.Max(1, Math.Round((now - _lastPoll).TotalSeconds));
                _lastPoll = now;

                // Turn counters into amounts used since the last poll.
                var sample = new NetSample { Time = Format.NowUnix, Seconds = secs };
                lock (LastCounters)
                {
                    foreach (var c in clients)
                    {
                        if (LastCounters.TryGetValue(c.Mac, out var last))
                        {
                            long d = c.DownBytes >= last.down ? c.DownBytes - last.down : c.DownBytes;
                            long u = c.UpBytes >= last.up ? c.UpBytes - last.up : c.UpBytes;
                            if (d > 0 || u > 0) sample.Devices[c.Mac] = (d, u);
                        }
                        LastCounters[c.Mac] = (c.DownBytes, c.UpBytes);
                    }
                }
                lock (_samples)
                {
                    _samples.Add(sample);
                    long cut = Format.NowUnix - 3600;
                    _samples.RemoveAll(x => x.Time < cut);
                }
                AddToMinute(sample);

                lock (Gate) { _clients = clients; _infra = infra; }
                UniFiOk = true;
                UniFiLastPoll = DateTime.UtcNow;
                UniFiStatus = "Connected · " + clients.Count + " devices";
                MergeUniFiClients(clients);
                Rebuild();
            }
            catch (Exception ex)
            {
                UniFiOk = false;
                UniFiStatus = ex is TaskCanceledException ? "UniFi isn't answering. Check the address." : ex.Message;
                Log.Write("UniFi: " + ex.Message);
                try { Changed?.Invoke(); } catch { }
            }
            finally { Interlocked.Exchange(ref _polling, 0); }
        }

        private static void AddToMinute(NetSample s)
        {
            long minute = s.Time - s.Time % 60;
            lock (Minute)
            {
                if (_minute != -1 && minute != _minute && Minute.Count > 0)
                {
                    var rows = new Dictionary<string, Totals>(Minute, StringComparer.OrdinalIgnoreCase);
                    long saved = _minute;
                    Minute.Clear();
                    try { Db.WriteDeviceMinute(saved, rows); } catch (Exception ex) { Log.Write("Device history: " + ex.Message); }
                    Task.Run(() => CheckUsageAlerts(saved));
                }
                _minute = minute;
                foreach (var kv in s.Devices)
                {
                    Minute.TryGetValue(kv.Key, out var t);
                    t.Rx += kv.Value.down; t.Tx += kv.Value.up;
                    Minute[kv.Key] = t;
                }
            }
        }

        /// <summary>Saves UniFi's clients into NetPull's device list, and flags new ones.</summary>
        private static void MergeUniFiClients(List<UniClient> clients)
        {
            var known = Db.Devices().ToDictionary(d => d.Mac, StringComparer.OrdinalIgnoreCase);
            bool baseline = !Settings.Current.UniFiBaselineDone;
            long now = Format.NowUnix;
            foreach (var c in clients)
            {
                if (known.TryGetValue(c.Mac, out var d))
                {
                    bool change = d.Ip != c.Ip || now - d.LastSeen > 60
                                  || (string.IsNullOrEmpty(d.Hostname) && !string.IsNullOrEmpty(c.Hostname))
                                  || (string.IsNullOrEmpty(d.Vendor) && !string.IsNullOrEmpty(c.Vendor));
                    if (!change) continue;
                    d.Ip = c.Ip ?? d.Ip;
                    d.LastSeen = now;
                    if (string.IsNullOrEmpty(d.Hostname)) d.Hostname = c.Hostname;
                    if (string.IsNullOrEmpty(d.Vendor)) d.Vendor = c.Vendor;
                    Db.SaveDevice(d);
                }
                else
                {
                    d = new DeviceRecord { Mac = c.Mac, Ip = c.Ip, Hostname = c.Hostname, Vendor = c.Vendor, FirstSeen = now, LastSeen = now, Trusted = baseline };
                    Db.SaveDevice(d);
                    if (!baseline) LanWatch.RaiseNewDevice(d);
                }
            }
            if (baseline)
            {
                Settings.Current.UniFiBaselineDone = true;
                Settings.Save();
            }
        }

        private static void PollTailscale()
        {
            if (!Settings.Current.Tailscale) { lock (Gate) _peers = new List<TailPeer>(); return; }
            var peers = Tailnet.Status();
            var now = DateTime.UtcNow;
            lock (PeerRates)
            {
                foreach (var p in peers)
                {
                    if (p.Id == null) continue;
                    if (PeerLast.TryGetValue(p.Id, out var last))
                    {
                        double secs = Math.Max(1, (now - last.at).TotalSeconds);
                        long rx = p.RxBytes >= last.rx ? p.RxBytes - last.rx : 0, tx = p.TxBytes >= last.tx ? p.TxBytes - last.tx : 0;
                        PeerRates[p.Id] = ((long)(rx / secs), (long)(tx / secs));
                    }
                    PeerLast[p.Id] = (p.RxBytes, p.TxBytes, now);
                }
            }
            lock (Gate) _peers = peers;
            Rebuild();
        }

        private static async Task AfterScan()
        {
            // Time how long each device takes to answer.
            var online = LanWatch.OnlineNow.ToList();
            var ips = Db.Devices().Where(d => online.Contains(d.Mac, StringComparer.OrdinalIgnoreCase)).ToList();
            using var gate = new SemaphoreSlim(16);
            await Task.WhenAll(ips.Select(async d =>
            {
                await gate.WaitAsync();
                try
                {
                    using var p = new Ping();
                    var r = await p.SendPingAsync(d.Ip, 800);
                    lock (Pings)
                    {
                        if (r.Status == IPStatus.Success) Pings[d.Mac] = (int)r.RoundtripTime; else Pings.Remove(d.Mac);
                    }
                }
                catch { }
                finally { gate.Release(); }
            }));
            Rebuild();
        }

        // ---------------- ports ----------------

        private static int _portBusy;

        private static async Task ScanNextUnscanned()
        {
            if (!Settings.Current.PortScanNew) return;
            var next = Devices.FirstOrDefault(d => d.Group == "lan" && d.Online && !d.IsThisPc && !d.IsInfra && d.Record != null && d.Record.PortsScanned == 0 && !string.IsNullOrEmpty(d.Ip));
            if (next != null) await ScanPorts(next);
        }

        public static async Task ScanPorts(NetDevice d)
        {
            if (d.Record == null || string.IsNullOrEmpty(d.Ip)) return;
            if (Interlocked.Exchange(ref _portBusy, 1) == 1) return;
            try
            {
                var open = await PortScan.Scan(d.Ip);
                d.Record.Ports = string.Join(",", open);
                d.Record.PortsScanned = Format.NowUnix;
                d.Record.Kind = PortScan.GuessKind(d.Record.Hostname + " " + d.Record.Name, d.Record.Vendor, open);
                Db.SaveDevice(d.Record);
            }
            catch (Exception ex) { Log.Write("Port scan: " + ex.Message); }
            finally { Interlocked.Exchange(ref _portBusy, 0); }
            Rebuild();
        }

        // ---------------- building the picture ----------------

        private static string DefaultGatewayIp()
        {
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    var kind = Adapters.KindOf(ni);
                    if (kind == "Tailscale" || kind == "VPN") continue;
                    var gw = ni.GetIPProperties().GatewayAddresses.Select(g => g.Address).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !a.Equals(System.Net.IPAddress.Any));
                    if (gw != null) return gw.ToString();
                }
            }
            catch { }
            return null;
        }

        private static HashSet<string> ThisPcMacs()
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    var b = ni.GetPhysicalAddress().GetAddressBytes();
                    if (b.Length == 6) set.Add(string.Join(":", b.Select(x => x.ToString("X2"))));
                }
            }
            catch { }
            return set;
        }

        public static void Rebuild()
        {
            try
            {
                List<UniClient> clients; List<UniDevice> infra; List<TailPeer> peers; Dictionary<string, List<(string, long, long)>> dpi;
                lock (Gate) { clients = _clients; infra = _infra; peers = _peers; dpi = _dpi; }
                bool uni = UniFiOk && infra.Count > 0;
                var records = Db.Devices();
                var byMac = clients.ToDictionary(c => c.Mac, StringComparer.OrdinalIgnoreCase);
                var infraByMac = infra.ToDictionary(d => d.Mac, StringComparer.OrdinalIgnoreCase);
                var scanOnline = LanWatch.OnlineNow;
                var mine = ThisPcMacs();
                string gwIp = DefaultGatewayIp();
                long now = Format.NowUnix;
                var list = new List<NetDevice>();

                // UniFi gateway, switches and access points.
                string gatewayKey = null;
                foreach (var d in infra)
                {
                    var n = new NetDevice
                    {
                        Key = d.Mac, Mac = d.Mac, Ip = d.Ip, Name = d.Name, Model = d.Model, Vendor = "Ubiquiti", Kind = d.Type,
                        IsInfra = true, IsGateway = d.Type == "gateway", Online = d.Online, Uptime = d.Uptime,
                        ParentKey = d.UplinkMac != null && infraByMac.ContainsKey(d.UplinkMac) ? d.UplinkMac : null,
                        Connection = d.UplinkMac != null && infraByMac.TryGetValue(d.UplinkMac, out var up) ? (d.UplinkPort > 0 ? "Port " + d.UplinkPort + " on " + up.Name : "Connected to " + up.Name) : null,
                        DownRate = d.WanDownRate, UpRate = d.WanUpRate, Wired = true
                    };
                    n.Record = records.FirstOrDefault(r => r.Mac.Equals(d.Mac, StringComparison.OrdinalIgnoreCase));
                    if (n.IsGateway) gatewayKey ??= n.Key;
                    list.Add(n);
                }

                // Everything else NetPull has seen on the local network.
                foreach (var r in records)
                {
                    if (infraByMac.ContainsKey(r.Mac)) continue;
                    byMac.TryGetValue(r.Mac, out var c);
                    var n = new NetDevice
                    {
                        Key = r.Mac, Mac = r.Mac, Ip = c?.Ip ?? r.Ip, Name = c?.Name, Hostname = c?.Hostname ?? r.Hostname, Vendor = r.Vendor ?? c?.Vendor,
                        Record = r, IsThisPc = mine.Contains(r.Mac),
                        Ports = PortScan.Parse(r.Ports), PortsKnown = r.PortsScanned > 0
                    };
                    n.Online = c != null || scanOnline.Contains(r.Mac) || n.IsThisPc
                               || (LanWatch.LastScan == DateTime.MinValue && !uni && now - r.LastSeen < 900);
                    lock (Pings) if (Pings.TryGetValue(r.Mac, out int ms)) n.PingMs = ms;
                    if (c != null)
                    {
                        n.Wired = c.Wired; n.Ssid = c.Ssid; n.Band = c.Band; n.Signal = c.Signal; n.Uptime = c.Uptime;
                        n.DownRate = c.DownRate; n.UpRate = c.UpRate; n.LinkDown = c.DownBytes; n.LinkUp = c.UpBytes;
                        n.ParentKey = c.UplinkMac != null && infraByMac.ContainsKey(c.UplinkMac) ? c.UplinkMac : gatewayKey;
                        if (c.UplinkMac != null && infraByMac.TryGetValue(c.UplinkMac, out var parent))
                            n.Connection = c.Wired
                                ? (c.SwitchPort > 0 ? "Port " + c.SwitchPort + " on " + parent.Name : "Wired to " + parent.Name)
                                : "Wi-Fi " + (c.Band ?? "") + (c.Signal != 0 ? ", " + c.Signal + " dBm" : "") + " via " + parent.Name;
                        if (dpi.TryGetValue(r.Mac, out var cats)) n.Dpi = cats;
                    }
                    if (n.IsThisPc)
                    {
                        var live = TrafficMonitor.Instance.LiveSamples(1).LastOrDefault();
                        if (live != null && c == null) { n.DownRate = live.Rx; n.UpRate = live.Tx; }
                    }
                    bool isGw = !uni && gwIp != null && n.Ip == gwIp;
                    n.IsGateway = isGw;
                    n.Kind = isGw ? "gateway" : n.IsThisPc ? "computer" : !string.IsNullOrEmpty(r.Kind) ? r.Kind
                        : PortScan.GuessKind((n.Name ?? "") + " " + (n.Hostname ?? "") + " " + (r.Name ?? ""), n.Vendor, n.Ports);
                    if (isGw) gatewayKey ??= n.Key;
                    list.Add(n);
                }

                // Without UniFi everything hangs off the router.
                foreach (var n in list.Where(x => x.Group == "lan" && !x.IsInfra && !x.IsGateway && x.ParentKey == null)) n.ParentKey = gatewayKey;

                // Tailscale machines.
                foreach (var p in peers.Where(p => !p.IsSelf && p.Id != null))
                {
                    var n = new NetDevice
                    {
                        Key = "ts:" + p.Id, Name = p.Name, Hostname = p.DnsName, Ip = p.Ip, Os = p.Os, Kind = "tailscale", Group = "tailscale",
                        Online = p.Online, LinkDown = p.RxBytes, LinkUp = p.TxBytes,
                        Connection = !p.Online ? (p.LastSeen > DateTime.MinValue ? "Last seen " + Format.Ago(p.LastSeen.ToUniversalTime()) : "Offline")
                                   : p.Direct ? "Direct connection" : "Through relay " + (p.Relay ?? "")
                    };
                    lock (PeerRates) if (PeerRates.TryGetValue(p.Id, out var rate)) { n.DownRate = rate.down; n.UpRate = rate.up; }
                    list.Add(n);
                }

                lock (Gate) _devices = list;
                CheckOnline(list);
            }
            catch (Exception ex) { Log.Write("Network model: " + ex); }
            try { Changed?.Invoke(); } catch { }
        }

        // ---------------- device alerts ----------------

        private static void CheckOnline(List<NetDevice> list)
        {
            if (!Settings.Current.AlertDeviceOffline) return;
            foreach (var n in list)
            {
                if (n.Record == null || !n.Record.AlertOffline) continue;
                if (n.Online)
                {
                    Misses.Remove(n.Key);
                    if (Offline.Remove(n.Key)) AlertEngine.Add(AlertKinds.DeviceOnline, n.DisplayName + " is back online", n.Ip ?? "", 0);
                    continue;
                }
                Misses.TryGetValue(n.Key, out int m);
                Misses[n.Key] = ++m;
                // Two checks in a row, so a phone napping for a moment doesn't set it off.
                if (m == 2 && Offline.Add(n.Key)) AlertEngine.Add(AlertKinds.DeviceOffline, n.DisplayName + " went offline", "Last seen at " + (n.Ip ?? "?") + ".", 0);
            }
        }

        private static void CheckUsageAlerts(long minute)
        {
            var s = Settings.Current;
            long to = minute + 60;
            var names = Devices.ToDictionary(d => d.Key, d => d.DisplayName, StringComparer.OrdinalIgnoreCase);
            if (s.AlertDeviceSpike && s.DeviceSpikeMegabytes > 0)
            {
                long limit = s.DeviceSpikeMegabytes * 1024L * 1024L;
                long from = to - Math.Max(1, s.SpikeMinutes) * 60L;
                foreach (var mac in Db.DeviceTotals(minute, to).Keys)
                {
                    if (SpikeCooldown.TryGetValue(mac, out var until) && DateTime.UtcNow < until) continue;
                    long used = Db.DeviceRecent(mac, from, to);
                    if (used < limit) continue;
                    SpikeCooldown[mac] = DateTime.UtcNow.AddHours(1);
                    names.TryGetValue(mac, out var name);
                    AlertEngine.Add(AlertKinds.DeviceSpike, (name ?? mac) + " is using a lot of data", Format.Size(used) + " in the last " + s.SpikeMinutes + " minutes.", 0);
                }
            }
            if (minute % 300 == 0)
            {
                var capped = Db.Devices().Where(d => d.CapGb > 0).ToList();
                if (capped.Count == 0) return;
                var start = AlertEngine.CapPeriodStart();
                var totals = Db.DeviceTotals(Format.LocalToUnix(start), Format.NowUnix + 3600);
                foreach (var d in capped)
                {
                    if (!totals.TryGetValue(d.Mac, out var t) || t.Sum < d.CapGb * 1024 * 1024 * 1024) continue;
                    string key = d.Mac + "|" + start.Ticks;
                    if (!CapAlerted.Add(key)) continue;
                    names.TryGetValue(d.Mac, out var name);
                    AlertEngine.Add(AlertKinds.DeviceCap, (name ?? d.DisplayName) + " reached its data limit", Format.Size(t.Sum) + " used of " + d.CapGb + " GB this month.", 0);
                }
            }
        }
    }
}
