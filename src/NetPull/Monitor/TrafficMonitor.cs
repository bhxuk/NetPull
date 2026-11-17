using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;
using NetPull.Core;

namespace NetPull.Monitor
{
    public struct AppSlice
    {
        public int App;
        public int Adapter;
        public long Rx;
        public long Tx;
    }

    /// <summary>One second of traffic.</summary>
    public sealed class SecondSample
    {
        public long Time;
        public long Rx;
        public long Tx;
        public List<AppSlice> Apps = new List<AppSlice>();

        public (long rx, long tx) For(int adapter)
        {
            if (adapter == 0) return (Rx, Tx);
            long r = 0, t = 0;
            foreach (var a in Apps) if (a.Adapter == adapter) { r += a.Rx; t += a.Tx; }
            return (r, t);
        }
    }

    public readonly record struct FlowKey(int Pid, bool Udp, string LocalIp, int LocalPort, string RemoteIp, int RemotePort);

    /// <summary>Live numbers for one conversation between an app and a remote address.</summary>
    public sealed class FlowStat
    {
        public FlowKey Key;
        public int AppId;
        public int Adapter;
        public long RxRate;
        public long TxRate;
        public long RxTotal;
        public long TxTotal;
        public DateTime FirstSeen;
        public DateTime LastActive;
    }

    /// <summary>Listens to Windows' own network events (ETW) to see how many bytes every app sends and receives.
    /// Needs admin rights. Adds it all up each second, and writes it to the history once a minute.</summary>
    public sealed class TrafficMonitor : IDisposable
    {
        public static TrafficMonitor Instance { get; } = new TrafficMonitor();

        private sealed class Counter { public long Rx; public long Tx; }

        private readonly object _gate = new object();
        private Dictionary<FlowKey, Counter> _pending = new Dictionary<FlowKey, Counter>();
        private TraceEventSession _session;
        private Thread _thread;
        private Timer _timer;
        private volatile bool _stopping;

        public bool IsRunning { get; private set; }
        public string Error { get; private set; }
        public DateTime StartedAt { get; } = DateTime.UtcNow;

        /// <summary>The last hour, one sample per second.</summary>
        private readonly List<SecondSample> _live = new List<SecondSample>();
        private readonly Dictionary<FlowKey, FlowStat> _flows = new Dictionary<FlowKey, FlowStat>();
        private Dictionary<int, (long rx, long tx)> _appRates = new Dictionary<int, (long, long)>();

        private long _minute = -1;
        private readonly Dictionary<(int app, int adapter), Counter> _minuteApps = new Dictionary<(int, int), Counter>();
        private readonly Dictionary<(int app, int adapter, string ip), Counter> _minuteHosts = new Dictionary<(int, int, string), Counter>();

        public long SessionRx { get; private set; }
        public long SessionTx { get; private set; }

        /// <summary>Raised on a background thread once a second.</summary>
        public event Action<SecondSample> Tick;

        /// <summary>Raised on a background thread after a minute has been written to the history (the minute's start, unix seconds).</summary>
        public event Action<long> MinuteSaved;

        public event Action StatusChanged;

        public void Start()
        {
            _stopping = false;
            _thread = new Thread(Listen) { IsBackground = true, Name = "NetPull ETW", Priority = ThreadPriority.AboveNormal };
            _thread.Start();
            _timer = new Timer(_ => OnTick(), null, 1000 - DateTime.UtcNow.Millisecond, 1000);
        }

        private void Listen()
        {
            while (!_stopping)
            {
                try
                {
                    if (!Elevation.IsAdmin)
                    {
                        SetStatus(false, "NetPull needs administrator rights to see which apps use the network.");
                        return;
                    }
                    _session = OpenSession();
                    var k = _session.Source.Kernel;
                    k.TcpIpRecv += d => Add(d.ProcessID, d.saddr, d.sport, d.daddr, d.dport, d.size, true, false);
                    k.TcpIpSend += d => Add(d.ProcessID, d.saddr, d.sport, d.daddr, d.dport, d.size, false, false);
                    k.TcpIpRecvIPV6 += d => Add(d.ProcessID, d.saddr, d.sport, d.daddr, d.dport, d.size, true, false);
                    k.TcpIpSendIPV6 += d => Add(d.ProcessID, d.saddr, d.sport, d.daddr, d.dport, d.size, false, false);
                    k.UdpIpRecv += d => Add(d.ProcessID, d.saddr, d.sport, d.daddr, d.dport, d.size, true, true);
                    k.UdpIpSend += d => Add(d.ProcessID, d.saddr, d.sport, d.daddr, d.dport, d.size, false, true);
                    k.UdpIpRecvIPV6 += d => Add(d.ProcessID, d.saddr, d.sport, d.daddr, d.dport, d.size, true, true);
                    k.UdpIpSendIPV6 += d => Add(d.ProcessID, d.saddr, d.sport, d.daddr, d.dport, d.size, false, true);
                    SetStatus(true, null);
                    _session.Source.Process();   // runs until the session stops
                }
                catch (Exception ex)
                {
                    Log.Write("Capture stopped: " + ex);
                    SetStatus(false, "Couldn't watch network traffic: " + ex.Message);
                }
                finally
                {
                    try { _session?.Dispose(); } catch { }
                    _session = null;
                }
                if (_stopping) break;
                SetStatus(false, "Reconnecting to Windows network events...");
                Thread.Sleep(5000);
            }
        }

        private static TraceEventSession OpenSession()
        {
            // Windows 8 and later allow a kernel session with its own name, so NetPull doesn't clash with other tools.
            try
            {
                var s = new TraceEventSession("NetPull Network Trace") { StopOnDispose = true, BufferSizeMB = 32 };
                s.EnableKernelProvider(KernelTraceEventParser.Keywords.NetworkTCPIP);
                return s;
            }
            catch (Exception ex)
            {
                Log.Write("Named kernel session failed, using the shared one: " + ex.Message);
                var s = new TraceEventSession(KernelTraceEventParser.KernelSessionName) { StopOnDispose = true, BufferSizeMB = 32 };
                s.EnableKernelProvider(KernelTraceEventParser.Keywords.NetworkTCPIP);
                return s;
            }
        }

        private void SetStatus(bool running, string error)
        {
            IsRunning = running;
            Error = error;
            try { StatusChanged?.Invoke(); } catch { }
        }

        private void Add(int pid, IPAddress saddr, int sport, IPAddress daddr, int dport, int size, bool recv, bool udp)
        {
            if (size <= 0) return;
            string s = Adapters.Normal(saddr), d = Adapters.Normal(daddr);
            bool sLocal = Adapters.IsLocal(s), dLocal = Adapters.IsLocal(d);
            if (sLocal && dLocal) return;   // this PC talking to itself
            string local, remote; int lport, rport;
            if (!sLocal && dLocal) { local = d; lport = dport; remote = s; rport = sport; }
            else { local = s; lport = sport; remote = d; rport = dport; }
            if (IsLoopback(remote) || remote == "0.0.0.0" || remote == "::") return;
            if (pid < 0) pid = 4;

            var key = new FlowKey(pid, udp, local, lport, remote, rport);
            lock (_gate)
            {
                if (!_pending.TryGetValue(key, out var c)) _pending[key] = c = new Counter();
                if (recv) c.Rx += size; else c.Tx += size;
            }
        }

        private static bool IsLoopback(string ip) => ip.StartsWith("127.") || ip == "::1";

        private int _busy;

        private void OnTick()
        {
            if (Interlocked.Exchange(ref _busy, 1) == 1) return;   // the last tick is still running
            try
            {
                Dictionary<FlowKey, Counter> batch;
                lock (_gate)
                {
                    batch = _pending;
                    _pending = new Dictionary<FlowKey, Counter>();
                }

                long now = Format.NowUnix;
                var sample = new SecondSample { Time = now };
                var slices = new Dictionary<(int, int), AppSlice>();
                var rates = new Dictionary<int, (long, long)>();
                var nowUtc = DateTime.UtcNow;

                long minute = now - now % 60;
                bool saved = false; long savedMinute = 0;

                lock (_flows)
                {
                    if (_minute != -1 && minute != _minute) { savedMinute = _minute; FlushMinute(); saved = true; }
                    _minute = minute;

                    foreach (var f in _flows.Values) { f.RxRate = 0; f.TxRate = 0; }
                    foreach (var kv in batch)
                    {
                        var key = kv.Key; var c = kv.Value;
                        var app = AppCatalog.Resolve(key.Pid);
                        int adapter = Adapters.ForLocal(key.LocalIp);

                        sample.Rx += c.Rx; sample.Tx += c.Tx;
                        var sk = (app.Id, adapter);
                        slices.TryGetValue(sk, out var sl);
                        sl.App = app.Id; sl.Adapter = adapter; sl.Rx += c.Rx; sl.Tx += c.Tx;
                        slices[sk] = sl;

                        rates.TryGetValue(app.Id, out var r);
                        rates[app.Id] = (r.Item1 + c.Rx, r.Item2 + c.Tx);

                        if (!_flows.TryGetValue(key, out var f))
                        {
                            _flows[key] = f = new FlowStat { Key = key, FirstSeen = nowUtc };
                            IpLookup.Request(key.RemoteIp);
                        }
                        f.AppId = app.Id; f.Adapter = adapter;
                        f.RxRate = c.Rx; f.TxRate = c.Tx;
                        f.RxTotal += c.Rx; f.TxTotal += c.Tx;
                        f.LastActive = nowUtc;

                        if (!_minuteApps.TryGetValue(sk, out var ma)) _minuteApps[sk] = ma = new Counter();
                        ma.Rx += c.Rx; ma.Tx += c.Tx;
                        var hk = (app.Id, adapter, key.RemoteIp);
                        if (!_minuteHosts.TryGetValue(hk, out var mh)) _minuteHosts[hk] = mh = new Counter();
                        mh.Rx += c.Rx; mh.Tx += c.Tx;
                    }

                    // Forget conversations that have been quiet for 10 minutes.
                    if (now % 30 == 0)
                        foreach (var k in _flows.Where(x => nowUtc - x.Value.LastActive > TimeSpan.FromMinutes(10)).Select(x => x.Key).ToList())
                            _flows.Remove(k);
                }

                sample.Apps = slices.Values.ToList();
                SessionRx += sample.Rx; SessionTx += sample.Tx;
                lock (_live)
                {
                    _live.Add(sample);
                    if (_live.Count > 3600) _live.RemoveRange(0, _live.Count - 3600);
                }
                _appRates = rates;
                Tick?.Invoke(sample);
                if (saved) { try { MinuteSaved?.Invoke(savedMinute); } catch (Exception ex) { Log.Write("MinuteSaved: " + ex.Message); } }
            }
            catch (Exception ex) { Log.Write("Tick: " + ex); }
            finally { Interlocked.Exchange(ref _busy, 0); }
        }

        private void FlushMinute()
        {
            long minute = _minute;
            var rows = _minuteApps.Select(kv => new Db.MinuteRow { App = kv.Key.app, Adapter = kv.Key.adapter, Rx = kv.Value.Rx, Tx = kv.Value.Tx }).ToList();
            var hosts = _minuteHosts.Select(kv => new Db.HostRow { App = kv.Key.app, Adapter = kv.Key.adapter, Ip = kv.Key.ip, Rx = kv.Value.Rx, Tx = kv.Value.Tx }).ToList();
            var seen = _minuteApps.Keys.Select(k => k.app).Distinct().ToList();
            _minuteApps.Clear();
            _minuteHosts.Clear();
            try { Db.WriteMinute(minute, rows, hosts, seen); }
            catch (Exception ex) { Log.Write("Couldn't save a minute of history: " + ex.Message); }
        }

        /// <summary>Totals for the minute that hasn't been written to the history yet.</summary>
        public Dictionary<int, Totals> PendingAppTotals(int adapter)
        {
            var d = new Dictionary<int, Totals>();
            List<KeyValuePair<(int app, int adapter), Counter>> copy;
            lock (_flows) copy = _minuteApps.Select(kv => new KeyValuePair<(int app, int adapter), Counter>(kv.Key, new Counter { Rx = kv.Value.Rx, Tx = kv.Value.Tx })).ToList();
            foreach (var kv in copy)
            {
                if (adapter != 0 && kv.Key.adapter != adapter) continue;
                d.TryGetValue(kv.Key.app, out var t);
                t.Rx += kv.Value.Rx; t.Tx += kv.Value.Tx;
                d[kv.Key.app] = t;
            }
            return d;
        }

        public List<SecondSample> LiveSamples(int seconds)
        {
            lock (_live)
            {
                int n = Math.Min(seconds, _live.Count);
                return _live.GetRange(_live.Count - n, n);
            }
        }

        public List<FlowStat> Flows()
        {
            lock (_flows)
                return _flows.Values.Select(f => new FlowStat
                {
                    Key = f.Key, AppId = f.AppId, Adapter = f.Adapter, RxRate = f.RxRate, TxRate = f.TxRate,
                    RxTotal = f.RxTotal, TxTotal = f.TxTotal, FirstSeen = f.FirstSeen, LastActive = f.LastActive
                }).ToList();
        }

        public Dictionary<int, (long rx, long tx)> AppRates => _appRates;

        public void Dispose()
        {
            _stopping = true;
            try { _timer?.Dispose(); } catch { }
            try { lock (_flows) if (_minute != -1) FlushMinute(); } catch { }
            try { _session?.Dispose(); } catch { }
        }
    }
}
