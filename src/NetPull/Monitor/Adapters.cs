using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using NetPull.Core;

namespace NetPull.Monitor
{
    /// <summary>Knows which network adapter (Ethernet, Wi-Fi, Tailscale...) each of this PC's addresses belongs to.</summary>
    public static class Adapters
    {
        private static Dictionary<string, int> _byIp = new Dictionary<string, int>();
        private static HashSet<string> _local = new HashSet<string>();
        private static DateTime _refreshed = DateTime.MinValue;
        private static readonly object Gate = new object();

        public static event Action Changed;

        public static void Start()
        {
            Refresh();
            NetworkChange.NetworkAddressChanged += (s, e) => { Refresh(); Changed?.Invoke(); };
        }

        public static string KindOf(NetworkInterface ni)
        {
            string d = (ni.Description + " " + ni.Name).ToLowerInvariant();
            if (d.Contains("tailscale")) return "Tailscale";
            if (d.Contains("wireguard") || d.Contains("wintun") || d.Contains("openvpn") || d.Contains("tap-windows") || d.Contains("vpn") || ni.NetworkInterfaceType == NetworkInterfaceType.Ppp) return "VPN";
            if (d.Contains("hyper-v") || d.Contains("vethernet") || d.Contains("virtualbox") || d.Contains("vmware") || d.Contains("wsl")) return "Virtual";
            if (ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211) return "Wi-Fi";
            if (ni.NetworkInterfaceType == NetworkInterfaceType.Wwanpp || ni.NetworkInterfaceType == NetworkInterfaceType.Wwanpp2) return "Mobile";
            return "Ethernet";
        }

        public static void Refresh()
        {
            var byIp = new Dictionary<string, int>();
            var local = new HashSet<string>();
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    IPInterfaceProperties props;
                    try { props = ni.GetIPProperties(); } catch { continue; }
                    var addrs = props.UnicastAddresses.Select(u => u.Address).ToList();
                    if (addrs.Count == 0) continue;
                    int id = Db.GetOrCreateAdapter(ni.Id, ni.Name, ni.Description, KindOf(ni));
                    foreach (var a in addrs)
                    {
                        string s = Normal(a);
                        byIp[s] = id;
                        local.Add(s);
                    }
                }
            }
            catch (Exception ex) { Log.Write("Adapter refresh: " + ex.Message); }
            lock (Gate)
            {
                _byIp = byIp;
                _local = local;
                _refreshed = DateTime.UtcNow;
            }
        }

        public static string Normal(IPAddress a)
        {
            if (a == null) return "";
            if (a.IsIPv4MappedToIPv6) a = a.MapToIPv4();
            if (a.AddressFamily == AddressFamily.InterNetworkV6 && a.ScopeId != 0) a = new IPAddress(a.GetAddressBytes());
            return a.ToString();
        }

        /// <summary>The adapter id for one of this PC's addresses, or 0 if it isn't known.</summary>
        public static int ForLocal(string ip)
        {
            if (DateTime.UtcNow - _refreshed > TimeSpan.FromSeconds(60)) Refresh();
            lock (Gate) return _byIp.TryGetValue(ip, out int id) ? id : 0;
        }

        public static bool IsLocal(string ip)
        {
            lock (Gate) return _local.Contains(ip);
        }

        /// <summary>Adapters that are connected now, for the filter list.</summary>
        public static List<AdapterRecord> Active()
        {
            var ids = new HashSet<int>();
            lock (Gate) foreach (var v in _byIp.Values) ids.Add(v);
            return Db.AllAdapters().Where(a => ids.Contains(a.Id)).OrderBy(a => a.Name).ToList();
        }
    }
}
