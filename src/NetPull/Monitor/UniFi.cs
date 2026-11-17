using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using NetPull.Core;

namespace NetPull.Monitor
{
    /// <summary>A client the UniFi gateway knows about, with its traffic counters.</summary>
    public sealed class UniClient
    {
        public string Mac;
        public string Ip;
        public string Name;
        public string Hostname;
        public string Vendor;
        public bool Wired;
        public string UplinkMac;     // switch or AP it's on
        public int SwitchPort;
        public string Ssid;
        public string Band;
        public int Signal;           // dBm, 0 if wired
        public long Uptime;          // seconds
        public long DownBytes;       // from the network to the device
        public long UpBytes;
        public long DownRate;
        public long UpRate;
        public bool Guest;
    }

    /// <summary>A UniFi gateway, switch or access point.</summary>
    public sealed class UniDevice
    {
        public string Mac;
        public string Ip;
        public string Name;
        public string Model;
        public string Type;          // gateway, switch, ap
        public string UplinkMac;
        public int UplinkPort;
        public bool Online;
        public long Uptime;
        public long WanDownRate;
        public long WanUpRate;
    }

    /// <summary>Reads the local UniFi Network API (UniFi OS consoles like the Cloud Gateway Ultra) with a read-only account.</summary>
    public sealed class UniFiClient : IDisposable
    {
        private readonly HttpClient _http;
        private readonly CookieContainer _cookies = new CookieContainer();
        private readonly string _base;
        private readonly string _user, _pass, _site;
        private string _csrf;
        private bool _loggedIn;

        public UniFiClient(string host, string user, string pass, string site)
        {
            host = (host ?? "").Trim().TrimEnd('/');
            if (!host.StartsWith("http", StringComparison.OrdinalIgnoreCase)) host = "https://" + host;
            _base = host;
            _user = user; _pass = pass; _site = string.IsNullOrWhiteSpace(site) ? "default" : site.Trim();
            string expectedHost = new Uri(_base).Host;
            var handler = new HttpClientHandler
            {
                CookieContainer = _cookies,
                UseCookies = true,
                // UniFi consoles use their own certificate. Only accept that for the console's own address.
                ServerCertificateCustomValidationCallback = (req, cert, chain, errors) =>
                    errors == SslPolicyErrors.None || string.Equals(req.RequestUri?.Host, expectedHost, StringComparison.OrdinalIgnoreCase)
            };
            _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(12) };
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("NetPull/" + Updates.CurrentText);
        }

        public async Task Login()
        {
            var body = new StringContent(JsonSerializer.Serialize(new { username = _user, password = _pass, rememberMe = true }), Encoding.UTF8, "application/json");
            using var resp = await _http.PostAsync(_base + "/api/auth/login", body);
            if (resp.StatusCode == HttpStatusCode.Unauthorized || resp.StatusCode == HttpStatusCode.Forbidden)
                throw new Exception("UniFi didn't accept the username or password.");
            if ((int)resp.StatusCode == 429) throw new Exception("UniFi is limiting sign-ins. Wait a few minutes and try again.");
            if (!resp.IsSuccessStatusCode) throw new Exception("UniFi sign-in failed (" + (int)resp.StatusCode + "). Check the address is your UniFi console.");
            if (resp.Headers.TryGetValues("X-CSRF-Token", out var t) || resp.Headers.TryGetValues("X-Updated-CSRF-Token", out t)) _csrf = t.FirstOrDefault();
            _loggedIn = true;
        }

        private async Task<JsonElement> Call(string path, object postBody = null, bool retry = true)
        {
            if (!_loggedIn) await Login();
            var req = new HttpRequestMessage(postBody == null ? HttpMethod.Get : HttpMethod.Post, _base + "/proxy/network/api/s/" + Uri.EscapeDataString(_site) + path);
            if (postBody != null) req.Content = new StringContent(JsonSerializer.Serialize(postBody), Encoding.UTF8, "application/json");
            if (_csrf != null) req.Headers.TryAddWithoutValidation("X-CSRF-Token", _csrf);
            using var resp = await _http.SendAsync(req);
            if ((resp.StatusCode == HttpStatusCode.Unauthorized || resp.StatusCode == HttpStatusCode.Forbidden) && retry)
            {
                _loggedIn = false;
                return await Call(path, postBody, false);
            }
            if (resp.Headers.TryGetValues("X-Updated-CSRF-Token", out var t)) _csrf = t.FirstOrDefault() ?? _csrf;
            if (!resp.IsSuccessStatusCode) throw new Exception("UniFi said " + (int)resp.StatusCode + " for " + path + (resp.StatusCode == HttpStatusCode.NotFound ? " (check the site name)" : ""));
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            return doc.RootElement.TryGetProperty("data", out var data) ? data.Clone() : default;
        }

        private static string S(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        private static long L(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out long x) ? x
            : e.TryGetProperty(name, out v) && v.ValueKind == JsonValueKind.Number ? (long)v.GetDouble() : 0;
        private static bool B(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
        public static string Mac(string m) => string.IsNullOrEmpty(m) ? null : m.Trim().ToUpperInvariant().Replace('-', ':');

        public async Task<List<UniClient>> Clients()
        {
            var list = new List<UniClient>();
            var data = await Call("/stat/sta");
            if (data.ValueKind != JsonValueKind.Array) return list;
            foreach (var c in data.EnumerateArray())
            {
                bool wired = B(c, "is_wired");
                // For clients, "tx" is what the network sent to the device: its download.
                long down = L(c, "tx_bytes"), up = L(c, "rx_bytes");
                if (wired && down == 0 && up == 0) { down = L(c, "wired-tx_bytes"); up = L(c, "wired-rx_bytes"); }
                long downR = L(c, "tx_bytes-r"), upR = L(c, "rx_bytes-r");
                if (wired && downR == 0 && upR == 0) { downR = L(c, "wired-tx_bytes-r"); upR = L(c, "wired-rx_bytes-r"); }
                string radio = S(c, "radio") ?? "";
                list.Add(new UniClient
                {
                    Mac = Mac(S(c, "mac")),
                    Ip = S(c, "ip") ?? S(c, "last_ip"),
                    Name = S(c, "name"),
                    Hostname = S(c, "hostname"),
                    Vendor = S(c, "oui"),
                    Wired = wired,
                    UplinkMac = Mac(wired ? (S(c, "sw_mac") ?? S(c, "last_uplink_mac")) : (S(c, "ap_mac") ?? S(c, "last_uplink_mac"))),
                    SwitchPort = (int)L(c, "sw_port"),
                    Ssid = S(c, "essid"),
                    Band = radio == "ng" ? "2.4 GHz" : radio == "na" ? "5 GHz" : radio == "6e" ? "6 GHz" : null,
                    Signal = (int)L(c, "signal"),
                    Uptime = L(c, "uptime"),
                    DownBytes = down, UpBytes = up, DownRate = downR, UpRate = upR,
                    Guest = B(c, "is_guest")
                });
            }
            return list.Where(c => c.Mac != null).ToList();
        }

        public async Task<List<UniDevice>> Devices()
        {
            var list = new List<UniDevice>();
            var data = await Call("/stat/device");
            if (data.ValueKind != JsonValueKind.Array) return list;
            foreach (var d in data.EnumerateArray())
            {
                string t = (S(d, "type") ?? "").ToLowerInvariant();
                string type = t == "uap" ? "ap" : t == "usw" ? "switch" : (t == "ugw" || t == "udm" || t == "uxg" || t == "ucg" || d.TryGetProperty("wan1", out _)) ? "gateway" : "switch";
                var dev = new UniDevice
                {
                    Mac = Mac(S(d, "mac")),
                    Ip = S(d, "ip"),
                    Name = S(d, "name") ?? S(d, "model"),
                    Model = S(d, "model"),
                    Type = type,
                    Online = L(d, "state") == 1,
                    Uptime = L(d, "uptime")
                };
                if (d.TryGetProperty("uplink", out var up) && up.ValueKind == JsonValueKind.Object)
                {
                    dev.UplinkMac = Mac(S(up, "uplink_mac"));
                    dev.UplinkPort = (int)L(up, "uplink_remote_port");
                }
                if (type == "gateway" && d.TryGetProperty("wan1", out var wan) && wan.ValueKind == JsonValueKind.Object)
                {
                    dev.WanDownRate = L(wan, "rx_bytes-r");
                    dev.WanUpRate = L(wan, "tx_bytes-r");
                }
                if (dev.Mac != null) list.Add(dev);
            }
            return list;
        }

        /// <summary>Traffic per app category for each client (needs Traffic Identification switched on in UniFi).</summary>
        public async Task<Dictionary<string, List<(string category, long down, long up)>>> Dpi()
        {
            var result = new Dictionary<string, List<(string, long, long)>>(StringComparer.OrdinalIgnoreCase);
            var data = await Call("/stat/stadpi", new { type = "by_cat" });
            if (data.ValueKind != JsonValueKind.Array) return result;
            foreach (var c in data.EnumerateArray())
            {
                string mac = Mac(S(c, "mac"));
                if (mac == null || !c.TryGetProperty("by_cat", out var cats) || cats.ValueKind != JsonValueKind.Array) continue;
                var list = new List<(string, long, long)>();
                foreach (var cat in cats.EnumerateArray())
                    list.Add((Category((int)L(cat, "cat")), L(cat, "tx_bytes"), L(cat, "rx_bytes")));
                result[mac] = list.GroupBy(x => x.Item1).Select(g => (g.Key, g.Sum(x => x.Item2), g.Sum(x => x.Item3))).OrderByDescending(x => x.Item2 + x.Item3).ToList();
            }
            return result;
        }

        public static string Category(int id) => id switch
        {
            0 => "Messaging",
            1 => "Peer to peer",
            3 => "File transfer",
            4 => "Streaming",
            5 => "Mail and work apps",
            6 => "Voice and video calls",
            7 => "Databases",
            8 => "Gaming",
            9 => "Network management",
            10 => "Remote access",
            11 => "Proxies and VPNs",
            12 => "Finance",
            13 => "Web",
            14 => "Updates",
            15 => "Web chat",
            17 => "Business",
            18 or 19 or 20 => "Network services",
            23 => "Private protocols",
            24 => "Social media",
            _ => "Other"
        };

        public void Dispose() => _http.Dispose();
    }
}
