using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using NetPull.Core;
using NetPull.Monitor;

namespace NetPull.UI
{
    /// <summary>The live and history graph, with which apps were busy at any point you click.</summary>
    public sealed class GraphPage : IPage
    {
        public string Key => "graph";
        public string Title => "Graph";
        public string Glyph => Ui.GlyphGraph;
        public FrameworkElement View { get; }

        private static readonly string[] RangeNames = { "Live", "3 hours", "24 hours", "Week", "Month" };
        private static readonly int[] RangeSeconds = { 300, 3 * 3600, 86400, 7 * 86400, 30 * 86400 };
        private static readonly int[] RangeStep = { 1, 60, 300, 3600, 6 * 3600 };

        private int _range;
        private long _end;   // end of the history window (unix), 0 = now
        private readonly Graph _graph = new Graph();
        private readonly TextBlock _when = Ui.Text("", 13, "MutedBrush");
        private readonly Button _back, _forward, _now;
        private readonly TextBlock _stat1Label = Ui.Text("", 12, "MutedBrush"), _stat1 = Ui.Text("", 20, "DownBrush", FontWeights.SemiBold);
        private readonly TextBlock _stat2Label = Ui.Text("", 12, "MutedBrush"), _stat2 = Ui.Text("", 20, "UpBrush", FontWeights.SemiBold);
        private readonly TextBlock _stat3 = Ui.Text("", 20, "TextBrush", FontWeights.SemiBold);
        private readonly TextBlock _stat4 = Ui.Text("", 15, "TextBrush", FontWeights.SemiBold);
        private readonly TextBlock _appsTitle = Ui.Text("", 14, "TextBrush", FontWeights.SemiBold);
        private readonly StackPanel _apps = new StackPanel();
        private List<SecondSample> _liveSamples = new List<SecondSample>();
        private DateTime _lastHistory = DateTime.MinValue;
        private bool _loading;
        private bool _network = Settings.Current.GraphSource == "network";
        private readonly TextBlock _stat3Label = Ui.Text("Since NetPull started", 12, "MutedBrush");
        private readonly TextBlock _stat4Label = Ui.Text("Busiest app", 12, "MutedBrush");
        private ComboBox _picker;

        public GraphPage()
        {
            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(3, GridUnitType.Star), MinHeight = 220 });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(2, GridUnitType.Star), MinHeight = 140 });

            // Title and adapter filter
            var head = new DockPanel { Margin = new Thickness(0, 0, 0, 14) };
            var picker = AdapterPicker.Create();
            _picker = picker;
            var headRight = new StackPanel { Orientation = Orientation.Horizontal };
            DockPanel.SetDock(headRight, Dock.Right);
            var src = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
            src.Children.Add(Ui.Chip("This PC", "graph-src", !_network, () => SetSource(false)));
            src.Children.Add(Ui.Chip("All devices", "graph-src", _network, () => SetSource(true)));
            headRight.Children.Add(src);
            headRight.Children.Add(picker);
            head.Children.Add(headRight);
            head.Children.Add(Ui.Text("Graph", 24, "TextBrush", FontWeights.SemiBold));
            root.Children.Add(head);
            AdapterPicker.Changed += () => { _lastHistory = DateTime.MinValue; Refresh(); };

            // Range chips and history navigation
            var bar = new DockPanel { Margin = new Thickness(0, 0, 0, 14) };
            Grid.SetRow(bar, 1);
            var chips = new StackPanel { Orientation = Orientation.Horizontal };
            for (int i = 0; i < RangeNames.Length; i++)
            {
                int idx = i;
                chips.Children.Add(Ui.Chip(RangeNames[i], "graph-range", i == 0, () => { _range = idx; _end = 0; _graph.Selected = -1; _lastHistory = DateTime.MinValue; Refresh(); }));
            }
            var navs = new StackPanel { Orientation = Orientation.Horizontal };
            DockPanel.SetDock(navs, Dock.Right);
            _back = Ui.Button("", () => Move(-1), "GhostButton", Ui.GlyphBack);
            _forward = Ui.Button("", () => Move(1), "GhostButton", Ui.GlyphForward);
            _now = Ui.Button("Now", () => { _end = 0; _lastHistory = DateTime.MinValue; Refresh(); }, "GhostButton");
            _back.ToolTip = "Earlier"; _forward.ToolTip = "Later";
            _when.Margin = new Thickness(0, 0, 10, 0);
            navs.Children.Add(_when);
            navs.Children.Add(_back);
            navs.Children.Add(_forward);
            navs.Children.Add(_now);
            bar.Children.Add(navs);
            bar.Children.Add(chips);
            root.Children.Add(bar);

            // Stats
            var stats = new UniformGrid { Columns = 4, Margin = new Thickness(-6, 0, -6, 14) };
            Grid.SetRow(stats, 2);
            stats.Children.Add(Stat(_stat1Label, _stat1));
            stats.Children.Add(Stat(_stat2Label, _stat2));
            stats.Children.Add(Stat(_stat3Label, _stat3));
            stats.Children.Add(Stat(_stat4Label, _stat4));
            root.Children.Add(stats);

            // Graph
            var gpanel = new DockPanel();
            var legend = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(70, 0, 0, 8) };
            legend.Children.Add(Ui.Legend("DownBrush", "Download"));
            legend.Children.Add(Ui.Legend("UpBrush", "Upload"));
            var hint = Ui.Text("Click the graph to see which apps were busy", 12, "MutedBrush");
            legend.Children.Add(hint);
            DockPanel.SetDock(legend, Dock.Top);
            gpanel.Children.Add(legend);
            gpanel.Children.Add(_graph);
            var gcard = Ui.Card(gpanel, 14);
            Grid.SetRow(gcard, 3);
            root.Children.Add(gcard);
            _graph.PointClicked += i => ShowAppsAt(i);

            // Apps at the selected point
            var acard = new DockPanel();
            _appsTitle.Margin = new Thickness(0, 0, 0, 8);
            DockPanel.SetDock(_appsTitle, Dock.Top);
            acard.Children.Add(_appsTitle);
            acard.Children.Add(new ScrollViewer { Content = _apps, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
            var acardBorder = Ui.Card(acard, 16);
            acardBorder.Margin = new Thickness(0, 14, 0, 0);
            Grid.SetRow(acardBorder, 4);
            root.Children.Add(acardBorder);

            View = root;
        }

        private static FrameworkElement Stat(TextBlock label, TextBlock value)
        {
            var sp = new StackPanel();
            sp.Children.Add(label);
            value.Margin = new Thickness(0, 4, 0, 0);
            sp.Children.Add(value);
            var c = Ui.Card(sp, 14);
            c.Margin = new Thickness(6, 0, 6, 0);
            return c;
        }

        public void Shown()
        {
            _lastHistory = DateTime.MinValue;
            Refresh();
        }

        private void SetSource(bool network)
        {
            _network = network;
            Settings.Current.GraphSource = network ? "network" : "pc";
            Settings.Save();
            _graph.Selected = -1;
            _lastHistory = DateTime.MinValue;
            Refresh();
        }

        public void Tick(SecondSample s)
        {
            if (_network)
            {
                if (_range == 0) { if (s.Time % 5 == 0) RefreshNetworkLive(); }
                else if (_end == 0 && DateTime.UtcNow - _lastHistory > TimeSpan.FromSeconds(60)) Refresh();
                var devs = NetworkModel.Devices;
                _stat3.Text = devs.Count(d => d.Group == "lan" && d.Online) + " online";
                return;
            }
            if (_range == 0) RefreshLive();
            else if (_end == 0 && DateTime.UtcNow - _lastHistory > TimeSpan.FromSeconds(60)) Refresh();
            _stat3.Text = Format.Size(TrafficMonitor.Instance.SessionRx + TrafficMonitor.Instance.SessionTx);
            if (_range == 0)
            {
                var (rx, tx) = s.For(AdapterPicker.Current);
                _stat1.Text = Format.Rate(rx);
                _stat2.Text = Format.Rate(tx);
            }
        }

        private void Move(int dir)
        {
            if (_range == 0) return;
            long now = Format.NowUnix;
            long end = _end == 0 ? now : _end;
            end += dir * RangeSeconds[_range];
            _end = end >= now ? 0 : end;
            _graph.Selected = -1;
            _lastHistory = DateTime.MinValue;
            Refresh();
        }

        private void Refresh()
        {
            bool live = _range == 0;
            _back.Visibility = live ? Visibility.Collapsed : Visibility.Visible;
            _forward.Visibility = live ? Visibility.Collapsed : Visibility.Visible;
            _now.Visibility = !live && _end != 0 ? Visibility.Visible : Visibility.Collapsed;
            _forward.IsEnabled = _end != 0;
            _stat1Label.Text = live ? "Download now" : "Downloaded";
            _stat2Label.Text = live ? "Upload now" : "Uploaded";
            _stat3Label.Text = _network ? "Devices" : "Since NetPull started";
            _stat4Label.Text = _network ? "Busiest device" : "Busiest app";
            if (_picker != null) _picker.Visibility = _network ? Visibility.Collapsed : Visibility.Visible;
            if (_network && live) { _when.Text = "Last 5 minutes, from UniFi"; RefreshNetworkLive(); ShowTopApps(); }
            else if (live) { _when.Text = "Last 5 minutes"; RefreshLive(); ShowTopApps(); }
            else _ = RefreshHistory();
        }

        private void RefreshLive()
        {
            int adapter = AdapterPicker.Current;
            _liveSamples = TrafficMonitor.Instance.LiveSamples(300);
            long now = Format.NowUnix;
            var down = new double[300];
            var up = new double[300];
            long start = now - 299;
            foreach (var s in _liveSamples)
            {
                long i = s.Time - start;
                if (i < 0 || i >= 300) continue;
                var (rx, tx) = s.For(adapter);
                down[i] = rx; up[i] = tx;
            }
            if (_graph.Selected >= 0) _graph.Selected = Math.Max(0, _graph.Selected - (int)(start - _graph.StartUnix));
            _graph.SetData(down, up, start, 1);
            if (_graph.Selected < 0 && now % 5 == 0) ShowTopApps();
        }

        private List<NetSample> _netSamples = new List<NetSample>();

        private void RefreshNetworkLive()
        {
            _netSamples = NetworkModel.Samples(310);
            long now = Format.NowUnix;
            long start = now - now % 10 - 290;
            var down = new double[30];
            var up = new double[30];
            foreach (var s in _netSamples)
            {
                long i = (s.Time - start) / 10;
                if (i < 0 || i >= 30) continue;
                down[i] += s.Down / (double)Math.Max(1, s.Seconds);
                up[i] += s.Up / (double)Math.Max(1, s.Seconds);
            }
            _graph.SetData(down, up, start, 10);
            var last = _netSamples.LastOrDefault();
            _stat1.Text = Format.Rate(last == null ? 0 : last.Down / (double)Math.Max(1, last.Seconds));
            _stat2.Text = Format.Rate(last == null ? 0 : last.Up / (double)Math.Max(1, last.Seconds));
            if (_graph.Selected < 0) ShowTopApps();
        }

        private (long from, long to) Span()
        {
            int step = RangeStep[_range];
            long end = _end == 0 ? Format.NowUnix : _end;
            end = end - end % step + step;   // round up to the next bucket
            return (end - RangeSeconds[_range], end);
        }

        private async Task RefreshHistory()
        {
            if (_loading) return;
            _loading = true;
            try
            {
                _lastHistory = DateTime.UtcNow;
                var (from, to) = Span();
                int step = RangeStep[_range];
                int adapter = AdapterPicker.Current;
                bool network = _network;
                var (rx, tx) = await Task.Run(() => network ? Db.DeviceSeries(from, to, step) : Db.Series(from, to, step, adapter));
                var down = rx.Select(v => v / (double)step).ToArray();
                var up = tx.Select(v => v / (double)step).ToArray();
                _graph.SetData(down, up, from, step);
                var f = Format.FromUnix(from).ToLocalTime();
                var t = Format.FromUnix(to).ToLocalTime();
                var c = CultureInfo.CurrentCulture;
                _when.Text = step >= 3600 ? f.ToString("d MMM", c) + " – " + t.ToString("d MMM", c) : f.ToString("ddd d MMM, HH:mm", c) + " – " + t.ToString("HH:mm", c);
                _stat1.Text = Format.Size(rx.Sum());
                _stat2.Text = Format.Size(tx.Sum());
                if (_graph.Selected >= 0) ShowAppsAt(_graph.Selected); else ShowTopApps();
            }
            catch (Exception ex) { Log.Write("Graph history: " + ex.Message); }
            finally { _loading = false; }
        }

        /// <summary>The busiest apps across the whole view.</summary>
        private async void ShowTopApps()
        {
            if (_network)
            {
                if (!Settings.Current.UniFi || !NetworkModel.UniFiOk)
                {
                    _appsTitle.Text = "Connect UniFi to see every device";
                    _apps.Children.Clear();
                    _apps.Children.Add(Ui.Wrap("A PC can't see other devices' traffic by itself. Add a view-only UniFi user in Settings and this graph shows your whole network, with the busiest devices here.", 13));
                    _stat4.Text = "-";
                    return;
                }
                var dt = new Dictionary<string, Totals>(StringComparer.OrdinalIgnoreCase);
                if (_range == 0)
                {
                    foreach (var sm in _netSamples)
                        foreach (var kv in sm.Devices)
                        {
                            dt.TryGetValue(kv.Key, out var t);
                            t.Rx += kv.Value.down; t.Tx += kv.Value.up;
                            dt[kv.Key] = t;
                        }
                    _appsTitle.Text = "Busiest devices in the last 5 minutes";
                }
                else
                {
                    var (f, t2) = Span();
                    dt = await Task.Run(() => Db.DeviceTotals(f, t2));
                    _appsTitle.Text = "Busiest devices in this view";
                }
                FillDevices(dt);
                return;
            }
            int adapter = AdapterPicker.Current;
            Dictionary<int, Totals> totals;
            if (_range == 0)
            {
                totals = new Dictionary<int, Totals>();
                foreach (var s in _liveSamples)
                    foreach (var a in s.Apps)
                    {
                        if (adapter != 0 && a.Adapter != adapter) continue;
                        totals.TryGetValue(a.App, out var t);
                        t.Rx += a.Rx; t.Tx += a.Tx;
                        totals[a.App] = t;
                    }
                _appsTitle.Text = "Busiest apps in the last 5 minutes";
            }
            else
            {
                var (from, to) = Span();
                totals = await Task.Run(() => Db.AppTotals(from, to, adapter));
                _appsTitle.Text = "Busiest apps in this view";
            }
            Fill(totals);
        }

        private async void ShowAppsAt(int index)
        {
            if (_network)
            {
                long ts = _graph.StartUnix + (long)index * _graph.Step;
                var whenN = Format.FromUnix(ts).ToLocalTime();
                var dt = new Dictionary<string, Totals>(StringComparer.OrdinalIgnoreCase);
                if (_range == 0)
                {
                    foreach (var sm in _netSamples.Where(x => x.Time >= ts && x.Time < ts + 10))
                        foreach (var kv in sm.Devices)
                        {
                            dt.TryGetValue(kv.Key, out var t);
                            t.Rx += kv.Value.down; t.Tx += kv.Value.up;
                            dt[kv.Key] = t;
                        }
                    _appsTitle.Text = "Devices around " + whenN.ToString("HH:mm:ss", CultureInfo.CurrentCulture);
                }
                else
                {
                    int step = _graph.Step;
                    dt = await Task.Run(() => Db.DeviceTotals(ts, ts + step));
                    _appsTitle.Text = "Devices at " + whenN.ToString(step >= 3600 ? "ddd d MMM, HH:mm" : "HH:mm", CultureInfo.CurrentCulture);
                }
                FillDevices(dt);
                return;
            }
            int adapter = AdapterPicker.Current;
            Dictionary<int, Totals> totals = new Dictionary<int, Totals>();
            long t0 = _graph.StartUnix + (long)index * _graph.Step;
            var when = Format.FromUnix(t0).ToLocalTime();
            if (_range == 0)
            {
                // Take a few seconds either side so a click doesn't have to be pixel perfect.
                foreach (var s in _liveSamples.Where(x => Math.Abs(x.Time - t0) <= 2))
                    foreach (var a in s.Apps)
                    {
                        if (adapter != 0 && a.Adapter != adapter) continue;
                        totals.TryGetValue(a.App, out var t);
                        t.Rx += a.Rx; t.Tx += a.Tx;
                        totals[a.App] = t;
                    }
                _appsTitle.Text = "Apps around " + when.ToString("HH:mm:ss", CultureInfo.CurrentCulture);
            }
            else
            {
                int step = _graph.Step;
                totals = await Task.Run(() => Db.AppTotals(t0, t0 + step, adapter));
                _appsTitle.Text = "Apps at " + (step >= 3600 ? when.ToString("ddd d MMM, HH:mm", CultureInfo.CurrentCulture) : when.ToString("HH:mm", CultureInfo.CurrentCulture))
                                  + " (" + (step >= 3600 ? step / 3600 + " hr" : step / 60 + " min") + ")";
            }
            Fill(totals);
        }

        private void FillDevices(Dictionary<string, Totals> totals)
        {
            _apps.Children.Clear();
            var devs = NetworkModel.Devices.Where(d => d.Mac != null).GroupBy(d => d.Mac, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            var list = totals.Where(kv => kv.Value.Sum > 0).OrderByDescending(kv => kv.Value.Sum).Take(10).ToList();
            string Name(string mac) => devs.TryGetValue(mac, out var d) ? d.DisplayName : mac;
            _stat4.Text = list.Count == 0 ? "Nothing yet" : Name(list[0].Key);
            if (list.Count == 0) { _apps.Children.Add(Ui.Text("No traffic here.", 13, "MutedBrush")); return; }
            long max = list.Max(kv => kv.Value.Sum);
            foreach (var kv in list)
            {
                devs.TryGetValue(kv.Key, out var d);
                var g = new Grid { Margin = new Thickness(0, 4, 0, 4) };
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) });
                g.Children.Add(Ui.Icon(TopologyMap.Glyph(d?.Kind ?? "device"), 15, "DownBrush"));
                var name = Ui.Text(Name(kv.Key), 13.5);
                name.ToolTip = d?.Ip;
                Grid.SetColumn(name, 1); g.Children.Add(name);
                var bar = UsageBar.Make(kv.Value.Rx, kv.Value.Tx, max);
                Grid.SetColumn(bar, 2); g.Children.Add(bar);
                var dn = Ui.Text("↓ " + Format.Size(kv.Value.Rx), 12.5, "DownBrush"); dn.HorizontalAlignment = HorizontalAlignment.Right;
                Grid.SetColumn(dn, 3); g.Children.Add(dn);
                var up = Ui.Text("↑ " + Format.Size(kv.Value.Tx), 12.5, "UpBrush"); up.HorizontalAlignment = HorizontalAlignment.Right;
                Grid.SetColumn(up, 4); g.Children.Add(up);
                _apps.Children.Add(g);
            }
        }

        private void Fill(Dictionary<int, Totals> totals)
        {
            _apps.Children.Clear();
            var list = totals.Where(kv => kv.Value.Sum > 0).OrderByDescending(kv => kv.Value.Sum).Take(10).ToList();
            var top = list.FirstOrDefault();
            var topApp = top.Key == 0 ? null : Db.GetApp(top.Key);
            _stat4.Text = topApp?.Name ?? "Nothing yet";
            if (list.Count == 0)
            {
                _apps.Children.Add(Ui.Text("No traffic here.", 13, "MutedBrush"));
                return;
            }
            long max = list.Max(kv => kv.Value.Sum);
            foreach (var kv in list)
            {
                var app = Db.GetApp(kv.Key);
                var g = new Grid { Margin = new Thickness(0, 4, 0, 4) };
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) });
                g.Children.Add(Ui.AppIcon(AppCatalog.IconFor(app)));
                var name = Ui.Text(app?.Name ?? "Unknown", 13.5);
                name.ToolTip = app?.Path;
                Grid.SetColumn(name, 1); g.Children.Add(name);
                var bar = UsageBar.Make(kv.Value.Rx, kv.Value.Tx, max);
                Grid.SetColumn(bar, 2); g.Children.Add(bar);
                var d = Ui.Text("↓ " + Format.Size(kv.Value.Rx), 12.5, "DownBrush"); d.HorizontalAlignment = HorizontalAlignment.Right;
                Grid.SetColumn(d, 3); g.Children.Add(d);
                var u = Ui.Text("↑ " + Format.Size(kv.Value.Tx), 12.5, "UpBrush"); u.HorizontalAlignment = HorizontalAlignment.Right;
                Grid.SetColumn(u, 4); g.Children.Add(u);
                _apps.Children.Add(g);
            }
        }
    }

    /// <summary>A thin two-colour bar: download then upload, scaled to the biggest row.</summary>
    public static class UsageBar
    {
        public static FrameworkElement Make(long rx, long tx, long max)
        {
            var g = new Grid { Height = 6, Margin = new Thickness(12, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
            double total = Math.Max(1, max);
            double fd = rx / total, fu = tx / total;
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0, fd), GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0, fu), GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0, 1 - fd - fu), GridUnitType.Star) });
            var back = new Border { Background = Ui.B("Panel2Brush"), CornerRadius = new CornerRadius(3) };
            Grid.SetColumnSpan(back, 3);
            g.Children.Add(back);
            var d = new Border { Background = Ui.B("DownBrush"), CornerRadius = new CornerRadius(3, 0, 0, 3) };
            g.Children.Add(d);
            var u = new Border { Background = Ui.B("UpBrush"), CornerRadius = new CornerRadius(0, 3, 3, 0) };
            Grid.SetColumn(u, 1);
            g.Children.Add(u);
            return g;
        }
    }
}
