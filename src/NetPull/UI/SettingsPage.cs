using System;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using NetPull.Core;
using NetPull.Monitor;

namespace NetPull.UI
{
    public sealed class SettingsPage : IPage
    {
        public string Key => "settings";
        public string Title => "Settings";
        public string Glyph => Ui.GlyphSettings;
        public FrameworkElement View { get; }

        private readonly StackPanel _body = new StackPanel { MaxWidth = 760, HorizontalAlignment = HorizontalAlignment.Left };
        private TextBlock _updateStatus;

        public SettingsPage()
        {
            var root = new DockPanel();
            var title = Ui.Text("Settings", 24, "TextBrush", FontWeights.SemiBold);
            title.Margin = new Thickness(0, 0, 0, 4);
            DockPanel.SetDock(title, Dock.Top);
            root.Children.Add(title);
            root.Children.Add(new ScrollViewer { Content = _body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(0, 0, 16, 0) });
            View = root;
            Build();
        }

        public void Shown() { }
        public void Tick(SecondSample s) { }

        private static void Save() => Settings.Save();

        private void Section(string name, params UIElement[] items)
        {
            _body.Children.Add(Ui.Heading(name.ToUpperInvariant()));
            var sp = new StackPanel();
            foreach (var i in items) sp.Children.Add(i);
            var c = Ui.Card(sp, 18);
            c.Padding = new Thickness(18, 8, 18, 8);
            _body.Children.Add(c);
        }

        private static TextBox Number(double value, Action<double> changed, double width = 80)
        {
            var t = new TextBox { Text = value.ToString(CultureInfo.CurrentCulture), Width = width, HorizontalContentAlignment = HorizontalAlignment.Right };
            t.LostKeyboardFocus += (s, e) =>
            {
                if (double.TryParse(t.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out double v) && v >= 0) changed(v);
                else t.Text = value.ToString(CultureInfo.CurrentCulture);
            };
            return t;
        }

        private void Build()
        {
            var s = Settings.Current;

            // General
            Section("General",
                Ui.Switch("Start with Windows", "NetPull starts quietly in the tray when you sign in, so it can keep a full record.", s.StartWithWindows, v =>
                {
                    s.StartWithWindows = v; Save();
                    Task.Run(() => Elevation.ApplyTask(v));
                }),
                Ui.Switch("Mini graph", "A small always-there speed graph you can drag anywhere. Double-click it to open NetPull.", s.MiniGraph, v => App.Instance.ShowMiniGraph(v)),
                Ui.Switch("Keep the mini graph on top", null, s.MiniGraphOnTop, v => { s.MiniGraphOnTop = v; Save(); if (App.Instance.Mini != null) App.Instance.Mini.Topmost = v; }),
                Ui.Row("Speed units", "Bytes are what downloads show; bits are what broadband speeds are sold in.",
                    Ui.Combo(new[] { "MB/s (bytes)", "Mbps (bits)" }, s.UseBits ? 1 : 0, i => { s.UseBits = i == 1; Save(); })),
                Ui.Row("Keep history for", "Older traffic is removed automatically.",
                    Ui.Combo(new[] { "30 days", "90 days", "1 year" }, s.KeepDays <= 30 ? 0 : s.KeepDays <= 90 ? 1 : 2, i => { s.KeepDays = new[] { 30, 90, 365 }[i]; Save(); }))
            );

            // Alerts
            var spikeRow = new StackPanel { Orientation = Orientation.Horizontal };
            spikeRow.Children.Add(Number(s.SpikeMegabytes, v => { s.SpikeMegabytes = (int)v; Save(); }));
            spikeRow.Children.Add(Margin(Ui.Text("MB in", 13, "MutedBrush"), 8));
            spikeRow.Children.Add(Ui.Combo(new[] { "5 minutes", "10 minutes", "30 minutes", "60 minutes" }, Array.IndexOf(new[] { 5, 10, 30, 60 }, s.SpikeMinutes) is int ix && ix >= 0 ? ix : 1,
                i => { s.SpikeMinutes = new[] { 5, 10, 30, 60 }[i]; Save(); }, 120));

            var lanEvery = Ui.Combo(new[] { "Every minute", "Every 5 minutes", "Every 15 minutes", "Every 30 minutes" },
                Array.IndexOf(new[] { 1, 5, 15, 30 }, s.LanScanMinutes) is int li && li >= 0 ? li : 1,
                i => { s.LanScanMinutes = new[] { 1, 5, 15, 30 }[i]; Save(); LanWatch.Reschedule(); }, 170);

            Section("Alerts",
                Ui.Switch("Show pop-ups", "Alerts always go on the Alerts page. This also shows them as Windows notifications.", s.Popups, v => { s.Popups = v; Save(); App.Instance.Tray?.SyncMiniItem(); }),
                Ui.Switch("An app goes online for the first time", null, s.AlertNewApp, v => { s.AlertNewApp = v; Save(); }),
                Ui.Switch("An app's file has changed", "Spots updates, and anything that has swapped out an app you trust.", s.AlertAppChanged, v => { s.AlertAppChanged = v; Save(); }),
                Ui.Switch("An app uses a lot of data", null, s.AlertSpike, v => { s.AlertSpike = v; Save(); }),
                Ui.Row("Counts as a lot", "Alerts once an hour at most for each app.", spikeRow),
                Ui.Switch("A new device joins my network", null, s.AlertNewDevice, v => { s.AlertNewDevice = v; Save(); }),
                Ui.Switch("Check my network automatically", "Quiet ARP checks of your local network only.", s.LanWatch, v => { s.LanWatch = v; Save(); LanWatch.Reschedule(); }),
                Ui.Row("How often", null, lanEvery)
            );

            // Data cap
            var adapters = Db.AllAdapters().OrderBy(a => a.Name).ToList();
            var capAdapter = Ui.Combo(new[] { "All adapters" }.Concat(adapters.Select(a => a.Name)), adapters.FindIndex(a => a.Guid == s.CapAdapter) + 1,
                i => { s.CapAdapter = i == 0 ? "" : adapters[i - 1].Guid; Save(); }, 200);
            var days = Enumerable.Range(1, 28).Select(d => d.ToString()).ToArray();
            Section("Data cap",
                Ui.Switch("Warn me about a monthly data cap", "Off unless you turn it on. Shows a bar on the Usage page too.", s.CapEnabled, v => { s.CapEnabled = v; Save(); if (v) Task.Run(AlertEngine.CheckCap); }),
                Ui.Row("Monthly allowance (GB)", null, Number(s.CapGigabytes, v => { s.CapGigabytes = v; Save(); })),
                Ui.Row("Month starts on day", "The day your bill resets.", Ui.Combo(days, s.CapResetDay - 1, i => { s.CapResetDay = i + 1; Save(); }, 80)),
                Ui.Row("Warn me at", null, Ui.Combo(new[] { "50%", "75%", "80%", "90%" }, Array.IndexOf(new[] { 50, 75, 80, 90 }, s.CapWarnPercent) is int wi && wi >= 0 ? wi : 2,
                    i => { s.CapWarnPercent = new[] { 50, 75, 80, 90 }[i]; Save(); }, 90)),
                Ui.Row("Count traffic on", "Pick your internet adapter if you also use Tailscale or VMs, so it isn't counted twice.", capAdapter)
            );

            // Lookups
            Section("Lookups",
                Ui.Switch("Look up countries and ISPs online", "Sends remote addresses (never your data) to ip-api.com. Names come from your normal DNS either way.", s.OnlineLookups, v => { s.OnlineLookups = v; Save(); }),
                Ui.Switch("Look up who made each device", "Sends the first half of a device's MAC address to macvendors.com.", s.VendorLookups, v => { s.VendorLookups = v; Save(); }),
                Ui.Row("Saved lookups", "Names and countries are kept for 30 days.", Ui.Button("Clear", () => { IpLookup.ClearCache(); }))
            );

            // Updates
            _updateStatus = Ui.Text("You have NetPull " + Updates.CurrentText, 13, "MutedBrush");
            var check = Ui.Button("Check now", async () =>
            {
                _updateStatus.Text = "Checking...";
                if (await Updates.Check())
                {
                    _updateStatus.Text = "NetPull " + Updates.LatestVersion + " is available.";
                    if (MessageBox.Show("Install NetPull " + Updates.LatestVersion + " now? It reopens by itself.", "NetPull", MessageBoxButton.YesNo) == MessageBoxResult.Yes)
                        await App.Instance.InstallUpdate(t => _updateStatus.Text = t);
                }
                else _updateStatus.Text = "You have the latest version (" + Updates.CurrentText + ").";
            });
            Section("Updates",
                Ui.Switch("Check for updates", null, s.CheckForUpdates, v => { s.CheckForUpdates = v; Save(); }),
                Ui.Switch("Install updates automatically", "NetPull updates itself in the background and reopens.", s.AutoUpdate, v => { s.AutoUpdate = v; Save(); }),
                Ui.Row("Version", null, check),
                _updateStatus
            );

            // Data
            var dataRow = new StackPanel { Orientation = Orientation.Horizontal };
            dataRow.Children.Add(Margin(Ui.Button("Export all usage", ExportAll, null, Ui.GlyphExport), 0, 10));
            dataRow.Children.Add(Margin(Ui.Button("Open data folder", () => Shell.ShowInFolder(Paths.Data)), 0, 10));
            dataRow.Children.Add(Ui.Button("Delete all history", () =>
            {
                if (MessageBox.Show("Delete all traffic history, devices and alerts? This can't be undone.", "NetPull", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
                Db.ResetAll();
                Settings.Current.LanBaselineDone = false;
                Settings.Save();
                App.Instance.Hub?.RefreshBadge();
            }));
            var bottom = new StackPanel { Margin = new Thickness(0, 8, 0, 8) };
            bottom.Children.Add(dataRow);
            var where = Ui.Wrap("Everything stays on this PC in " + Paths.Data, 12);
            where.Margin = new Thickness(0, 10, 0, 0);
            bottom.Children.Add(where);
            Section("Your data", bottom);

            var foot = Ui.Wrap("NetPull " + Updates.CurrentText + "  ·  github.com/" + Updates.Repo, 12);
            foot.Margin = new Thickness(0, 18, 0, 24);
            _body.Children.Add(foot);
        }

        private static FrameworkElement Margin(FrameworkElement e, double left, double right = 8)
        {
            e.Margin = new Thickness(left, 0, right, 0);
            e.VerticalAlignment = VerticalAlignment.Center;
            return e;
        }

        private static void ExportAll()
        {
            try
            {
                var rows = new System.Collections.Generic.List<string[]>();
                long now = Format.NowUnix;
                long from = now - (long)Settings.Current.KeepDays * 86400;
                // One row per app per day.
                for (var day = DateTime.Today.AddDays(-Settings.Current.KeepDays + 1); day <= DateTime.Today; day = day.AddDays(1))
                {
                    long f = Format.LocalToUnix(day), t = Format.LocalToUnix(day.AddDays(1));
                    if (t < from) continue;
                    foreach (var kv in Db.AppTotals(f, t).OrderByDescending(x => x.Value.Sum))
                    {
                        var a = Db.GetApp(kv.Key);
                        rows.Add(new[] { day.ToString("yyyy-MM-dd"), a?.Name ?? "Unknown", a?.Path ?? "", kv.Value.Rx.ToString(), kv.Value.Tx.ToString() });
                    }
                }
                string saved = Csv.Save("NetPull usage by day.csv", new[] { "Date", "App", "Path", "Downloaded (bytes)", "Uploaded (bytes)" }, rows);
                if (saved != null) App.Instance.Tray?.Notify("Exported", saved);
            }
            catch (Exception ex) { MessageBox.Show("Couldn't export: " + ex.Message, "NetPull"); }
        }
    }
}
