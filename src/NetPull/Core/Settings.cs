using System;
using System.IO;
using System.Text.Json;

namespace NetPull.Core
{
    public static class Paths
    {
        public static string Data
        {
            get
            {
                string p = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NetPull");
                Directory.CreateDirectory(p);
                return p;
            }
        }

        public static string SettingsFile => Path.Combine(Data, "settings.json");
        public static string Database => Path.Combine(Data, "netpull.db");
        public static string LogFile => Path.Combine(Data, "netpull.log");
        public static string ShowSignal => Path.Combine(Data, "show.signal");
    }

    public class AppSettings
    {
        // General
        public bool FirstRun { get; set; } = true;
        public DateTime FirstRunTime { get; set; } = DateTime.MinValue;
        public bool StartWithWindows { get; set; } = true;
        public bool UseBits { get; set; } = false;
        public int KeepDays { get; set; } = 90;
        public bool TrayHintShown { get; set; }

        // Mini graph
        public bool MiniGraph { get; set; } = false;
        public bool MiniGraphOnTop { get; set; } = true;
        public double MiniGraphLeft { get; set; } = double.NaN;
        public double MiniGraphTop { get; set; } = double.NaN;

        // Window
        public double WindowWidth { get; set; } = 1180;
        public double WindowHeight { get; set; } = 760;

        // Which adapter the pages show ("" = all)
        public string AdapterFilter { get; set; } = "";

        // Alerts
        public bool Popups { get; set; } = true;
        public bool AlertNewApp { get; set; } = true;
        public bool AlertAppChanged { get; set; } = true;
        public bool AlertSpike { get; set; } = true;
        public int SpikeMegabytes { get; set; } = 500;
        public int SpikeMinutes { get; set; } = 10;
        public bool AlertNewDevice { get; set; } = true;

        // Data cap (off by default)
        public bool CapEnabled { get; set; } = false;
        public double CapGigabytes { get; set; } = 100;
        public int CapResetDay { get; set; } = 1;
        public int CapWarnPercent { get; set; } = 80;
        public string CapAdapter { get; set; } = "";
        public long CapAlertPeriod { get; set; }
        public bool CapWarned { get; set; }
        public bool CapHit { get; set; }

        // LAN watch
        public bool LanWatch { get; set; } = true;
        public int LanScanMinutes { get; set; } = 5;
        public bool LanBaselineDone { get; set; }

        // Whole network
        public bool PortScanNew { get; set; } = true;
        public bool Tailscale { get; set; } = true;
        public bool AlertDeviceOffline { get; set; } = true;
        public bool AlertDeviceSpike { get; set; } = true;
        public int DeviceSpikeMegabytes { get; set; } = 2000;
        public string GraphSource { get; set; } = "pc";   // "pc" or "network"

        // UniFi (optional, read-only)
        public bool UniFi { get; set; } = false;
        public string UniFiHost { get; set; } = "";
        public string UniFiUser { get; set; } = "";
        public string UniFiPasswordProtected { get; set; } = "";
        public string UniFiSite { get; set; } = "default";
        public bool UniFiBaselineDone { get; set; }

        // Lookups
        public bool OnlineLookups { get; set; } = true;
        public bool VendorLookups { get; set; } = true;

        // Updates
        public bool CheckForUpdates { get; set; } = true;
        public bool AutoUpdate { get; set; } = true;
    }

    public static class Settings
    {
        public static AppSettings Current { get; private set; } = new AppSettings();
        private static readonly object Gate = new object();

        public static void Load()
        {
            try
            {
                if (File.Exists(Paths.SettingsFile))
                    Current = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Paths.SettingsFile)) ?? new AppSettings();
            }
            catch (Exception ex)
            {
                Log.Write("Settings unreadable, starting fresh: " + ex.Message);
                Current = new AppSettings();
            }
            if (Current.FirstRunTime == DateTime.MinValue) Current.FirstRunTime = DateTime.UtcNow;
            if (Current.KeepDays < 7) Current.KeepDays = 90;
            if (Current.LanScanMinutes < 1) Current.LanScanMinutes = 5;
            if (Current.CapResetDay < 1 || Current.CapResetDay > 28) Current.CapResetDay = 1;
        }

        public static void Save()
        {
            lock (Gate)
            {
                try
                {
                    string tmp = Paths.SettingsFile + ".tmp";
                    File.WriteAllText(tmp, JsonSerializer.Serialize(Current, new JsonSerializerOptions { WriteIndented = true }));
                    File.Copy(tmp, Paths.SettingsFile, true);
                    File.Delete(tmp);
                }
                catch (Exception ex) { Log.Write("Couldn't save settings: " + ex.Message); }
            }
        }
    }

    public static class Log
    {
        private static readonly object Gate = new object();

        public static void Write(string text)
        {
            lock (Gate)
            {
                try
                {
                    var fi = new FileInfo(Paths.LogFile);
                    if (fi.Exists && fi.Length > 2_000_000)
                    {
                        File.Copy(Paths.LogFile, Paths.LogFile + ".old", true);
                        File.Delete(Paths.LogFile);
                    }
                    File.AppendAllText(Paths.LogFile, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + text + Environment.NewLine);
                }
                catch { }
            }
        }
    }
}

namespace NetPull.Core
{
    /// <summary>Keeps the UniFi password encrypted with Windows (DPAPI), readable only by this Windows account.</summary>
    public static class Secret
    {
        private static readonly byte[] Salt = System.Text.Encoding.UTF8.GetBytes("NetPull.bhxuk");

        public static string Protect(string plain)
        {
            if (string.IsNullOrEmpty(plain)) return "";
            var bytes = System.Security.Cryptography.ProtectedData.Protect(System.Text.Encoding.UTF8.GetBytes(plain), Salt, System.Security.Cryptography.DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(bytes);
        }

        public static string Unprotect(string stored)
        {
            if (string.IsNullOrEmpty(stored)) return "";
            try
            {
                var bytes = System.Security.Cryptography.ProtectedData.Unprotect(Convert.FromBase64String(stored), Salt, System.Security.Cryptography.DataProtectionScope.CurrentUser);
                return System.Text.Encoding.UTF8.GetString(bytes);
            }
            catch { return ""; }
        }
    }
}
