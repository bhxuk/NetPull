using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace NetPull.Monitor
{
    /// <summary>Checks a handful of common ports on a local device, to tell what it is. Connect-only, nothing is sent.</summary>
    public static class PortScan
    {
        public static readonly Dictionary<int, string> Services = new Dictionary<int, string>
        {
            [21] = "FTP", [22] = "SSH", [23] = "Telnet", [53] = "DNS", [80] = "Web page", [139] = "NetBIOS", [443] = "Web page (HTTPS)",
            [445] = "Windows file sharing", [548] = "Apple file sharing", [554] = "Camera stream (RTSP)", [631] = "Printer (IPP)",
            [1883] = "MQTT (smart home)", [3000] = "Web app", [3100] = "Web app", [3389] = "Remote Desktop", [5000] = "Web app / NAS",
            [5001] = "Web app / NAS (HTTPS)", [5900] = "VNC remote control", [7000] = "AirPlay", [8000] = "Web app", [8008] = "Chromecast",
            [8009] = "Chromecast", [8080] = "Web app", [8123] = "Home Assistant", [8443] = "Web app (HTTPS)", [8554] = "Camera stream (RTSP)",
            [8888] = "Web app", [9000] = "Web app", [9100] = "Printer", [9443] = "Portainer", [32400] = "Plex", [62078] = "iPhone / iPad"
        };

        public static async Task<List<int>> Scan(string ip, int timeoutMs = 500)
        {
            var open = new List<int>();
            if (string.IsNullOrEmpty(ip)) return open;
            await Task.WhenAll(Services.Keys.Select(async port =>
            {
                try
                {
                    using var c = new TcpClient();
                    var connect = c.ConnectAsync(ip, port);
                    if (await Task.WhenAny(connect, Task.Delay(timeoutMs)) == connect && c.Connected)
                        lock (open) open.Add(port);
                }
                catch { }
            }));
            open.Sort();
            return open;
        }

        public static List<int> Parse(string ports) =>
            string.IsNullOrEmpty(ports) ? new List<int>() : ports.Split(',').Select(p => int.TryParse(p, out int n) ? n : 0).Where(n => n > 0).ToList();

        public static string Describe(int port) => Services.TryGetValue(port, out var s) ? s : "Port " + port;

        /// <summary>A best guess at what kind of device this is.</summary>
        public static string GuessKind(string name, string vendor, IEnumerable<int> ports)
        {
            string n = ((name ?? "") + " " + (vendor ?? "")).ToLowerInvariant();
            var p = new HashSet<int>(ports ?? Enumerable.Empty<int>());
            bool Has(params string[] words) => words.Any(w => n.Contains(w));
            if (p.Contains(554) || p.Contains(8554) || Has("camera", "cam", "hikvision", "reolink", "dahua", "ezviz", "aqara", "wyze", "ring", "arlo", "blink")) return "camera";
            if (p.Contains(9100) || p.Contains(631) || Has("printer", "epson", "brother", "canon", "hp inc", "hewlett", "kyocera")) return "printer";
            if (p.Contains(62078) || Has("iphone", "ipad", "android", "galaxy", "pixel", "oneplus", "xiaomi", "redmi", "oppo", "huawei", "phone")) return "phone";
            if (p.Contains(8008) || p.Contains(8009) || p.Contains(7000) || Has("tv", "bravia", "roku", "fire", "chromecast", "apple tv", "shield", "sonos", "echo", "lg electronics", "vizio", "hisense", "tcl")) return "tv";
            if (p.Contains(32400) || p.Contains(8123) || p.Contains(9443) || Has("server", "nas", "synology", "qnap", "raspberry", "proxmox", "ubuntu", "unraid")) return "server";
            if (p.Contains(1883) || Has("espressif", "tuya", "shelly", "sonoff", "tapo", "tp-link", "philips", "hue", "tado", "nest", "google home", "meross", "ikea", "smart")) return "smart";
            if (p.Contains(3389) || p.Contains(445) || Has("desktop", "laptop", "pc", "intel", "dell", "lenovo", "asus", "micro-star", "gigabyte", "macbook", "imac")) return "computer";
            if (p.Contains(22)) return "server";
            if (p.Contains(80) || p.Contains(443)) return "web";
            return "device";
        }
    }
}
