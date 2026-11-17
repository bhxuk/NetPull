using System;
using System.Collections.Generic;
using NetPull.Core;

namespace NetPull.Monitor
{
    public static class AlertKinds
    {
        public const string NewApp = "new-app";
        public const string AppChanged = "app-changed";
        public const string Spike = "spike";
        public const string DataCap = "data-cap";
        public const string NewDevice = "new-device";
        public const string DeviceOffline = "device-offline";
        public const string DeviceOnline = "device-online";
        public const string DeviceSpike = "device-spike";
        public const string DeviceCap = "device-cap";
    }

    /// <summary>Turns what the monitor sees into alerts: new apps, changed apps, big downloads, the data cap and new devices.</summary>
    public static class AlertEngine
    {
        /// <summary>Raised (on any thread) when an alert is added.</summary>
        public static event Action<AlertRecord> Raised;

        private static readonly Dictionary<int, DateTime> SpikeCooldown = new Dictionary<int, DateTime>();

        /// <summary>For the first few minutes NetPull quietly learns the apps you already use.</summary>
        public static bool Learning => DateTime.UtcNow - Settings.Current.FirstRunTime < TimeSpan.FromMinutes(10);

        public static void Start()
        {
            AppCatalog.NewApp += a =>
            {
                if (!Settings.Current.AlertNewApp || Learning || a.Key == AppCatalog.UnknownKey) return;
                string where = string.IsNullOrEmpty(a.Path) ? "" : a.Path;
                Add(AlertKinds.NewApp, a.Name + " went online for the first time", where, a.Id);
            };
            AppCatalog.AppChanged += (a, oldV, newV) =>
            {
                if (!Settings.Current.AlertAppChanged) return;
                string body = (oldV ?? "") != (newV ?? "") && !string.IsNullOrEmpty(newV)
                    ? "Version " + (string.IsNullOrEmpty(oldV) ? "?" : oldV) + " → " + newV + ". Usually an update. " + a.Path
                    : "The file is different from the last time it went online. " + a.Path;
                Add(AlertKinds.AppChanged, a.Name + " has changed", body, a.Id);
            };
            LanWatch.NewDevice += d =>
            {
                if (!Settings.Current.AlertNewDevice) return;
                string what = !string.IsNullOrEmpty(d.Hostname) ? d.Hostname : !string.IsNullOrEmpty(d.Vendor) ? d.Vendor + " device" : "An unknown device";
                Add(AlertKinds.NewDevice, "New device on your network", what + " at " + d.Ip + " (" + d.Mac + ")", 0);
            };
            TrafficMonitor.Instance.MinuteSaved += OnMinute;
        }

        public static void Add(string kind, string title, string body, int app)
        {
            try
            {
                var a = Db.AddAlert(kind, title, body, app);
                Raised?.Invoke(a);
            }
            catch (Exception ex) { Log.Write("Alert: " + ex.Message); }
        }

        private static void OnMinute(long minute)
        {
            var s = Settings.Current;
            long to = minute + 60;

            if (s.AlertSpike && s.SpikeMegabytes > 0)
            {
                long from = to - Math.Max(1, s.SpikeMinutes) * 60L;
                long limit = s.SpikeMegabytes * 1024L * 1024L;
                foreach (var kv in Db.AppTotals(minute, to))
                {
                    if (kv.Value.Sum == 0) continue;
                    if (SpikeCooldown.TryGetValue(kv.Key, out var until) && DateTime.UtcNow < until) continue;
                    long used = Db.AppRecent(kv.Key, from, to);
                    if (used < limit) continue;
                    SpikeCooldown[kv.Key] = DateTime.UtcNow.AddHours(1);
                    var app = Db.GetApp(kv.Key);
                    Add(AlertKinds.Spike, (app?.Name ?? "An app") + " is using a lot of data",
                        Format.Size(used) + " in the last " + s.SpikeMinutes + " minutes.", kv.Key);
                }
            }

            if (s.CapEnabled && s.CapGigabytes > 0 && minute % 300 == 0) CheckCap();
        }

        /// <summary>The start of the current billing month (local time).</summary>
        public static DateTime CapPeriodStart()
        {
            var now = DateTime.Now;
            int day = Math.Min(Math.Max(1, Settings.Current.CapResetDay), 28);
            var start = new DateTime(now.Year, now.Month, day);
            if (now < start) start = start.AddMonths(-1);
            return start;
        }

        public static Totals CapUsage()
        {
            var start = CapPeriodStart();
            int adapter = Db.AdapterIdFor(Settings.Current.CapAdapter);
            if (adapter < 0) adapter = 0;
            return Db.Total(Format.LocalToUnix(start), Format.NowUnix + 3600, adapter);
        }

        public static void CheckCap()
        {
            var s = Settings.Current;
            long period = Format.LocalToUnix(CapPeriodStart());
            if (s.CapAlertPeriod != period)
            {
                s.CapAlertPeriod = period; s.CapWarned = false; s.CapHit = false;
                Settings.Save();
            }
            double used = CapUsage().Sum;
            double cap = s.CapGigabytes * 1024 * 1024 * 1024;
            if (!s.CapHit && used >= cap)
            {
                s.CapHit = true; s.CapWarned = true; Settings.Save();
                Add(AlertKinds.DataCap, "You've reached your data cap", Format.Size((long)used) + " used of " + s.CapGigabytes + " GB this month.", 0);
            }
            else if (!s.CapWarned && used >= cap * s.CapWarnPercent / 100.0)
            {
                s.CapWarned = true; Settings.Save();
                Add(AlertKinds.DataCap, "You've used " + s.CapWarnPercent + "% of your data cap", Format.Size((long)used) + " used of " + s.CapGigabytes + " GB this month.", 0);
            }
        }
    }
}
