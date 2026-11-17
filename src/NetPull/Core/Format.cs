using System;
using System.Globalization;

namespace NetPull.Core
{
    /// <summary>Turns byte counts and speeds into short, readable text.</summary>
    public static class Format
    {
        private static readonly CultureInfo C = CultureInfo.CurrentCulture;

        public static string Size(long bytes)
        {
            double b = Math.Abs((double)bytes);
            if (b < 1024) return bytes.ToString(C) + " B";
            string[] units = { "KB", "MB", "GB", "TB", "PB" };
            double v = b / 1024; int i = 0;
            while (v >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
            return Num(v) + " " + units[i];
        }

        public static string Rate(double bytesPerSecond)
        {
            if (bytesPerSecond < 0) bytesPerSecond = 0;
            if (Settings.Current.UseBits)
            {
                double bits = bytesPerSecond * 8;
                if (bits < 1000) return Math.Round(bits).ToString(C) + " bps";
                string[] u = { "Kbps", "Mbps", "Gbps", "Tbps" };
                double v = bits / 1000; int i = 0;
                while (v >= 1000 && i < u.Length - 1) { v /= 1000; i++; }
                return Num(v) + " " + u[i];
            }
            if (bytesPerSecond < 1024) return Math.Round(bytesPerSecond).ToString(C) + " B/s";
            string[] units = { "KB/s", "MB/s", "GB/s", "TB/s" };
            double x = bytesPerSecond / 1024; int j = 0;
            while (x >= 1024 && j < units.Length - 1) { x /= 1024; j++; }
            return Num(x) + " " + units[j];
        }

        /// <summary>Very short speed for the tray tooltip and the mini graph, e.g. "1.2M".</summary>
        public static string RateShort(double bytesPerSecond)
        {
            string s = Rate(bytesPerSecond);
            return s.Replace("/s", "").Replace("bps", "b");
        }

        private static string Num(double v)
        {
            if (v >= 100) return v.ToString("0", C);
            if (v >= 10) return v.ToString("0.0", C);
            return v.ToString("0.00", C);
        }

        public static string Ago(DateTime utc)
        {
            var span = DateTime.UtcNow - utc;
            if (span.TotalSeconds < 60) return "just now";
            if (span.TotalMinutes < 60) return (int)span.TotalMinutes + " min ago";
            if (span.TotalHours < 24) return (int)span.TotalHours + " hr ago";
            if (span.TotalDays < 7) return (int)span.TotalDays + (span.TotalDays < 2 ? " day ago" : " days ago");
            return utc.ToLocalTime().ToString("d MMM yyyy", C);
        }

        public static DateTime FromUnix(long t) => DateTimeOffset.FromUnixTimeSeconds(t).UtcDateTime;
        public static long ToUnix(DateTime utc) => new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeSeconds();
        public static long NowUnix => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        public static long LocalToUnix(DateTime local) => new DateTimeOffset(local).ToUnixTimeSeconds();
    }
}
