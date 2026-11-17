using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using NetPull.Core;
using NetPull.Monitor;

namespace NetPull.UI
{
    /// <summary>How much each app (or each website / server) used over a day, week, month or more.</summary>
    public sealed class UsagePage : IPage
    {
        public string Key => "usage";
        public string Title => "Usage";
        public string Glyph => Ui.GlyphUsage;
        public FrameworkElement View { get; }

        private int _period;
        private bool _hosts;
        private bool _devices;
        private readonly StackPanel _list = new StackPanel();
        private readonly TextBlock _totalDown = Ui.Text("", 20, "DownBrush", FontWeights.SemiBold);
        private readonly TextBlock _totalUp = Ui.Text("", 20, "UpBrush", FontWeights.SemiBold);
        private readonly TextBlock _total = Ui.Text("", 20, "TextBrush", FontWeights.SemiBold);
        private readonly StackPanel _capPanel = new StackPanel();
        private readonly RadioButton _billing;
        private readonly HashSet<int> _expanded = new HashSet<int>();
        private DateTime _lastRefresh = DateTime.MinValue;
        private bool _busy;
        private List<string[]> _exportRows = new List<string[]>();
        private string[] _exportHeader = Array.Empty<string>();

        public UsagePage()
        {
            var root = new DockPanel();

            var head = new DockPanel { Margin = new Thickness(0, 0, 0, 14) };
            DockPanel.SetDock(head, Dock.Top);
            var right = new StackPanel { Orientation = Orientation.Horizontal };
            DockPanel.SetDock(right, Dock.Right);
            var export = Ui.Button("Export CSV", Export, null, Ui.GlyphExport);
            export.Margin = new Thickness(0, 0, 10, 0);
            right.Children.Add(export);
            right.Children.Add(AdapterPicker.Create());
            head.Children.Add(right);
            head.Children.Add(Ui.Text("Usage", 24, "TextBrush", FontWeights.SemiBold));
            root.Children.Add(head);
            AdapterPicker.Changed += () => Refresh();

            var bar = new DockPanel { Margin = new Thickness(0, 0, 0, 14) };
            DockPanel.SetDock(bar, Dock.Top);
            var tabs = new StackPanel { Orientation = Orientation.Horizontal };
            DockPanel.SetDock(tabs, Dock.Right);
            tabs.Children.Add(Ui.Chip("Apps", "usage-tab", true, () => { _hosts = false; _devices = false; Refresh(); }));
            tabs.Children.Add(Ui.Chip("Hosts", "usage-tab", false, () => { _hosts = true; _devices = false; Refresh(); }));
            tabs.Children.Add(Ui.Chip("Devices", "usage-tab", false, () => { _hosts = false; _devices = true; Refresh(); }));
            bar.Children.Add(tabs);
            var chips = new StackPanel { Orientation = Orientation.Horizontal };
            for (int i = 0; i < Periods.Names.Length; i++)
            {
                int idx = i;
                chips.Children.Add(Ui.Chip(Periods.Names[i], "usage-period", i == 0, () => { _period = idx; Refresh(); }));
            }
            _billing = Ui.Chip("Billing month", "usage-period", false, () => { _period = 6; Refresh(); });
            chips.Children.Add(_billing);
            bar.Children.Add(chips);
            root.Children.Add(bar);

            var stats = new Grid { Margin = new Thickness(0, 0, 0, 14) };
            DockPanel.SetDock(stats, Dock.Top);
            for (int i = 0; i < 4; i++) stats.ColumnDefinitions.Add(new ColumnDefinition());
            AddStat(stats, 0, "Downloaded", _totalDown);
            AddStat(stats, 1, "Uploaded", _totalUp);
            AddStat(stats, 2, "Total", _total);
            var capCard = Ui.Card(_capPanel, 14);
            capCard.Margin = new Thickness(6, 0, 0, 0);
            Grid.SetColumn(capCard, 3);
            stats.Children.Add(capCard);
            root.Children.Add(stats);

            var scroll = new ScrollViewer { Content = _list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            root.Children.Add(Ui.Card(scroll, 12));
            View = root;

            IpLookup.Updated += () => View.Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_hosts && View.IsVisible && DateTime.UtcNow - _lastRefresh > TimeSpan.FromSeconds(4)) Refresh();
            }));
        }

        private static void AddStat(Grid g, int col, string label, TextBlock value)
        {
            var sp = new StackPanel();
            sp.Children.Add(Ui.Text(label, 12, "MutedBrush"));
            value.Margin = new Thickness(0, 4, 0, 0);
            sp.Children.Add(value);
            var c = Ui.Card(sp, 14);
            c.Margin = new Thickness(col == 0 ? 0 : 6, 0, 6, 0);
            Grid.SetColumn(c, col);
            g.Children.Add(c);
        }

        public void Shown()
        {
            _billing.Visibility = Settings.Current.CapEnabled ? Visibility.Visible : Visibility.Collapsed;
            if (!Settings.Current.CapEnabled && _period == 6) _period = 3;
            Refresh();
        }

        public void Tick(SecondSample s)
        {
            if (DateTime.UtcNow - _lastRefresh > TimeSpan.FromSeconds(30)) Refresh();
        }

        private async void Refresh()
        {
            if (_busy) return;
            _busy = true;
            _lastRefresh = DateTime.UtcNow;
            try
            {
                var (fromL, toL) = Periods.Range(_period);
                long from = Format.LocalToUnix(fromL), to = Format.LocalToUnix(toL);
                int adapter = AdapterPicker.Current;
                bool hosts = _hosts, devicesTab = _devices;

                var apps = await Task.Run(() => Db.AppTotals(from, to, adapter));
                if (Format.NowUnix < to)
                    foreach (var kv in TrafficMonitor.Instance.PendingAppTotals(adapter))
                    {
                        apps.TryGetValue(kv.Key, out var t);
                        t.Rx += kv.Value.Rx; t.Tx += kv.Value.Tx;
                        apps[kv.Key] = t;
                    }
                long rx = apps.Values.Sum(v => v.Rx), tx = apps.Values.Sum(v => v.Tx);
                _totalDown.Text = Format.Size(rx);
                _totalUp.Text = Format.Size(tx);
                _total.Text = Format.Size(rx + tx);
                UpdateCap();

                if (devicesTab)
                {
                    var dev = await Task.Run(() => Db.DeviceTotals(from, to));
                    long drx = dev.Values.Sum(v => v.Rx), dtx = dev.Values.Sum(v => v.Tx);
                    _totalDown.Text = Format.Size(drx);
                    _totalUp.Text = Format.Size(dtx);
                    _total.Text = Format.Size(drx + dtx);
                    FillDevices(dev);
                }
                else if (hosts)
                {
                    var rows = await Task.Run(() => Db.HostTotals(from, to, adapter));
                    FillHosts(rows);
                }
                else FillApps(apps, from, to, adapter);
            }
            catch (Exception ex) { Log.Write("Usage: " + ex.Message); }
            finally { _busy = false; }
        }

        private void UpdateCap()
        {
            _capPanel.Children.Clear();
            var s = Settings.Current;
            if (!s.CapEnabled)
            {
                _capPanel.Children.Add(Ui.Text("Data cap", 12, "MutedBrush"));
                var off = Ui.Text("Off", 15, "TextBrush", FontWeights.SemiBold);
                off.Margin = new Thickness(0, 6, 0, 0);
                _capPanel.Children.Add(off);
                var link = Ui.Button("Set one in Settings", () => App.Instance.ShowMain("settings"), "GhostButton");
                link.HorizontalAlignment = HorizontalAlignment.Left;
                link.Margin = new Thickness(-8, 2, 0, 0);
                link.Foreground = Ui.B("AccentBrush");
                _capPanel.Children.Add(link);
                return;
            }
            var used = AlertEngine.CapUsage().Sum;
            double cap = s.CapGigabytes * 1024 * 1024 * 1024;
            double f = Math.Min(1, used / Math.Max(1, cap));
            _capPanel.Children.Add(Ui.Text("Data cap this month", 12, "MutedBrush"));
            var v = Ui.Text(Format.Size(used) + " of " + s.CapGigabytes + " GB", 15, "TextBrush", FontWeights.SemiBold);
            v.Margin = new Thickness(0, 6, 0, 8);
            _capPanel.Children.Add(v);
            var g = new Grid { Height = 6 };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(f, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1 - f, GridUnitType.Star) });
            var back = new Border { Background = Ui.B("Panel2Brush"), CornerRadius = new CornerRadius(3) };
            Grid.SetColumnSpan(back, 2);
            g.Children.Add(back);
            g.Children.Add(new Border { Background = Ui.B(f >= 1 ? "BadBrush" : f * 100 >= s.CapWarnPercent ? "WarnBrush" : "AccentBrush"), CornerRadius = new CornerRadius(3) });
            _capPanel.Children.Add(g);
            var start = AlertEngine.CapPeriodStart();
            var resets = Ui.Text("Resets " + start.AddMonths(1).ToString("d MMM"), 11.5, "MutedBrush");
            resets.Margin = new Thickness(0, 6, 0, 0);
            _capPanel.Children.Add(resets);
        }

        private static Grid RowGrid()
        {
            var g = new Grid { Margin = new Thickness(4, 0, 4, 0) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) });
            return g;
        }

        private static void Put(Grid g, UIElement e, int col)
        {
            Grid.SetColumn(e, col);
            g.Children.Add(e);
        }

        private static TextBlock Num(string text, string brush)
        {
            var t = Ui.Text(text, 13, brush);
            t.HorizontalAlignment = HorizontalAlignment.Right;
            return t;
        }

        private void Header(string first)
        {
            var g = RowGrid();
            g.Margin = new Thickness(4, 0, 4, 6);
            Put(g, Ui.Text(first, 12, "MutedBrush"), 1);
            Put(g, Num("Download", "MutedBrush"), 3);
            Put(g, Num("Upload", "MutedBrush"), 4);
            Put(g, Num("Total", "MutedBrush"), 5);
            _list.Children.Add(g);
        }

        private void FillApps(Dictionary<int, Totals> apps, long from, long to, int adapter)
        {
            _list.Children.Clear();
            var rows = apps.Where(kv => kv.Value.Sum > 0).OrderByDescending(kv => kv.Value.Sum).ToList();
            _exportHeader = new[] { "App", "Path", "Downloaded (bytes)", "Uploaded (bytes)", "Total (bytes)" };
            _exportRows = rows.Select(kv =>
            {
                var a = Db.GetApp(kv.Key);
                return new[] { a?.Name ?? "Unknown", a?.Path ?? "", kv.Value.Rx.ToString(), kv.Value.Tx.ToString(), kv.Value.Sum.ToString() };
            }).ToList();
            if (rows.Count == 0)
            {
                _list.Children.Add(Ui.Empty("Nothing recorded yet", "NetPull adds up traffic every minute. Leave it running and this fills in."));
                return;
            }
            Header("App");
            long max = rows.Max(r => r.Value.Sum);
            foreach (var kv in rows)
            {
                var app = Db.GetApp(kv.Key);
                var outer = new StackPanel();
                var row = new Border { Padding = new Thickness(0, 7, 0, 7), CornerRadius = new CornerRadius(8), Background = System.Windows.Media.Brushes.Transparent, Cursor = Cursors.Hand };
                var g = RowGrid();
                Put(g, Ui.AppIcon(AppCatalog.IconFor(app), 22), 0);
                var names = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                names.Children.Add(Ui.Text(app?.Name ?? "Unknown", 13.5));
                if (!string.IsNullOrEmpty(app?.Path)) names.Children.Add(Ui.Text(app.Path, 11, "MutedBrush"));
                Put(g, names, 1);
                Put(g, UsageBar.Make(kv.Value.Rx, kv.Value.Tx, max), 2);
                Put(g, Num(Format.Size(kv.Value.Rx), "DownBrush"), 3);
                Put(g, Num(Format.Size(kv.Value.Tx), "UpBrush"), 4);
                Put(g, Num(Format.Size(kv.Value.Sum), "TextBrush"), 5);
                row.Child = g;
                row.MouseEnter += (s, e) => row.Background = Ui.B("Panel2Brush");
                row.MouseLeave += (s, e) => row.Background = System.Windows.Media.Brushes.Transparent;
                outer.Children.Add(row);

                var details = new StackPanel { Margin = new Thickness(48, 2, 4, 10) };
                outer.Children.Add(details);
                int id = kv.Key;
                async void Expand()
                {
                    details.Children.Clear();
                    if (!_expanded.Contains(id)) return;
                    details.Children.Add(Ui.Text("Talked to", 12, "MutedBrush"));
                    var hosts = await Task.Run(() => Db.HostTotals(from, to, adapter, id));
                    foreach (var h in hosts.OrderByDescending(x => x.totals.Sum).Take(12))
                    {
                        var info = IpLookup.Get(h.ip);
                        var hg = new Grid { Margin = new Thickness(0, 3, 0, 3) };
                        hg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
                        hg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
                        hg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) });
                        hg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) });
                        var hn = Ui.Text(info.Display, 12.5); hn.ToolTip = h.ip;
                        hg.Children.Add(hn);
                        var where = Ui.Text(string.Join(" · ", new[] { info.Where, info.Isp }.Where(x => !string.IsNullOrEmpty(x))), 12, "MutedBrush");
                        Grid.SetColumn(where, 1); hg.Children.Add(where);
                        var d = Num(Format.Size(h.totals.Rx), "DownBrush"); Grid.SetColumn(d, 2); hg.Children.Add(d);
                        var u = Num(Format.Size(h.totals.Tx), "UpBrush"); Grid.SetColumn(u, 3); hg.Children.Add(u);
                        details.Children.Add(hg);
                    }
                    if (hosts.Count == 0) details.Children.Add(Ui.Text("No details for this period.", 12, "MutedBrush"));
                }
                row.MouseLeftButtonUp += (s, e) =>
                {
                    if (!_expanded.Add(id)) _expanded.Remove(id);
                    Expand();
                };
                if (_expanded.Contains(id)) Expand();
                _list.Children.Add(outer);
            }
        }

        private void FillHosts(List<(string ip, int app, Totals totals)> rows)
        {
            _list.Children.Clear();
            // Group by name so all the addresses of one service add up together.
            var groups = rows.GroupBy(r => IpLookup.Get(r.ip).Display)
                .Select(gr => new
                {
                    Name = gr.Key,
                    Info = IpLookup.Get(gr.First().ip),
                    Rx = gr.Sum(x => x.totals.Rx),
                    Tx = gr.Sum(x => x.totals.Tx),
                    Apps = gr.Select(x => x.app).Distinct().ToList(),
                    Ips = gr.Select(x => x.ip).Distinct().ToList()
                })
                .Where(x => x.Rx + x.Tx > 0)
                .OrderByDescending(x => x.Rx + x.Tx).ToList();

            _exportHeader = new[] { "Host", "Addresses", "Country", "ISP", "Apps", "Downloaded (bytes)", "Uploaded (bytes)", "Total (bytes)" };
            _exportRows = groups.Select(x => new[]
            {
                x.Name, string.Join(" ", x.Ips), x.Info.Where ?? "", x.Info.Isp ?? "",
                string.Join("; ", x.Apps.Select(a => Db.GetApp(a)?.Name ?? "?")), x.Rx.ToString(), x.Tx.ToString(), (x.Rx + x.Tx).ToString()
            }).ToList();

            if (groups.Count == 0)
            {
                _list.Children.Add(Ui.Empty("Nothing recorded yet", "Websites and servers your apps talk to show up here, totalled by the hour."));
                return;
            }
            Header("Host");
            long max = groups.Max(x => x.Rx + x.Tx);
            foreach (var x in groups.Take(300))
            {
                var g = RowGrid();
                g.Margin = new Thickness(4, 6, 4, 6);
                var badge = Ui.Text(string.IsNullOrEmpty(x.Info.CountryCode) ? "·" : x.Info.CountryCode, 11, "MutedBrush", FontWeights.SemiBold);
                Put(g, badge, 0);
                var names = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                names.Children.Add(Ui.Text(x.Name, 13.5));
                string sub = string.Join(" · ", new[] { x.Info.Where, x.Info.Isp, Ui.Plural(x.Apps.Count, "app", "apps") }.Where(s => !string.IsNullOrEmpty(s)));
                names.Children.Add(Ui.Text(sub, 11, "MutedBrush"));
                names.ToolTip = string.Join("\n", x.Ips.Take(10)) + "\n" + string.Join(", ", x.Apps.Select(a => Db.GetApp(a)?.Name ?? "?"));
                Put(g, names, 1);
                Put(g, UsageBar.Make(x.Rx, x.Tx, max), 2);
                Put(g, Num(Format.Size(x.Rx), "DownBrush"), 3);
                Put(g, Num(Format.Size(x.Tx), "UpBrush"), 4);
                Put(g, Num(Format.Size(x.Rx + x.Tx), "TextBrush"), 5);
                _list.Children.Add(g);
            }
        }

        private void FillDevices(Dictionary<string, Totals> totals)
        {
            _list.Children.Clear();
            var names = NetworkModel.Devices.Where(d => d.Mac != null).GroupBy(d => d.Mac, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            var rows = totals.Where(kv => kv.Value.Sum > 0).OrderByDescending(kv => kv.Value.Sum).ToList();
            _exportHeader = new[] { "Device", "MAC", "Address", "Downloaded (bytes)", "Uploaded (bytes)", "Total (bytes)" };
            _exportRows = rows.Select(kv =>
            {
                names.TryGetValue(kv.Key, out var d);
                return new[] { d?.DisplayName ?? kv.Key, kv.Key, d?.Ip ?? "", kv.Value.Rx.ToString(), kv.Value.Tx.ToString(), kv.Value.Sum.ToString() };
            }).ToList();
            if (rows.Count == 0)
            {
                _list.Children.Add(Settings.Current.UniFi
                    ? Ui.Empty("Nothing recorded yet", "NetPull adds up each device's data from UniFi every minute. Leave it running and this fills in.")
                    : Ui.Empty("Connect UniFi to see every device", "A PC can't see other devices' traffic by itself. Add a view-only UniFi user in Settings and NetPull records how much each device uses."));
                return;
            }
            Header("Device");
            long max = rows.Max(r => r.Value.Sum);
            foreach (var kv in rows)
            {
                names.TryGetValue(kv.Key, out var d);
                var g = RowGrid();
                g.Margin = new Thickness(4, 6, 4, 6);
                Put(g, Ui.Icon(TopologyMap.Glyph(d?.Kind ?? "device"), 15, "DownBrush"), 0);
                var sp = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                sp.Children.Add(Ui.Text(d?.DisplayName ?? kv.Key, 13.5));
                sp.Children.Add(Ui.Text(string.Join("   ", new[] { d?.Ip, kv.Key }.Where(x => !string.IsNullOrEmpty(x))), 11, "MutedBrush"));
                Put(g, sp, 1);
                Put(g, UsageBar.Make(kv.Value.Rx, kv.Value.Tx, max), 2);
                Put(g, Num(Format.Size(kv.Value.Rx), "DownBrush"), 3);
                Put(g, Num(Format.Size(kv.Value.Tx), "UpBrush"), 4);
                Put(g, Num(Format.Size(kv.Value.Sum), "TextBrush"), 5);
                _list.Children.Add(g);
            }
        }

        private void Export()
        {
            try
            {
                var (from, to) = Periods.Range(_period);
                string name = "NetPull " + (_devices ? "devices" : _hosts ? "hosts" : "apps") + " " + from.ToString("yyyy-MM-dd") + " to " + to.AddDays(-1).ToString("yyyy-MM-dd") + ".csv";
                string saved = Csv.Save(name, _exportHeader, _exportRows);
                if (saved != null) App.Instance.Tray?.Notify("Exported", saved);
            }
            catch (Exception ex) { MessageBox.Show("Couldn't export: " + ex.Message, "NetPull"); }
        }
    }
}
