using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NetPull.Core;

namespace NetPull.Monitor
{
    public sealed class IpInfo
    {
        public string Ip;
        public string Host;
        public string Country;
        public string CountryCode;
        public string Isp;
        public bool IsPrivate;
        internal bool DnsDone;
        internal bool GeoDone;

        public string Display => !string.IsNullOrEmpty(Host) ? Host : Ip;
        public string Where => IsPrivate ? Country : string.IsNullOrEmpty(Country) ? "" : Country;
    }

    /// <summary>Finds a name (reverse DNS), country and ISP for remote addresses. Country and ISP come from
    /// ip-api.com when online lookups are on. Results are kept for 30 days.</summary>
    public static class IpLookup
    {
        private static readonly ConcurrentDictionary<string, IpInfo> Cache = new ConcurrentDictionary<string, IpInfo>();
        private static readonly ConcurrentQueue<string> DnsQueue = new ConcurrentQueue<string>();
        private static readonly ConcurrentQueue<string> GeoQueue = new ConcurrentQueue<string>();
        private static readonly SemaphoreSlim DnsGate = new SemaphoreSlim(6);
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        private static int _changes;

        /// <summary>Raised (at most every couple of seconds) when new names or countries have arrived.</summary>
        public static event Action Updated;

        public static void Start()
        {
            Http.DefaultRequestHeaders.UserAgent.ParseAdd("NetPull/" + Updates.CurrentText);
            new Thread(DnsLoop) { IsBackground = true, Name = "NetPull DNS" }.Start();
            new Thread(GeoLoop) { IsBackground = true, Name = "NetPull Geo" }.Start();
            new Thread(() =>
            {
                while (true)
                {
                    Thread.Sleep(2000);
                    if (Interlocked.Exchange(ref _changes, 0) > 0) { try { Updated?.Invoke(); } catch { } }
                }
            }) { IsBackground = true }.Start();
        }

        public static IpInfo Get(string ip)
        {
            if (string.IsNullOrEmpty(ip)) return new IpInfo { Ip = ip };
            if (Cache.TryGetValue(ip, out var hit)) return hit;
            Request(ip);
            return Cache.TryGetValue(ip, out hit) ? hit : new IpInfo { Ip = ip };
        }

        public static void Request(string ip)
        {
            if (string.IsNullOrEmpty(ip) || Cache.ContainsKey(ip)) return;
            var info = new IpInfo { Ip = ip };
            if (!Cache.TryAdd(ip, info)) return;

            string label = PrivateLabel(ip);
            if (label != null)
            {
                info.IsPrivate = true;
                info.Country = label;
                info.GeoDone = true;
            }

            try
            {
                var saved = Db.GetIpInfo(ip);
                if (saved != null && Format.NowUnix - saved.Updated < 30L * 86400)
                {
                    info.Host = saved.Host;
                    if (!info.IsPrivate) { info.Country = saved.Country; info.CountryCode = saved.CountryCode; info.Isp = saved.Isp; }
                    info.DnsDone = true;
                    info.GeoDone = info.IsPrivate || !string.IsNullOrEmpty(saved.Country) || !Settings.Current.OnlineLookups;
                }
            }
            catch { }

            if (!info.DnsDone) DnsQueue.Enqueue(ip);
            if (!info.GeoDone && Settings.Current.OnlineLookups) GeoQueue.Enqueue(ip);
        }

        /// <summary>Forgets everything so names and countries are looked up again.</summary>
        public static void ClearCache()
        {
            Cache.Clear();
            try { Db.ClearIpInfo(); } catch { }
        }

        public static string PrivateLabel(string ip)
        {
            if (!IPAddress.TryParse(ip, out var a)) return null;
            if (a.IsIPv4MappedToIPv6) a = a.MapToIPv4();
            if (a.AddressFamily == AddressFamily.InterNetwork)
            {
                var b = a.GetAddressBytes();
                if (b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168)) return "Local network";
                if (b[0] == 100 && b[1] >= 64 && b[1] <= 127) return "Tailscale / private";
                if (b[0] == 169 && b[1] == 254) return "Link-local";
                if (b[0] >= 224 && b[0] <= 239) return "Multicast";
                if (b[0] == 255) return "Broadcast";
                return null;
            }
            if (a.IsIPv6LinkLocal || a.IsIPv6SiteLocal) return "Link-local";
            if (a.IsIPv6Multicast) return "Multicast";
            var v6 = a.GetAddressBytes();
            if ((v6[0] & 0xFE) == 0xFC) return v6[0] == 0xFD && v6[1] == 0x7A ? "Tailscale / private" : "Local network";
            return null;
        }

        private static void Save(IpInfo i)
        {
            try
            {
                Db.PutIpInfo(new IpInfoRecord
                {
                    Ip = i.Ip, Host = i.Host, Country = i.IsPrivate ? null : i.Country, CountryCode = i.CountryCode, Isp = i.Isp, Updated = Format.NowUnix
                });
            }
            catch { }
            Interlocked.Increment(ref _changes);
        }

        private static void DnsLoop()
        {
            while (true)
            {
                if (!DnsQueue.TryDequeue(out string ip)) { Thread.Sleep(300); continue; }
                DnsGate.Wait();
                _ = Task.Run(async () =>
                {
                    try
                    {
                        if (!Cache.TryGetValue(ip, out var info)) return;
                        try
                        {
                            var lookup = Dns.GetHostEntryAsync(ip);
                            if (await Task.WhenAny(lookup, Task.Delay(3000)) == lookup && lookup.Status == TaskStatus.RanToCompletion)
                            {
                                string h = lookup.Result.HostName;
                                if (!string.IsNullOrEmpty(h) && h != ip) info.Host = h.TrimEnd('.');
                            }
                        }
                        catch { }
                        info.DnsDone = true;
                        Save(info);
                    }
                    finally { DnsGate.Release(); }
                });
            }
        }

        private static void GeoLoop()
        {
            while (true)
            {
                Thread.Sleep(4000);   // ip-api.com allows 15 batch requests a minute
                if (!Settings.Current.OnlineLookups) { while (GeoQueue.TryDequeue(out _)) { } continue; }
                var batch = new List<string>();
                while (batch.Count < 100 && GeoQueue.TryDequeue(out string ip)) if (!batch.Contains(ip)) batch.Add(ip);
                if (batch.Count == 0) continue;
                try
                {
                    var body = new StringContent(JsonSerializer.Serialize(batch), Encoding.UTF8, "application/json");
                    using var resp = Http.PostAsync("http://ip-api.com/batch?fields=status,country,countryCode,isp,org,query", body).GetAwaiter().GetResult();
                    if ((int)resp.StatusCode == 429)
                    {
                        foreach (var ip in batch) GeoQueue.Enqueue(ip);
                        Thread.Sleep(60000);
                        continue;
                    }
                    resp.EnsureSuccessStatusCode();
                    using var doc = JsonDocument.Parse(resp.Content.ReadAsStringAsync().GetAwaiter().GetResult());
                    foreach (var e in doc.RootElement.EnumerateArray())
                    {
                        string ip = e.TryGetProperty("query", out var q) ? q.GetString() : null;
                        if (ip == null || !Cache.TryGetValue(ip, out var info)) continue;
                        if (e.TryGetProperty("status", out var st) && st.GetString() == "success")
                        {
                            info.Country = e.TryGetProperty("country", out var c) ? c.GetString() : null;
                            info.CountryCode = e.TryGetProperty("countryCode", out var cc) ? cc.GetString() : null;
                            string isp = e.TryGetProperty("isp", out var i) ? i.GetString() : null;
                            string org = e.TryGetProperty("org", out var o) ? o.GetString() : null;
                            info.Isp = string.IsNullOrEmpty(org) ? isp : org;
                        }
                        info.GeoDone = true;
                        Save(info);
                    }
                    // Slow down when close to the limit.
                    if (resp.Headers.TryGetValues("X-Rl", out var rl) && int.TryParse(rl.FirstOrDefault(), out int left) && left <= 1
                        && resp.Headers.TryGetValues("X-Ttl", out var ttl) && int.TryParse(ttl.FirstOrDefault(), out int wait))
                        Thread.Sleep((wait + 1) * 1000);
                }
                catch (Exception ex)
                {
                    Log.Write("Country lookup: " + ex.Message);
                    foreach (var ip in batch) if (Cache.TryGetValue(ip, out var info)) info.GeoDone = true;
                    Thread.Sleep(30000);
                }
            }
        }
    }
}
