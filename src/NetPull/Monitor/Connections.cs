using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using NetPull.Core;

namespace NetPull.Monitor
{
    public sealed class ConnectionInfo
    {
        public string Key;
        public int Pid;
        public int AppId;
        public string AppName;
        public string AppPath;
        public bool Udp;
        public string LocalIp;
        public int LocalPort;
        public string RemoteIp;
        public int RemotePort;
        public string State;
        public bool Listening;
        public long RxRate, TxRate, RxTotal, TxTotal;
        public int Adapter;
        public DateTime LastActive;
    }

    /// <summary>Every open connection on this PC, with live speeds from the traffic monitor.</summary>
    public static class Connections
    {
        private static readonly string[] States =
        {
            "", "Closed", "Listening", "Connecting", "Connecting", "Connected", "Closing", "Closing", "Closing", "Closing", "Closing", "Waiting", "Closed"
        };

        private static readonly ConcurrentDictionary<int, (long start, string name, string path)> Names = new ConcurrentDictionary<int, (long, string, string)>();

        private static (string name, string path) NameFor(int pid, int appId)
        {
            if (appId != 0)
            {
                var a = Db.GetApp(appId);
                if (a != null) return (a.Name, a.Path);
            }
            if (pid == 0 || pid == 4) return ("System", null);
            long st = Native.ProcessStartTime(pid);
            if (Names.TryGetValue(pid, out var hit) && hit.start == st) return (hit.name, hit.path);
            string path = Native.ProcessPath(pid, out st);
            string name = path == null ? "Process " + pid : AppCatalog.FriendlyName(path);
            Names[pid] = (st, name, path);
            return (name, path);
        }

        private static string FlowId(int pid, bool udp, string rip, int rport, int lport) => pid + "|" + (udp ? "u" : "t") + "|" + rip + "|" + rport + "|" + lport;

        public static List<ConnectionInfo> Snapshot(bool includeListening, bool activeOnly)
        {
            var result = new List<ConnectionInfo>();
            var flows = new Dictionary<string, FlowStat>();
            foreach (var f in TrafficMonitor.Instance.Flows())
                flows[FlowId(f.Key.Pid, f.Key.Udp, f.Key.RemoteIp, f.Key.RemotePort, f.Key.LocalPort)] = f;
            var used = new HashSet<string>();
            var now = DateTime.UtcNow;

            foreach (var row in TcpRows())
            {
                bool listening = row.state == 2;
                if (listening && !includeListening) continue;
                if (!listening && (row.remote.StartsWith("127.") || row.remote == "::1" || row.remote == "0.0.0.0")) continue;

                FlowStat f = null;
                string id = FlowId(row.pid, false, row.remote, row.rport, row.lport);
                if (flows.TryGetValue(id, out f)) used.Add(id);
                else
                {
                    // Some Windows versions report ports the other way round in their events.
                    string alt = FlowId(row.pid, false, row.remote, Swap(row.rport), Swap(row.lport));
                    if (flows.TryGetValue(alt, out f)) used.Add(alt);
                }
                if (activeOnly && (f == null || now - f.LastActive > TimeSpan.FromSeconds(60))) continue;

                var (name, path) = NameFor(row.pid, f?.AppId ?? 0);
                result.Add(new ConnectionInfo
                {
                    Key = "t|" + row.pid + "|" + row.local + "|" + row.lport + "|" + row.remote + "|" + row.rport,
                    Pid = row.pid, AppId = f?.AppId ?? 0, AppName = name, AppPath = path, Udp = false,
                    LocalIp = row.local, LocalPort = row.lport, RemoteIp = listening ? "" : row.remote, RemotePort = listening ? 0 : row.rport,
                    State = row.state < States.Length ? States[row.state] : "", Listening = listening,
                    RxRate = f?.RxRate ?? 0, TxRate = f?.TxRate ?? 0, RxTotal = f?.RxTotal ?? 0, TxTotal = f?.TxTotal ?? 0,
                    Adapter = f?.Adapter ?? Adapters.ForLocal(row.local), LastActive = f?.LastActive ?? DateTime.MinValue
                });
            }

            // UDP has no connections, so show the conversations seen in the last two minutes (and recently closed TCP ones).
            foreach (var kv in flows)
            {
                if (used.Contains(kv.Key)) continue;
                var f = kv.Value;
                if (now - f.LastActive > TimeSpan.FromSeconds(activeOnly ? 60 : 120)) continue;
                var (name, path) = NameFor(f.Key.Pid, f.AppId);
                result.Add(new ConnectionInfo
                {
                    Key = (f.Key.Udp ? "u|" : "c|") + f.Key.Pid + "|" + f.Key.LocalIp + "|" + f.Key.LocalPort + "|" + f.Key.RemoteIp + "|" + f.Key.RemotePort,
                    Pid = f.Key.Pid, AppId = f.AppId, AppName = name, AppPath = path, Udp = f.Key.Udp,
                    LocalIp = f.Key.LocalIp, LocalPort = f.Key.LocalPort, RemoteIp = f.Key.RemoteIp, RemotePort = f.Key.RemotePort,
                    State = f.Key.Udp ? "UDP" : "Closed",
                    RxRate = f.RxRate, TxRate = f.TxRate, RxTotal = f.RxTotal, TxTotal = f.TxTotal, Adapter = f.Adapter, LastActive = f.LastActive
                });
            }

            if (includeListening)
                foreach (var u in UdpListeners())
                {
                    var (name, path) = NameFor(u.pid, 0);
                    result.Add(new ConnectionInfo
                    {
                        Key = "ul|" + u.pid + "|" + u.local + "|" + u.lport, Pid = u.pid, AppName = name, AppPath = path, Udp = true,
                        LocalIp = u.local, LocalPort = u.lport, RemoteIp = "", State = "Listening", Listening = true, Adapter = Adapters.ForLocal(u.local)
                    });
                }
            return result;
        }

        private static int Swap(int p) => ((p & 0xFF) << 8) | ((p >> 8) & 0xFF);

        private static List<(int pid, int state, string local, int lport, string remote, int rport)> TcpRows()
        {
            var list = new List<(int, int, string, int, string, int)>();
            // IPv4: MIB_TCPROW_OWNER_PID is 6 DWORDs.
            ReadTable(true, Native.AF_INET, (p, n) =>
            {
                for (int i = 0; i < n; i++)
                {
                    IntPtr r = p + 4 + i * 24;
                    int state = Marshal.ReadInt32(r);
                    string local = new IPAddress((uint)Marshal.ReadInt32(r, 4)).ToString();
                    int lport = Native.Port((uint)Marshal.ReadInt32(r, 8));
                    string remote = new IPAddress((uint)Marshal.ReadInt32(r, 12)).ToString();
                    int rport = Native.Port((uint)Marshal.ReadInt32(r, 16));
                    int pid = Marshal.ReadInt32(r, 20);
                    list.Add((pid, state, local, lport, remote, rport));
                }
            });
            // IPv6: MIB_TCP6ROW_OWNER_PID is 56 bytes.
            ReadTable(true, Native.AF_INET6, (p, n) =>
            {
                for (int i = 0; i < n; i++)
                {
                    IntPtr r = p + 4 + i * 56;
                    string local = V6(r, 0);
                    int lport = Native.Port((uint)Marshal.ReadInt32(r, 20));
                    string remote = V6(r, 24);
                    int rport = Native.Port((uint)Marshal.ReadInt32(r, 44));
                    int state = Marshal.ReadInt32(r, 48);
                    int pid = Marshal.ReadInt32(r, 52);
                    list.Add((pid, state, local, lport, remote, rport));
                }
            });
            return list;
        }

        private static List<(int pid, string local, int lport)> UdpListeners()
        {
            var list = new List<(int, string, int)>();
            ReadTable(false, Native.AF_INET, (p, n) =>
            {
                for (int i = 0; i < n; i++)
                {
                    IntPtr r = p + 4 + i * 12;
                    list.Add((Marshal.ReadInt32(r, 8), new IPAddress((uint)Marshal.ReadInt32(r)).ToString(), Native.Port((uint)Marshal.ReadInt32(r, 4))));
                }
            });
            ReadTable(false, Native.AF_INET6, (p, n) =>
            {
                for (int i = 0; i < n; i++)
                {
                    IntPtr r = p + 4 + i * 28;
                    list.Add((Marshal.ReadInt32(r, 24), V6(r, 0), Native.Port((uint)Marshal.ReadInt32(r, 20))));
                }
            });
            return list;
        }

        private static string V6(IntPtr p, int offset)
        {
            var b = new byte[16];
            Marshal.Copy(p + offset, b, 0, 16);
            return Adapters.Normal(new IPAddress(b));
        }

        private static void ReadTable(bool tcp, int af, Action<IntPtr, int> read)
        {
            int size = 0;
            if (tcp) Native.GetExtendedTcpTable(IntPtr.Zero, ref size, false, af, Native.TCP_TABLE_OWNER_PID_ALL, 0);
            else Native.GetExtendedUdpTable(IntPtr.Zero, ref size, false, af, Native.UDP_TABLE_OWNER_PID, 0);
            if (size <= 0) return;
            size += 4096;   // room in case the table grows between the two calls
            IntPtr buf = Marshal.AllocHGlobal(size);
            try
            {
                uint rc = tcp
                    ? Native.GetExtendedTcpTable(buf, ref size, false, af, Native.TCP_TABLE_OWNER_PID_ALL, 0)
                    : Native.GetExtendedUdpTable(buf, ref size, false, af, Native.UDP_TABLE_OWNER_PID, 0);
                if (rc != 0) return;
                read(buf, Marshal.ReadInt32(buf));
            }
            catch (Exception ex) { Log.Write("Connection table: " + ex.Message); }
            finally { Marshal.FreeHGlobal(buf); }
        }
    }
}
