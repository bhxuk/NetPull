using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using NetPull.Core;

namespace NetPull.Monitor
{
    /// <summary>Checks the local network every few minutes with quiet ARP requests and notices devices it hasn't seen before.</summary>
    public static class LanWatch
    {
        private static Timer _timer;
        private static int _scanning;
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };

        public static DateTime LastScan { get; private set; } = DateTime.MinValue;
        public static bool Scanning => _scanning == 1;

        /// <summary>MAC addresses that answered the last scan.</summary>
        public static HashSet<string> OnlineNow { get; private set; } = new HashSet<string>();

        public static event Action<DeviceRecord> NewDevice;
        public static event Action ScanFinished;
        public static event Action<double> Progress;

        public static void Start()
        {
            Http.DefaultRequestHeaders.UserAgent.ParseAdd("NetPull/" + Updates.CurrentText);
            Reschedule();
        }

        public static void Reschedule()
        {
            _timer?.Dispose();
            _timer = null;
            if (!Settings.Current.LanWatch) return;
            var every = TimeSpan.FromMinutes(Math.Max(1, Settings.Current.LanScanMinutes));
            _timer = new Timer(_ => _ = ScanNow(), null, TimeSpan.FromSeconds(30), every);
        }

        private sealed class Subnet { public IPAddress Local; public uint Net; public uint Broadcast; public string Gateway; }

        private static List<Subnet> Subnets()
        {
            var list = new List<Subnet>();
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                string kind = Adapters.KindOf(ni);
                if (kind == "Tailscale" || kind == "VPN") continue;
                IPInterfaceProperties p;
                try { p = ni.GetIPProperties(); } catch { continue; }
                var gw = p.GatewayAddresses.Select(g => g.Address).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !a.Equals(IPAddress.Any));
                if (gw == null) continue;   // only real networks with a router
                foreach (var u in p.UnicastAddresses)
                {
                    if (u.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    int prefix = Math.Max(22, u.PrefixLength);   // at most 1,022 addresses
                    var b = u.Address.GetAddressBytes();
                    uint ip = (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]);
                    uint mask = prefix >= 32 ? 0xFFFFFFFF : ~((1u << (32 - prefix)) - 1);
                    list.Add(new Subnet { Local = u.Address, Net = ip & mask, Broadcast = (ip & mask) | ~mask, Gateway = gw.ToString() });
                }
            }
            return list;
        }

        private static uint ToArp(uint hostOrder) => (uint)IPAddress.HostToNetworkOrder((int)hostOrder);

        public static async Task ScanNow()
        {
            if (Interlocked.Exchange(ref _scanning, 1) == 1) return;
            try
            {
                var found = new Dictionary<string, string>();   // mac -> ip
                var subnets = Subnets();
                int total = subnets.Sum(s => (int)(s.Broadcast - s.Net - 1)), done = 0;
                using var gate = new SemaphoreSlim(32);
                var tasks = new List<Task>();
                foreach (var sn in subnets)
                {
                    var lb = sn.Local.GetAddressBytes();
                    uint src = BitConverter.ToUInt32(lb, 0);
                    for (uint a = sn.Net + 1; a < sn.Broadcast; a++)
                    {
                        uint target = a;
                        await gate.WaitAsync();
                        tasks.Add(Task.Run(() =>
                        {
                            try
                            {
                                var mac = new byte[6];
                                int len = 6;
                                if (Native.SendARP(ToArp(target), src, mac, ref len) == 0 && len == 6)
                                {
                                    string m = string.Join(":", mac.Select(x => x.ToString("X2")));
                                    string ip = new IPAddress(ToArp(target)).ToString();
                                    if (m != "00:00:00:00:00:00" && m != "FF:FF:FF:FF:FF:FF") lock (found) found[m] = ip;
                                }
                            }
                            catch { }
                            finally
                            {
                                gate.Release();
                                int d = Interlocked.Increment(ref done);
                                if (d % 16 == 0) Progress?.Invoke(d / (double)Math.Max(1, total));
                            }
                        }));
                    }
                }
                await Task.WhenAll(tasks);

                // This PC itself doesn't answer its own ARP.
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    var mac = ni.GetPhysicalAddress().GetAddressBytes();
                    if (mac.Length != 6) continue;
                    string m = string.Join(":", mac.Select(x => x.ToString("X2")));
                    try
                    {
                        var v4 = ni.GetIPProperties().UnicastAddresses.FirstOrDefault(u => u.Address.AddressFamily == AddressFamily.InterNetwork);
                        if (v4 != null && subnets.Any(s => s.Local.Equals(v4.Address))) found[m] = v4.Address.ToString();
                    }
                    catch { }
                }

                await Merge(found);
                LastScan = DateTime.UtcNow;
            }
            catch (Exception ex) { Log.Write("Network scan: " + ex.Message); }
            finally
            {
                Interlocked.Exchange(ref _scanning, 0);
                try { ScanFinished?.Invoke(); } catch { }
            }
        }

        private static async Task Merge(Dictionary<string, string> found)
        {
            var known = Db.Devices().ToDictionary(d => d.Mac, StringComparer.OrdinalIgnoreCase);
            bool baseline = !Settings.Current.LanBaselineDone;
            long now = Format.NowUnix;
            var fresh = new List<DeviceRecord>();

            foreach (var kv in found)
            {
                if (known.TryGetValue(kv.Key, out var d))
                {
                    d.Ip = kv.Value;
                    d.LastSeen = now;
                }
                else
                {
                    d = new DeviceRecord { Mac = kv.Key, Ip = kv.Value, FirstSeen = now, LastSeen = now, Trusted = baseline };
                    known[kv.Key] = d;
                    fresh.Add(d);
                }
                if (string.IsNullOrEmpty(d.Hostname)) d.Hostname = await ReverseName(kv.Value);
                if (string.IsNullOrEmpty(d.Vendor)) d.Vendor = await Vendor(kv.Key);
                Db.SaveDevice(d);
            }
            OnlineNow = new HashSet<string>(found.Keys, StringComparer.OrdinalIgnoreCase);

            if (baseline)
            {
                // The first scan learns what's already there, so it doesn't alert about every device at once.
                Settings.Current.LanBaselineDone = true;
                Settings.Save();
                return;
            }
            foreach (var d in fresh)
            {
                try { NewDevice?.Invoke(d); } catch { }
            }
        }

        private static async Task<string> ReverseName(string ip)
        {
            try
            {
                var lookup = Dns.GetHostEntryAsync(ip);
                if (await Task.WhenAny(lookup, Task.Delay(2000)) == lookup && lookup.Status == TaskStatus.RanToCompletion)
                {
                    string h = lookup.Result.HostName;
                    if (!string.IsNullOrEmpty(h) && h != ip) return h.Split('.')[0];
                }
            }
            catch { }
            return null;
        }

        private static DateTime _lastVendorCall = DateTime.MinValue;

        /// <summary>Who made the device, from the first half of its MAC address.</summary>
        private static async Task<string> Vendor(string mac)
        {
            try
            {
                if ((Convert.ToByte(mac.Substring(0, 2), 16) & 0x02) != 0) return "Private address";
                if (!Settings.Current.OnlineLookups || !Settings.Current.VendorLookups) return null;
                var wait = TimeSpan.FromMilliseconds(1100) - (DateTime.UtcNow - _lastVendorCall);
                if (wait > TimeSpan.Zero) await Task.Delay(wait);   // the free service allows about one lookup a second
                _lastVendorCall = DateTime.UtcNow;
                using var resp = await Http.GetAsync("https://api.macvendors.com/" + Uri.EscapeDataString(mac.Substring(0, 8)));
                if (!resp.IsSuccessStatusCode) return null;
                string v = (await resp.Content.ReadAsStringAsync()).Trim();
                return v.Length > 0 && v.Length < 80 && !v.StartsWith("{") ? v : null;
            }
            catch { return null; }
        }
    }
}
