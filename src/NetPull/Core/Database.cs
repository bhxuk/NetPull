using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Data.Sqlite;

namespace NetPull.Core
{
    public sealed class AppRecord
    {
        public int Id;
        public string Key;
        public string Path;
        public string Name;
        public long FirstSeen;
        public long LastSeen;
        public long FileSize;
        public long FileTime;
        public string Version;
        public string Hash;
    }

    public sealed class AdapterRecord
    {
        public int Id;
        public string Guid;
        public string Name;
        public string Description;
        public string Kind;
        public override string ToString() => Name;
    }

    public sealed class DeviceRecord
    {
        public string Mac;
        public string Ip;
        public string Hostname;
        public string Vendor;
        public string Name;
        public bool Trusted;
        public long FirstSeen;
        public long LastSeen;
        public bool AlertOffline;
        public double CapGb;
        public string Ports;
        public long PortsScanned;
        public string Kind;

        public string DisplayName =>
            !string.IsNullOrWhiteSpace(Name) ? Name :
            !string.IsNullOrWhiteSpace(Hostname) ? Hostname :
            !string.IsNullOrWhiteSpace(Vendor) ? Vendor + " device" : "Unknown device";
    }

    public sealed class AlertRecord
    {
        public long Id;
        public long Time;
        public string Kind;
        public string Title;
        public string Body;
        public int AppId;
        public bool Unread;
    }

    public sealed class IpInfoRecord
    {
        public string Ip;
        public string Host;
        public string Country;
        public string CountryCode;
        public string Isp;
        public long Updated;
    }

    public struct Totals
    {
        public long Rx;
        public long Tx;
        public long Sum => Rx + Tx;
    }

    /// <summary>NetPull's history, kept in a small SQLite file in %LOCALAPPDATA%\NetPull.</summary>
    public static class Db
    {
        private static SqliteConnection _c;
        private static readonly object Gate = new object();
        private static readonly Dictionary<string, AppRecord> AppsByKey = new Dictionary<string, AppRecord>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<int, AppRecord> AppsById = new Dictionary<int, AppRecord>();
        private static readonly Dictionary<string, AdapterRecord> AdaptersByGuid = new Dictionary<string, AdapterRecord>(StringComparer.OrdinalIgnoreCase);

        public static void Open()
        {
            lock (Gate)
            {
                try { OpenFile(); }
                catch (Exception ex)
                {
                    // A damaged file shouldn't stop NetPull: keep it aside and start a new one.
                    Log.Write("Database couldn't open, starting a new one: " + ex.Message);
                    try { _c?.Dispose(); } catch { }
                    try { File.Move(Paths.Database, Paths.Database + ".broken-" + DateTime.Now.ToString("yyyyMMddHHmmss"), true); } catch { }
                    OpenFile();
                }
            }
        }

        private static void OpenFile()
        {
            _c = new SqliteConnection("Data Source=" + Paths.Database);
            _c.Open();
            Exec("PRAGMA journal_mode=WAL;");
            Exec("PRAGMA synchronous=NORMAL;");
            Exec(@"
CREATE TABLE IF NOT EXISTS apps(id INTEGER PRIMARY KEY, key TEXT UNIQUE, path TEXT, name TEXT, first_seen INTEGER, last_seen INTEGER,
  file_size INTEGER DEFAULT 0, file_time INTEGER DEFAULT 0, version TEXT, hash TEXT);
CREATE TABLE IF NOT EXISTS adapters(id INTEGER PRIMARY KEY, guid TEXT UNIQUE, name TEXT, description TEXT, kind TEXT);
CREATE TABLE IF NOT EXISTS usage_min(t INTEGER, app INTEGER, adapter INTEGER, rx INTEGER, tx INTEGER, PRIMARY KEY(t, app, adapter)) WITHOUT ROWID;
CREATE TABLE IF NOT EXISTS usage_hour(t INTEGER, app INTEGER, adapter INTEGER, rx INTEGER, tx INTEGER, PRIMARY KEY(t, app, adapter)) WITHOUT ROWID;
CREATE TABLE IF NOT EXISTS host_hour(t INTEGER, app INTEGER, adapter INTEGER, ip TEXT, rx INTEGER, tx INTEGER, PRIMARY KEY(t, app, adapter, ip)) WITHOUT ROWID;
CREATE TABLE IF NOT EXISTS ipinfo(ip TEXT PRIMARY KEY, host TEXT, country TEXT, cc TEXT, isp TEXT, updated INTEGER);
CREATE TABLE IF NOT EXISTS devices(mac TEXT PRIMARY KEY, ip TEXT, hostname TEXT, vendor TEXT, name TEXT, trusted INTEGER DEFAULT 0, first_seen INTEGER, last_seen INTEGER);
CREATE TABLE IF NOT EXISTS alerts(id INTEGER PRIMARY KEY, t INTEGER, kind TEXT, title TEXT, body TEXT, app INTEGER, unread INTEGER DEFAULT 1);
CREATE INDEX IF NOT EXISTS ix_alerts_t ON alerts(t);
CREATE TABLE IF NOT EXISTS dev_min(t INTEGER, mac TEXT, rx INTEGER, tx INTEGER, PRIMARY KEY(t, mac)) WITHOUT ROWID;
CREATE TABLE IF NOT EXISTS dev_hour(t INTEGER, mac TEXT, rx INTEGER, tx INTEGER, PRIMARY KEY(t, mac)) WITHOUT ROWID;
");
            // Columns added in 1.1 (ignore "duplicate column" on newer files).
            foreach (var col in new[] { "alert_offline INTEGER DEFAULT 0", "cap_gb REAL DEFAULT 0", "ports TEXT", "ports_scanned INTEGER DEFAULT 0", "kind TEXT" })
            {
                try { Exec("ALTER TABLE devices ADD COLUMN " + col); } catch { }
            }
            AppsByKey.Clear(); AppsById.Clear(); AdaptersByGuid.Clear();
            using (var cmd = _c.CreateCommand())
            {
                cmd.CommandText = "SELECT id, key, path, name, first_seen, last_seen, file_size, file_time, version, hash FROM apps";
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    var a = new AppRecord
                    {
                        Id = r.GetInt32(0), Key = r.GetString(1), Path = Str(r, 2), Name = Str(r, 3),
                        FirstSeen = Long(r, 4), LastSeen = Long(r, 5), FileSize = Long(r, 6), FileTime = Long(r, 7),
                        Version = Str(r, 8), Hash = Str(r, 9)
                    };
                    AppsByKey[a.Key] = a; AppsById[a.Id] = a;
                }
            }
            using (var cmd = _c.CreateCommand())
            {
                cmd.CommandText = "SELECT id, guid, name, description, kind FROM adapters";
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    var a = new AdapterRecord { Id = r.GetInt32(0), Guid = r.GetString(1), Name = Str(r, 2), Description = Str(r, 3), Kind = Str(r, 4) };
                    AdaptersByGuid[a.Guid] = a;
                }
            }
        }

        private static string Str(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
        private static long Long(SqliteDataReader r, int i) => r.IsDBNull(i) ? 0 : r.GetInt64(i);

        private static void Exec(string sql)
        {
            using var cmd = _c.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }

        private static SqliteCommand Cmd(string sql, params (string name, object value)[] args)
        {
            var cmd = _c.CreateCommand();
            cmd.CommandText = sql;
            foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
            return cmd;
        }

        // ---------------- apps ----------------

        public static AppRecord GetApp(int id)
        {
            lock (Gate) return AppsById.TryGetValue(id, out var a) ? a : null;
        }

        public static List<AppRecord> AllApps()
        {
            lock (Gate) return new List<AppRecord>(AppsById.Values);
        }

        public static AppRecord GetOrCreateApp(string key, string path, string name, out bool created)
        {
            created = false;
            lock (Gate)
            {
                if (AppsByKey.TryGetValue(key, out var a)) return a;
                long now = Format.NowUnix;
                using var cmd = Cmd("INSERT INTO apps(key, path, name, first_seen, last_seen) VALUES(@k, @p, @n, @t, @t); SELECT last_insert_rowid();",
                    ("@k", key), ("@p", path), ("@n", name), ("@t", now));
                a = new AppRecord { Id = Convert.ToInt32(cmd.ExecuteScalar()), Key = key, Path = path, Name = name, FirstSeen = now, LastSeen = now };
                AppsByKey[key] = a; AppsById[a.Id] = a;
                created = true;
                return a;
            }
        }

        public static void UpdateAppName(AppRecord a, string name)
        {
            lock (Gate)
            {
                a.Name = name;
                using var cmd = Cmd("UPDATE apps SET name=@n WHERE id=@i", ("@n", name), ("@i", a.Id));
                cmd.ExecuteNonQuery();
            }
        }

        public static void UpdateAppFile(AppRecord a, long size, long time, string version, string hash)
        {
            lock (Gate)
            {
                a.FileSize = size; a.FileTime = time; a.Version = version; a.Hash = hash;
                using var cmd = Cmd("UPDATE apps SET file_size=@s, file_time=@ft, version=@v, hash=@h WHERE id=@i",
                    ("@s", size), ("@ft", time), ("@v", version), ("@h", hash), ("@i", a.Id));
                cmd.ExecuteNonQuery();
            }
        }

        // ---------------- adapters ----------------

        public static int GetOrCreateAdapter(string guid, string name, string description, string kind)
        {
            lock (Gate)
            {
                if (AdaptersByGuid.TryGetValue(guid, out var a))
                {
                    if (a.Name != name || a.Description != description)
                    {
                        a.Name = name; a.Description = description; a.Kind = kind;
                        using var up = Cmd("UPDATE adapters SET name=@n, description=@d, kind=@k WHERE id=@i", ("@n", name), ("@d", description), ("@k", kind), ("@i", a.Id));
                        up.ExecuteNonQuery();
                    }
                    return a.Id;
                }
                using var cmd = Cmd("INSERT INTO adapters(guid, name, description, kind) VALUES(@g, @n, @d, @k); SELECT last_insert_rowid();",
                    ("@g", guid), ("@n", name), ("@d", description), ("@k", kind));
                a = new AdapterRecord { Id = Convert.ToInt32(cmd.ExecuteScalar()), Guid = guid, Name = name, Description = description, Kind = kind };
                AdaptersByGuid[guid] = a;
                return a.Id;
            }
        }

        public static List<AdapterRecord> AllAdapters()
        {
            lock (Gate) return new List<AdapterRecord>(AdaptersByGuid.Values);
        }

        /// <summary>The adapter id for a saved filter (a guid), or 0 for all adapters.</summary>
        public static int AdapterIdFor(string guid)
        {
            if (string.IsNullOrEmpty(guid)) return 0;
            lock (Gate) return AdaptersByGuid.TryGetValue(guid, out var a) ? a.Id : -1;
        }

        // ---------------- usage ----------------

        public struct MinuteRow { public int App; public int Adapter; public long Rx; public long Tx; }
        public struct HostRow { public int App; public int Adapter; public string Ip; public long Rx; public long Tx; }

        /// <summary>Adds one minute of traffic to the minute, hour and host tables.</summary>
        public static void WriteMinute(long minute, List<MinuteRow> rows, List<HostRow> hosts, IEnumerable<int> seenApps)
        {
            if (rows.Count == 0 && hosts.Count == 0) return;
            long hour = minute - minute % 3600;
            lock (Gate)
            {
                using var tx = _c.BeginTransaction();
                using (var m = _c.CreateCommand())
                using (var h = _c.CreateCommand())
                {
                    m.Transaction = tx; h.Transaction = tx;
                    m.CommandText = "INSERT INTO usage_min(t, app, adapter, rx, tx) VALUES(@t, @a, @d, @r, @x) ON CONFLICT(t, app, adapter) DO UPDATE SET rx = rx + excluded.rx, tx = tx + excluded.tx";
                    h.CommandText = "INSERT INTO usage_hour(t, app, adapter, rx, tx) VALUES(@t, @a, @d, @r, @x) ON CONFLICT(t, app, adapter) DO UPDATE SET rx = rx + excluded.rx, tx = tx + excluded.tx";
                    var mp = AddParams(m); var hp = AddParams(h);
                    foreach (var row in rows)
                    {
                        Set(mp, minute, row.App, row.Adapter, row.Rx, row.Tx); m.ExecuteNonQuery();
                        Set(hp, hour, row.App, row.Adapter, row.Rx, row.Tx); h.ExecuteNonQuery();
                    }
                }
                using (var c = _c.CreateCommand())
                {
                    c.Transaction = tx;
                    c.CommandText = "INSERT INTO host_hour(t, app, adapter, ip, rx, tx) VALUES(@t, @a, @d, @ip, @r, @x) ON CONFLICT(t, app, adapter, ip) DO UPDATE SET rx = rx + excluded.rx, tx = tx + excluded.tx";
                    var p = AddParams(c);
                    var ip = c.Parameters.Add("@ip", SqliteType.Text);
                    foreach (var row in hosts)
                    {
                        Set(p, hour, row.App, row.Adapter, row.Rx, row.Tx);
                        ip.Value = row.Ip;
                        c.ExecuteNonQuery();
                    }
                }
                using (var c = _c.CreateCommand())
                {
                    c.Transaction = tx;
                    c.CommandText = "UPDATE apps SET last_seen=@t WHERE id=@i";
                    var t = c.Parameters.Add("@t", SqliteType.Integer);
                    var i = c.Parameters.Add("@i", SqliteType.Integer);
                    foreach (int id in seenApps)
                    {
                        t.Value = minute + 59; i.Value = id;
                        c.ExecuteNonQuery();
                        if (AppsById.TryGetValue(id, out var a)) a.LastSeen = minute + 59;
                    }
                }
                tx.Commit();
            }
        }

        private static SqliteParameter[] AddParams(SqliteCommand c) => new[]
        {
            c.Parameters.Add("@t", SqliteType.Integer), c.Parameters.Add("@a", SqliteType.Integer), c.Parameters.Add("@d", SqliteType.Integer),
            c.Parameters.Add("@r", SqliteType.Integer), c.Parameters.Add("@x", SqliteType.Integer)
        };

        private static void Set(SqliteParameter[] p, long t, int app, int adapter, long rx, long tx)
        {
            p[0].Value = t; p[1].Value = app; p[2].Value = adapter; p[3].Value = rx; p[4].Value = tx;
        }

        /// <summary>Minute rows are kept for 8 days; anything longer comes from the hour table.</summary>
        private static string TableFor(long from, long to)
        {
            long now = Format.NowUnix;
            return (to - from > 2 * 86400 || from < now - 7 * 86400) ? "usage_hour" : "usage_min";
        }

        private static string Filter(int adapter, int app)
        {
            string s = "";
            if (adapter != 0) s += " AND adapter = @ad";
            if (app != 0) s += " AND app = @ap";
            return s;
        }

        /// <summary>Bytes per bucket between from and to (unix seconds), for the history graph.</summary>
        public static (long[] rx, long[] tx) Series(long from, long to, int bucket, int adapter = 0, int app = 0)
        {
            int n = (int)Math.Max(1, (to - from + bucket - 1) / bucket);
            var rx = new long[n]; var tx = new long[n];
            string table = bucket >= 3600 ? "usage_hour" : "usage_min";
            lock (Gate)
            {
                using var cmd = Cmd("SELECT (t - @f) / @b AS k, SUM(rx), SUM(tx) FROM " + table + " WHERE t >= @f AND t < @to" + Filter(adapter, app) + " GROUP BY k",
                    ("@f", from), ("@to", to), ("@b", bucket), ("@ad", adapter), ("@ap", app));
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    long k = r.GetInt64(0);
                    if (k < 0 || k >= n) continue;
                    rx[k] += r.GetInt64(1); tx[k] += r.GetInt64(2);
                }
            }
            return (rx, tx);
        }

        public static Dictionary<int, Totals> AppTotals(long from, long to, int adapter = 0)
        {
            var d = new Dictionary<int, Totals>();
            string table = TableFor(from, to);
            if (table == "usage_hour") from -= from % 3600;
            lock (Gate)
            {
                using var cmd = Cmd("SELECT app, SUM(rx), SUM(tx) FROM " + table + " WHERE t >= @f AND t < @to" + Filter(adapter, 0) + " GROUP BY app",
                    ("@f", from), ("@to", to), ("@ad", adapter), ("@ap", 0));
                using var r = cmd.ExecuteReader();
                while (r.Read()) d[r.GetInt32(0)] = new Totals { Rx = r.GetInt64(1), Tx = r.GetInt64(2) };
            }
            return d;
        }

        public static Totals Total(long from, long to, int adapter = 0)
        {
            var t = new Totals();
            foreach (var v in AppTotals(from, to, adapter).Values) { t.Rx += v.Rx; t.Tx += v.Tx; }
            return t;
        }

        public static List<(string ip, int app, Totals totals)> HostTotals(long from, long to, int adapter = 0, int app = 0)
        {
            var list = new List<(string, int, Totals)>();
            from -= from % 3600;
            lock (Gate)
            {
                using var cmd = Cmd("SELECT ip, app, SUM(rx), SUM(tx) FROM host_hour WHERE t >= @f AND t < @to" + Filter(adapter, app) + " GROUP BY ip, app",
                    ("@f", from), ("@to", to), ("@ad", adapter), ("@ap", app));
                using var r = cmd.ExecuteReader();
                while (r.Read()) list.Add((r.GetString(0), r.GetInt32(1), new Totals { Rx = r.GetInt64(2), Tx = r.GetInt64(3) }));
            }
            return list;
        }

        /// <summary>Total for one app over the last few minutes, for spike alerts.</summary>
        public static long AppRecent(int app, long from, long to)
        {
            lock (Gate)
            {
                using var cmd = Cmd("SELECT COALESCE(SUM(rx + tx), 0) FROM usage_min WHERE app = @a AND t >= @f AND t < @to", ("@a", app), ("@f", from), ("@to", to));
                return Convert.ToInt64(cmd.ExecuteScalar());
            }
        }

        public static void Prune(int keepDays)
        {
            long now = Format.NowUnix;
            lock (Gate)
            {
                using (var c = Cmd("DELETE FROM usage_min WHERE t < @t", ("@t", now - 8 * 86400))) c.ExecuteNonQuery();
                using (var c = Cmd("DELETE FROM usage_hour WHERE t < @t", ("@t", now - (long)keepDays * 86400))) c.ExecuteNonQuery();
                using (var c = Cmd("DELETE FROM host_hour WHERE t < @t", ("@t", now - (long)keepDays * 86400))) c.ExecuteNonQuery();
                using (var c = Cmd("DELETE FROM dev_min WHERE t < @t", ("@t", now - 8 * 86400))) c.ExecuteNonQuery();
                using (var c = Cmd("DELETE FROM dev_hour WHERE t < @t", ("@t", now - (long)keepDays * 86400))) c.ExecuteNonQuery();
                using (var c = Cmd("DELETE FROM alerts WHERE t < @t", ("@t", now - (long)keepDays * 86400))) c.ExecuteNonQuery();
                using (var c = Cmd("DELETE FROM ipinfo WHERE updated < @t", ("@t", now - 60L * 86400))) c.ExecuteNonQuery();
            }
        }

        // ---------------- ip info ----------------

        public static IpInfoRecord GetIpInfo(string ip)
        {
            lock (Gate)
            {
                using var cmd = Cmd("SELECT host, country, cc, isp, updated FROM ipinfo WHERE ip = @ip", ("@ip", ip));
                using var r = cmd.ExecuteReader();
                if (!r.Read()) return null;
                return new IpInfoRecord { Ip = ip, Host = Str(r, 0), Country = Str(r, 1), CountryCode = Str(r, 2), Isp = Str(r, 3), Updated = Long(r, 4) };
            }
        }

        public static void PutIpInfo(IpInfoRecord i)
        {
            lock (Gate)
            {
                using var cmd = Cmd("INSERT INTO ipinfo(ip, host, country, cc, isp, updated) VALUES(@ip, @h, @c, @cc, @isp, @u) " +
                                    "ON CONFLICT(ip) DO UPDATE SET host=excluded.host, country=excluded.country, cc=excluded.cc, isp=excluded.isp, updated=excluded.updated",
                    ("@ip", i.Ip), ("@h", i.Host), ("@c", i.Country), ("@cc", i.CountryCode), ("@isp", i.Isp), ("@u", i.Updated));
                cmd.ExecuteNonQuery();
            }
        }

        public static void ClearIpInfo()
        {
            lock (Gate) Exec("DELETE FROM ipinfo");
        }

        // ---------------- devices ----------------

        public static List<DeviceRecord> Devices()
        {
            var list = new List<DeviceRecord>();
            lock (Gate)
            {
                using var cmd = Cmd("SELECT mac, ip, hostname, vendor, name, trusted, first_seen, last_seen, alert_offline, cap_gb, ports, ports_scanned, kind FROM devices");
                using var r = cmd.ExecuteReader();
                while (r.Read())
                    list.Add(new DeviceRecord
                    {
                        Mac = r.GetString(0), Ip = Str(r, 1), Hostname = Str(r, 2), Vendor = Str(r, 3), Name = Str(r, 4),
                        Trusted = Long(r, 5) != 0, FirstSeen = Long(r, 6), LastSeen = Long(r, 7),
                        AlertOffline = Long(r, 8) != 0, CapGb = r.IsDBNull(9) ? 0 : r.GetDouble(9), Ports = Str(r, 10), PortsScanned = Long(r, 11), Kind = Str(r, 12)
                    });
            }
            return list;
        }

        public static void SaveDevice(DeviceRecord d)
        {
            lock (Gate)
            {
                using var cmd = Cmd("INSERT INTO devices(mac, ip, hostname, vendor, name, trusted, first_seen, last_seen, alert_offline, cap_gb, ports, ports_scanned, kind) " +
                                    "VALUES(@m, @ip, @h, @v, @n, @tr, @f, @l, @ao, @cap, @p, @ps, @k) " +
                                    "ON CONFLICT(mac) DO UPDATE SET ip=excluded.ip, hostname=excluded.hostname, vendor=excluded.vendor, name=excluded.name, trusted=excluded.trusted, last_seen=excluded.last_seen, " +
                                    "alert_offline=excluded.alert_offline, cap_gb=excluded.cap_gb, ports=excluded.ports, ports_scanned=excluded.ports_scanned, kind=excluded.kind",
                    ("@m", d.Mac), ("@ip", d.Ip), ("@h", d.Hostname), ("@v", d.Vendor), ("@n", d.Name), ("@tr", d.Trusted ? 1 : 0), ("@f", d.FirstSeen), ("@l", d.LastSeen),
                    ("@ao", d.AlertOffline ? 1 : 0), ("@cap", d.CapGb), ("@p", d.Ports), ("@ps", d.PortsScanned), ("@k", d.Kind));
                cmd.ExecuteNonQuery();
            }
        }

        public static void ForgetDevice(string mac)
        {
            lock (Gate)
            {
                using var cmd = Cmd("DELETE FROM devices WHERE mac = @m", ("@m", mac));
                cmd.ExecuteNonQuery();
            }
        }

        // ---------------- device traffic (from UniFi / Tailscale) ----------------

        public static void WriteDeviceMinute(long minute, Dictionary<string, Totals> rows)
        {
            if (rows.Count == 0) return;
            long hour = minute - minute % 3600;
            lock (Gate)
            {
                using var tx = _c.BeginTransaction();
                foreach (var table in new[] { ("dev_min", minute), ("dev_hour", hour) })
                {
                    using var c = _c.CreateCommand();
                    c.Transaction = tx;
                    c.CommandText = "INSERT INTO " + table.Item1 + "(t, mac, rx, tx) VALUES(@t, @m, @r, @x) ON CONFLICT(t, mac) DO UPDATE SET rx = rx + excluded.rx, tx = tx + excluded.tx";
                    var t = c.Parameters.Add("@t", SqliteType.Integer);
                    var m = c.Parameters.Add("@m", SqliteType.Text);
                    var r = c.Parameters.Add("@r", SqliteType.Integer);
                    var x = c.Parameters.Add("@x", SqliteType.Integer);
                    foreach (var kv in rows)
                    {
                        t.Value = table.Item2; m.Value = kv.Key; r.Value = kv.Value.Rx; x.Value = kv.Value.Tx;
                        c.ExecuteNonQuery();
                    }
                }
                tx.Commit();
            }
        }

        public static (long[] rx, long[] tx) DeviceSeries(long from, long to, int bucket, string mac = null)
        {
            int n = (int)Math.Max(1, (to - from + bucket - 1) / bucket);
            var rx = new long[n]; var tx = new long[n];
            string table = bucket >= 3600 ? "dev_hour" : "dev_min";
            lock (Gate)
            {
                using var cmd = Cmd("SELECT (t - @f) / @b AS k, SUM(rx), SUM(tx) FROM " + table + " WHERE t >= @f AND t < @to" + (mac == null ? "" : " AND mac = @m") + " GROUP BY k",
                    ("@f", from), ("@to", to), ("@b", bucket), ("@m", mac));
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    long k = r.GetInt64(0);
                    if (k < 0 || k >= n) continue;
                    rx[k] += r.GetInt64(1); tx[k] += r.GetInt64(2);
                }
            }
            return (rx, tx);
        }

        public static Dictionary<string, Totals> DeviceTotals(long from, long to)
        {
            var d = new Dictionary<string, Totals>(StringComparer.OrdinalIgnoreCase);
            long now = Format.NowUnix;
            bool hourly = to - from > 2 * 86400 || from < now - 7 * 86400;
            string table = hourly ? "dev_hour" : "dev_min";
            if (hourly) from -= from % 3600;
            lock (Gate)
            {
                using var cmd = Cmd("SELECT mac, SUM(rx), SUM(tx) FROM " + table + " WHERE t >= @f AND t < @to GROUP BY mac", ("@f", from), ("@to", to));
                using var r = cmd.ExecuteReader();
                while (r.Read()) d[r.GetString(0)] = new Totals { Rx = r.GetInt64(1), Tx = r.GetInt64(2) };
            }
            return d;
        }

        public static long DeviceRecent(string mac, long from, long to)
        {
            lock (Gate)
            {
                using var cmd = Cmd("SELECT COALESCE(SUM(rx + tx), 0) FROM dev_min WHERE mac = @m AND t >= @f AND t < @to", ("@m", mac), ("@f", from), ("@to", to));
                return Convert.ToInt64(cmd.ExecuteScalar());
            }
        }

        // ---------------- alerts ----------------

        public static AlertRecord AddAlert(string kind, string title, string body, int app)
        {
            lock (Gate)
            {
                long now = Format.NowUnix;
                using var cmd = Cmd("INSERT INTO alerts(t, kind, title, body, app, unread) VALUES(@t, @k, @ti, @b, @a, 1); SELECT last_insert_rowid();",
                    ("@t", now), ("@k", kind), ("@ti", title), ("@b", body), ("@a", app));
                return new AlertRecord { Id = Convert.ToInt64(cmd.ExecuteScalar()), Time = now, Kind = kind, Title = title, Body = body, AppId = app, Unread = true };
            }
        }

        public static List<AlertRecord> Alerts(int limit = 500)
        {
            var list = new List<AlertRecord>();
            lock (Gate)
            {
                using var cmd = Cmd("SELECT id, t, kind, title, body, app, unread FROM alerts ORDER BY t DESC, id DESC LIMIT @l", ("@l", limit));
                using var r = cmd.ExecuteReader();
                while (r.Read())
                    list.Add(new AlertRecord { Id = r.GetInt64(0), Time = Long(r, 1), Kind = Str(r, 2), Title = Str(r, 3), Body = Str(r, 4), AppId = (int)Long(r, 5), Unread = Long(r, 6) != 0 });
            }
            return list;
        }

        public static int UnreadAlerts()
        {
            lock (Gate)
            {
                using var cmd = Cmd("SELECT COUNT(*) FROM alerts WHERE unread = 1");
                return Convert.ToInt32(cmd.ExecuteScalar());
            }
        }

        public static void MarkAlertsRead()
        {
            lock (Gate) Exec("UPDATE alerts SET unread = 0 WHERE unread = 1");
        }

        public static void ClearAlerts()
        {
            lock (Gate) Exec("DELETE FROM alerts");
        }

        /// <summary>Removes all history, apps, devices and alerts.</summary>
        public static void ResetAll()
        {
            lock (Gate)
            {
                Exec("DELETE FROM usage_min; DELETE FROM usage_hour; DELETE FROM host_hour; DELETE FROM alerts; DELETE FROM devices; DELETE FROM ipinfo; DELETE FROM dev_min; DELETE FROM dev_hour;");
            }
        }
    }
}
