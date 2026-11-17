using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace NetPull.Core
{
    /// <summary>Checks GitHub for a newer NetPull and installs it.</summary>
    public static class Updates
    {
        public const string Repo = "bhxuk/NetPull";
        public static string LatestVersion { get; private set; }
        public static string LatestUrl { get; private set; } = "https://github.com/" + Repo + "/releases/latest";
        public static string LatestSetupUrl { get; private set; }

        public static Version Current => typeof(Updates).Assembly.GetName().Version ?? new Version(1, 0, 0);
        public static string CurrentText => Current.Major + "." + Current.Minor + "." + Math.Max(0, Current.Build);

        public static async Task<bool> Check()
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
                http.DefaultRequestHeaders.UserAgent.ParseAdd("NetPull/" + CurrentText);
                using var doc = JsonDocument.Parse(await http.GetStringAsync("https://api.github.com/repos/" + Repo + "/releases/latest"));
                string tag = doc.RootElement.GetProperty("tag_name").GetString() ?? "";
                LatestUrl = doc.RootElement.GetProperty("html_url").GetString() ?? LatestUrl;
                LatestSetupUrl = null;
                if (doc.RootElement.TryGetProperty("assets", out var assets))
                    foreach (var a in assets.EnumerateArray())
                        if ((a.GetProperty("name").GetString() ?? "").Equals("NetPull-Setup.exe", StringComparison.OrdinalIgnoreCase))
                            LatestSetupUrl = a.GetProperty("browser_download_url").GetString();
                LatestVersion = tag.TrimStart('v', 'V');
                return Version.TryParse(LatestVersion, out var v) && v > new Version(Current.Major, Current.Minor, Math.Max(0, Current.Build));
            }
            catch (Exception ex)
            {
                Log.Write("Update check failed: " + ex.Message);
                return false;
            }
        }

        /// <summary>Downloads the newest Setup.exe and runs it quietly. The installer closes NetPull, updates it and starts it again.</summary>
        public static async Task<bool> DownloadAndInstall(Action<string> status = null)
        {
            try
            {
                if (string.IsNullOrEmpty(LatestSetupUrl) && !await Check()) return false;
                if (string.IsNullOrEmpty(LatestSetupUrl)) return false;
                status?.Invoke("Downloading NetPull " + LatestVersion + "...");
                using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
                http.DefaultRequestHeaders.UserAgent.ParseAdd("NetPull/" + CurrentText);
                var bytes = await http.GetByteArrayAsync(LatestSetupUrl);
                string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "NetPull");
                System.IO.Directory.CreateDirectory(dir);
                string path = System.IO.Path.Combine(dir, "NetPull-Setup-" + LatestVersion + ".exe");
                await System.IO.File.WriteAllBytesAsync(path, bytes);
                status?.Invoke("Installing...");
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path, "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART") { UseShellExecute = true });
                return true;
            }
            catch (Exception ex)
            {
                Log.Write("Update failed: " + ex.Message);
                status?.Invoke("Couldn't update: " + ex.Message);
                return false;
            }
        }
    }
}
