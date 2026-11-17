using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Security.Cryptography;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NetPull.Core;

namespace NetPull.Monitor
{
    /// <summary>Works out which app a process id belongs to, and keeps an eye on app files changing.</summary>
    public static class AppCatalog
    {
        private sealed class PidEntry
        {
            public AppRecord App;
            public long StartTime;
            public DateTime Checked;
        }

        private static readonly Dictionary<int, PidEntry> Pids = new Dictionary<int, PidEntry>();
        private static readonly object Gate = new object();
        private static readonly HashSet<int> CheckedThisSession = new HashSet<int>();

        /// <summary>Raised (on a background thread) the first time an app ever uses the network.</summary>
        public static event Action<AppRecord> NewApp;

        /// <summary>Raised when an app's file is different from the last time it went online: (app, old version, new version).</summary>
        public static event Action<AppRecord, string, string> AppChanged;

        public const string SystemKey = "system";
        public const string UnknownKey = "unknown";

        public static AppRecord Resolve(int pid)
        {
            lock (Gate)
            {
                if (Pids.TryGetValue(pid, out var e))
                {
                    // Process ids get reused, so check now and then that it's the same process.
                    if (DateTime.UtcNow - e.Checked < TimeSpan.FromSeconds(20)) return e.App;
                    long st = pid <= 4 ? 0 : Native.ProcessStartTime(pid);
                    if (st == e.StartTime || (st == -1 && e.App.Key != UnknownKey))
                    {
                        e.Checked = DateTime.UtcNow;
                        return e.App;
                    }
                    Pids.Remove(pid);
                }
            }

            var app = Lookup(pid, out long start);
            lock (Gate) Pids[pid] = new PidEntry { App = app, StartTime = start, Checked = DateTime.UtcNow };
            bool firstTime;
            lock (Gate) firstTime = CheckedThisSession.Add(app.Id);
            if (firstTime && !string.IsNullOrEmpty(app.Path) && app.Key != SystemKey && !app.Key.StartsWith("svchost:") && File.Exists(app.Path)) _ = Task.Run(() => CheckFile(app));
            return app;
        }

        private static AppRecord Lookup(int pid, out long start)
        {
            start = 0;
            bool created;
            AppRecord app;
            if (pid == 0 || pid == 4)
            {
                app = Db.GetOrCreateApp(SystemKey, Path.Combine(Environment.SystemDirectory, "ntoskrnl.exe"), "System", out created);
            }
            else
            {
                string path = Native.ProcessPath(pid, out start);
                if (string.IsNullOrEmpty(path))
                {
                    app = Db.GetOrCreateApp(UnknownKey, null, "Unknown (closed or protected)", out created);
                    start = -1;
                }
                else if (Path.GetFileName(path).Equals("svchost.exe", StringComparison.OrdinalIgnoreCase))
                {
                    var (svc, display) = ServiceFor(pid);
                    string key = svc == null ? path.ToLowerInvariant() : "svchost:" + svc.ToLowerInvariant();
                    string name = display == null ? "Windows services (svchost)" : display;
                    app = Db.GetOrCreateApp(key, path, name, out created);
                }
                else
                {
                    app = Db.GetOrCreateApp(path.ToLowerInvariant(), path, FriendlyName(path), out created);
                }
            }
            if (created)
            {
                try { NewApp?.Invoke(app); } catch (Exception ex) { Log.Write("NewApp handler: " + ex.Message); }
            }
            return app;
        }

        public static string FriendlyName(string path)
        {
            try
            {
                var v = FileVersionInfo.GetVersionInfo(path);
                string d = (v.FileDescription ?? "").Trim();
                if (d.Length > 1 && d.Length < 60) return d;
                string p = (v.ProductName ?? "").Trim();
                if (p.Length > 1 && p.Length < 60) return p;
            }
            catch { }
            return Path.GetFileNameWithoutExtension(path);
        }

        private static readonly ConcurrentDictionary<int, (string, string)> ServiceCache = new ConcurrentDictionary<int, (string, string)>();

        /// <summary>Names the Windows service(s) running in a svchost process, e.g. "Windows Update".</summary>
        private static (string svc, string display) ServiceFor(int pid)
        {
            if (ServiceCache.TryGetValue(pid, out var hit)) return hit;
            (string, string) result = (null, null);
            try
            {
                using var searcher = new ManagementObjectSearcher("SELECT Name, DisplayName FROM Win32_Service WHERE ProcessId = " + pid);
                var list = searcher.Get().Cast<ManagementObject>()
                    .Select(o => ((string)o["Name"], (string)o["DisplayName"]))
                    .OrderBy(s => s.Item1, StringComparer.OrdinalIgnoreCase).ToList();
                if (list.Count == 1) result = list[0];
                else if (list.Count > 1) result = (string.Join("+", list.Select(s => s.Item1)), list[0].Item2 + " + " + (list.Count - 1) + " more");
            }
            catch (Exception ex) { Log.Write("Service lookup: " + ex.Message); }
            ServiceCache[pid] = result;
            return result;
        }

        private static void CheckFile(AppRecord app)
        {
            try
            {
                var fi = new FileInfo(app.Path);
                long size = fi.Length, time = fi.LastWriteTimeUtc.Ticks;
                if (app.Hash != null && app.FileSize == size && app.FileTime == time) return;
                string version = null;
                try { version = FileVersionInfo.GetVersionInfo(app.Path).FileVersion?.Trim(); } catch { }
                string hash;
                using (var s = new FileStream(app.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var sha = SHA256.Create())
                    hash = Convert.ToHexString(sha.ComputeHash(s));
                string oldHash = app.Hash, oldVersion = app.Version;
                Db.UpdateAppFile(app, size, time, version, hash);
                if (oldHash != null && !oldHash.Equals(hash, StringComparison.OrdinalIgnoreCase))
                    AppChanged?.Invoke(app, oldVersion, version);
            }
            catch (Exception ex) { Log.Write("Couldn't check " + app.Path + ": " + ex.Message); }
        }

        // ---------------- icons ----------------

        private static readonly ConcurrentDictionary<string, ImageSource> Icons = new ConcurrentDictionary<string, ImageSource>(StringComparer.OrdinalIgnoreCase);

        public static ImageSource IconFor(AppRecord app)
        {
            string path = app?.Path;
            if (string.IsNullOrEmpty(path)) return Fallback;
            return Icons.GetOrAdd(path, p =>
            {
                try
                {
                    if (!File.Exists(p)) return Fallback;
                    using var icon = System.Drawing.Icon.ExtractAssociatedIcon(p);
                    if (icon == null) return Fallback;
                    var src = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromWidthAndHeight(32, 32));
                    src.Freeze();
                    return src;
                }
                catch { return Fallback; }
            });
        }

        private static ImageSource _fallback;

        public static ImageSource Fallback
        {
            get
            {
                if (_fallback != null) return _fallback;
                try
                {
                    var src = Imaging.CreateBitmapSourceFromHIcon(System.Drawing.SystemIcons.Application.Handle, Int32Rect.Empty, BitmapSizeOptions.FromWidthAndHeight(32, 32));
                    src.Freeze();
                    _fallback = src;
                }
                catch { }
                return _fallback;
            }
        }
    }
}
