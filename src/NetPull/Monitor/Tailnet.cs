using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using NetPull.Core;

namespace NetPull.Monitor
{
    public sealed class TailPeer
    {
        public string Id;
        public string Name;
        public string DnsName;
        public string Os;
        public string Ip;
        public bool Online;
        public bool Direct;          // talking directly rather than through a relay
        public string Relay;
        public long RxBytes;         // from that machine to this PC
        public long TxBytes;
        public DateTime LastSeen;
        public bool IsSelf;
    }

    /// <summary>Reads "tailscale status --json" to list the machines on your tailnet.</summary>
    public static class Tailnet
    {
        public static string Exe
        {
            get
            {
                foreach (var p in new[]
                {
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Tailscale", "tailscale.exe"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Tailscale", "tailscale.exe")
                })
                    if (File.Exists(p)) return p;
                return null;
            }
        }

        public static bool Installed => Exe != null;

        public static List<TailPeer> Status()
        {
            var list = new List<TailPeer>();
            string exe = Exe;
            if (exe == null) return list;
            try
            {
                var psi = new ProcessStartInfo(exe, "status --json")
                {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
                };
                using var p = Process.Start(psi);
                string json = p.StandardOutput.ReadToEnd();
                p.WaitForExit(8000);
                if (string.IsNullOrWhiteSpace(json)) return list;
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.TryGetProperty("Self", out var self)) list.Add(Read(self, true));
                if (root.TryGetProperty("Peer", out var peers) && peers.ValueKind == JsonValueKind.Object)
                    foreach (var kv in peers.EnumerateObject()) list.Add(Read(kv.Value, false));
            }
            catch (Exception ex) { Log.Write("Tailscale status: " + ex.Message); }
            return list;
        }

        private static string S(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        private static long L(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;

        private static TailPeer Read(JsonElement e, bool self)
        {
            string ip = null;
            if (e.TryGetProperty("TailscaleIPs", out var ips) && ips.ValueKind == JsonValueKind.Array)
                foreach (var i in ips.EnumerateArray()) { ip ??= i.GetString(); if (i.GetString()?.Contains('.') == true) { ip = i.GetString(); break; } }
            DateTime.TryParse(S(e, "LastSeen"), out var seen);
            string cur = S(e, "CurAddr");
            return new TailPeer
            {
                Id = S(e, "ID") ?? S(e, "PublicKey") ?? S(e, "HostName"),
                Name = S(e, "HostName"),
                DnsName = (S(e, "DNSName") ?? "").TrimEnd('.'),
                Os = S(e, "OS"),
                Ip = ip,
                Online = e.TryGetProperty("Online", out var on) && on.ValueKind == JsonValueKind.True,
                Direct = !string.IsNullOrEmpty(cur),
                Relay = S(e, "Relay"),
                RxBytes = L(e, "RxBytes"),
                TxBytes = L(e, "TxBytes"),
                LastSeen = seen,
                IsSelf = self
            };
        }
    }
}
