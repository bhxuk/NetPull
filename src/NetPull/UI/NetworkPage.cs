using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using NetPull.Core;
using NetPull.Monitor;

namespace NetPull.UI
{
    /// <summary>The whole network: a map of everything connected, what each device is, and (with UniFi) how much it uses.</summary>
    public sealed class NetworkPage : IPage
    {
        public string Key => "network";
        public string Title => "Network";
        public string Glyph => Ui.GlyphDevices;
        public FrameworkElement View { get; }

        private readonly TopologyMap _map = new TopologyMap();
        private readonly StackPanel _list = new StackPanel();
        private readonly ScrollViewer _listScroll;
        private readonly StackPanel _details = new StackPanel();
        private readonly TextBlock _status = Ui.Text("", 12.5, "MutedBrush");
        private readonly Button _scan;
        private bool _listView;
        private string _selected;
        private Dictionary<string, Totals> _today = new Dictionary<string, Totals>(StringComparer.OrdinalIgnoreCase);
        private DateTime _todayAt = DateTime.MinValue;
        private readonly DispatcherTimer _throttle;
        private double _progress;

        public NetworkPage()
        {
            var root = new DockPanel();

            var head = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
            DockPanel.SetDock(head, Dock.Top);
            var right = new StackPanel { Orientation = Orientation.Horizontal };
            DockPanel.SetDock(right, Dock.Right);
            _status.Margin = new Thickness(0, 0, 14, 0);
            right.Children.Add(_status);
            _scan = Ui.Button("Scan now", () => { _ = LanWatch.ScanNow(); UpdateStatus(); }, "AccentButton", Ui.GlyphRefresh);
            right.Children.Add(_scan);
            head.Children.Add(right);
            head.Children.Add(Ui.Text("Network", 24, "TextBrush", FontWeights.SemiBold));
            root.Children.Add(head);

            var bar = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
            DockPanel.SetDock(bar, Dock.Top);
            var chips = new StackPanel { Orientation = Orientation.Horizontal };
            chips.Children.Add(Ui.Chip("Map", "net-view", true, () => { _listView = false; Refresh(); }));
            chips.Children.Add(Ui.Chip("List", "net-view", false, () => { _listView = true; Refresh(); }));
            DockPanel.SetDock(chips, Dock.Left);
            bar.Children.Add(chips);
            var hint = Ui.Text("Click a device for details. Ctrl + mouse wheel zooms the map.", 12.5, "MutedBrush");
            hint.Margin = new Thickness(14, 0, 0, 0);
            bar.Children.Add(hint);
            root.Children.Add(bar);

            var body = new Grid();
            body.ColumnDefinitions.Add(new ColumnDefinition());
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(340) });
            _listScroll = new ScrollViewer { Content = _list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Visibility = Visibility.Collapsed };
            var left = new Grid();
            left.Children.Add(_map);
            left.Children.Add(_listScroll);
            body.Children.Add(Ui.Card(left, 6));
            var detailCard = Ui.Card(new ScrollViewer { Content = _details, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }, 16);
            detailCard.Margin = new Thickness(14, 0, 0, 0);
            Grid.SetColumn(detailCard, 1);
            body.Children.Add(detailCard);
            root.Children.Add(body);
            View = root;

            _map.Selected += d => { _selected = d.Key; Refresh(); };
            _throttle = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
            _throttle.Tick += (s, e) => { _throttle.Stop(); if (View.IsVisible) Refresh(); };
            NetworkModel.Changed += () => View.Dispatcher.BeginInvoke(new Action(() => { if (!_throttle.IsEnabled) _throttle.Start(); }));
            LanWatch.Progress += p => _progress = p;
            LanWatch.ScanFinished += () => View.Dispatcher.BeginInvoke(new Action(UpdateStatus));
        }

        public void Shown()
        {
            Refresh();
            UpdateStatus();
        }

        public void Tick(SecondSample s)
        {
            if (LanWatch.Scanning || s.Time % 10 == 0) UpdateStatus();
        }

        private void UpdateStatus()
        {
            _scan.IsEnabled = !LanWatch.Scanning;
            string scan = LanWatch.Scanning ? "Scanning... " + (int)(_progress * 100) + "%"
                : LanWatch.LastScan == DateTime.MinValue ? "First scan starts shortly"
                : "Scanned " + Format.Ago(LanWatch.LastScan);
            string uni = Settings.Current.UniFi ? "UniFi: " + NetworkModel.UniFiStatus : "UniFi not connected";
            _status.Text = scan + "   ·   " + uni;
        }

        private void Refresh()
        {
            var devices = NetworkModel.Devices;
            if (DateTime.UtcNow - _todayAt > TimeSpan.FromSeconds(30))
            {
                _todayAt = DateTime.UtcNow;
                try { _today = Db.DeviceTotals(Format.LocalToUnix(DateTime.Today), Format.NowUnix + 60); } catch { }
            }
            _map.SelectedKey = _selected;
            _map.Visibility = _listView ? Visibility.Collapsed : Visibility.Visible;
            _listScroll.Visibility = _listView ? Visibility.Visible : Visibility.Collapsed;
            if (_listView) FillList(devices); else _map.Show(devices);
            // Don't rebuild the details while someone is typing in them.
            if (_details.IsKeyboardFocusWithin && _detailsKey == _selected) return;
            _detailsKey = _selected;
            FillDetails(devices.FirstOrDefault(d => d.Key == _selected), devices);
        }

        private string _detailsKey;

        // ---------------- list ----------------

        private void FillList(List<NetDevice> devices)
        {
            _list.Children.Clear();
            var hdr = ListRow();
            hdr.Margin = new Thickness(8, 4, 8, 6);
            Put(hdr, Ui.Text("Device", 12, "MutedBrush"), 1);
            Put(hdr, Ui.Text("Connection", 12, "MutedBrush"), 2);
            Put(hdr, Right(Ui.Text("↓ now", 12, "MutedBrush")), 3);
            Put(hdr, Right(Ui.Text("↑ now", 12, "MutedBrush")), 4);
            Put(hdr, Right(Ui.Text("Today", 12, "MutedBrush")), 5);
            _list.Children.Add(hdr);
            foreach (var d in devices.OrderByDescending(x => x.IsThisPc).ThenByDescending(x => x.Online).ThenByDescending(x => x.DownRate + x.UpRate).ThenBy(x => x.Group).ThenBy(x => x.DisplayName))
            {
                var g = ListRow();
                var icon = new TextBlock { Text = TopologyMap.Glyph(d.Kind), FontFamily = Ui.IconFont, FontSize = 15, Foreground = Ui.B(d.Online ? (d.Group == "tailscale" ? "UpBrush" : "DownBrush") : "MutedBrush"), VerticalAlignment = VerticalAlignment.Center };
                Put(g, icon, 0);
                var names = new StackPanel();
                names.Children.Add(Ui.Text(d.DisplayName, 13.5, d.Online ? "TextBrush" : "MutedBrush", FontWeights.SemiBold));
                names.Children.Add(Ui.Text(string.Join("   ", new[] { d.Ip, d.Online ? null : "offline", d.Trusted ? null : "not trusted" }.Where(x => !string.IsNullOrEmpty(x))), 11.5, "MutedBrush"));
                Put(g, names, 1);
                Put(g, Ui.Text(d.Connection ?? (d.Group == "tailscale" ? "Tailscale" : d.IsInfra ? TopologyMap.KindName(d.Kind) : ""), 12.5, "MutedBrush"), 2);
                Put(g, Right(Ui.Text(d.DownRate > 0 ? Format.Rate(d.DownRate) : "", 12.5, "DownBrush")), 3);
                Put(g, Right(Ui.Text(d.UpRate > 0 ? Format.Rate(d.UpRate) : "", 12.5, "UpBrush")), 4);
                string today = d.Mac != null && _today.TryGetValue(d.Mac, out var t) ? Format.Size(t.Sum) : "";
                Put(g, Right(Ui.Text(today, 12.5)), 5);
                var row = new Border { Child = g, Padding = new Thickness(8, 7, 8, 7), CornerRadius = new CornerRadius(8), Cursor = Cursors.Hand, Background = Ui.B(d.Key == _selected ? "Panel2Brush" : "PanelBrush") };
                string key = d.Key;
                row.MouseEnter += (s, e) => row.Background = Ui.B("Panel2Brush");
                row.MouseLeave += (s, e) => row.Background = Ui.B(key == _selected ? "Panel2Brush" : "PanelBrush");
                row.MouseLeftButtonUp += (s, e) => { _selected = key; Refresh(); };
                _list.Children.Add(row);
            }
        }

        private static Grid ListRow()
        {
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
            return g;
        }

        private static void Put(Grid g, UIElement e, int col) { Grid.SetColumn(e, col); g.Children.Add(e); }
        private static FrameworkElement Right(TextBlock t) { t.HorizontalAlignment = HorizontalAlignment.Right; return t; }

        // ---------------- details ----------------

        private void FillDetails(NetDevice d, List<NetDevice> all)
        {
            _details.Children.Clear();
            if (d == null) { Summary(all); return; }

            var head = new Grid { Margin = new Thickness(0, 0, 0, 12) };
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(46) });
            head.ColumnDefinitions.Add(new ColumnDefinition());
            head.Children.Add(new Border
            {
                Width = 38, Height = 38, CornerRadius = new CornerRadius(19), Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x22, 0x22, 0xE1, 0xF0)),
                HorizontalAlignment = HorizontalAlignment.Left,
                Child = new TextBlock { Text = TopologyMap.Glyph(d.Kind), FontFamily = Ui.IconFont, FontSize = 17, Foreground = Ui.B("DownBrush"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
            });
            var title = new StackPanel();
            var name = Ui.Wrap(d.DisplayName, 17);
            name.Foreground = Ui.B("TextBrush");
            name.FontWeight = FontWeights.SemiBold;
            title.Children.Add(name);
            title.Children.Add(Ui.Text(TopologyMap.KindName(d.Kind) + (d.Online ? "" : "  ·  offline"), 12.5, d.Online ? "MutedBrush" : "WarnBrush"));
            Grid.SetColumn(title, 1);
            head.Children.Add(title);
            _details.Children.Add(head);

            if (d.Record != null && !d.IsThisPc)
            {
                var rename = Ui.Button("Rename", () => Rename(d), null, Ui.GlyphEdit);
                rename.HorizontalAlignment = HorizontalAlignment.Left;
                rename.Margin = new Thickness(0, 0, 0, 10);
                _details.Children.Add(rename);
            }

            // Speeds
            if (d.DownRate + d.UpRate > 0 || d.Group == "tailscale" || NetworkModel.UniFiOk || d.IsThisPc)
            {
                var sp = new Grid { Margin = new Thickness(0, 4, 0, 8) };
                sp.ColumnDefinitions.Add(new ColumnDefinition()); sp.ColumnDefinitions.Add(new ColumnDefinition());
                sp.Children.Add(Big("Download now", Format.Rate(d.DownRate), "DownBrush"));
                var up = Big("Upload now", Format.Rate(d.UpRate), "UpBrush");
                Grid.SetColumn(up, 1);
                sp.Children.Add(up);
                _details.Children.Add(sp);
                if (d.Group == "tailscale")
                    _details.Children.Add(Ui.Wrap("Speeds and totals for Tailscale machines are what they send to and from this PC.", 11.5));
            }

            // Facts
            _details.Children.Add(Ui.Heading("Details"));
            Fact("Address", d.Ip);
            Fact("MAC", d.Mac);
            Fact("Maker", d.Vendor);
            Fact("Model", d.Model);
            Fact("System", d.Os);
            Fact("Host name", d.Hostname != d.DisplayName ? d.Hostname : null);
            Fact("Connected by", d.Connection);
            Fact("Wi-Fi", d.Ssid != null ? d.Ssid + (d.Band != null ? ", " + d.Band : "") : null);
            Fact("Signal", d.Signal != 0 ? d.Signal + " dBm (" + (d.Signal >= -60 ? "good" : d.Signal >= -70 ? "fair" : "weak") + ")" : null);
            Fact("Connected for", d.Uptime > 0 ? Duration(d.Uptime) : null);
            Fact("Ping", d.PingMs.HasValue ? d.PingMs + " ms" : null);
            if (d.Record != null)
            {
                Fact("First seen", Format.Ago(Format.FromUnix(d.Record.FirstSeen)));
                Fact("Last seen", d.Online ? "Now" : Format.Ago(Format.FromUnix(d.Record.LastSeen)));
            }

            // Data used (UniFi)
            if (d.Mac != null && d.Group == "lan" && NetworkModel.UniFiOk)
            {
                _details.Children.Add(Ui.Heading("Data used"));
                var startMonth = AlertEngine.CapPeriodStart();
                var month = Db.DeviceTotals(Format.LocalToUnix(startMonth), Format.NowUnix + 60);
                _today.TryGetValue(d.Mac, out var today);
                month.TryGetValue(d.Mac, out var m);
                Fact("Today", "↓ " + Format.Size(today.Rx) + "   ↑ " + Format.Size(today.Tx));
                Fact("This month", "↓ " + Format.Size(m.Rx) + "   ↑ " + Format.Size(m.Tx));
                long now = Format.NowUnix, end = now - now % 1800 + 1800;
                var (rx, tx) = Db.DeviceSeries(end - 86400, end, 1800, d.Mac);
                var graph = new Graph { Compact = true, Height = 70, Margin = new Thickness(0, 8, 0, 2), IsHitTestVisible = false };
                graph.SetData(rx.Select(v => v / 1800.0).ToArray(), tx.Select(v => v / 1800.0).ToArray(), end - 86400, 1800);
                _details.Children.Add(Ui.Card(graph, 4));
                _details.Children.Add(Ui.Text("Last 24 hours", 11, "MutedBrush"));
            }
            else if (d.Group == "lan" && !d.IsThisPc && !NetworkModel.UniFiOk)
            {
                var note = Ui.Wrap("Connect your UniFi console in Settings to see how much data this device uses.", 12);
                note.Margin = new Thickness(0, 12, 0, 0);
                _details.Children.Add(note);
            }

            // What it's doing (DPI)
            if (d.Dpi != null && d.Dpi.Count > 0)
            {
                _details.Children.Add(Ui.Heading("What it's doing"));
                long max = d.Dpi.Max(c => c.down + c.up);
                foreach (var c in d.Dpi.Take(6))
                {
                    var g = new Grid { Margin = new Thickness(0, 3, 0, 3) };
                    g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
                    g.ColumnDefinitions.Add(new ColumnDefinition());
                    g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
                    g.Children.Add(Ui.Text(c.category, 12.5));
                    var bar = UsageBar.Make(c.down, c.up, max);
                    Grid.SetColumn(bar, 1); g.Children.Add(bar);
                    var v = Ui.Text(Format.Size(c.down + c.up), 12, "MutedBrush"); v.HorizontalAlignment = HorizontalAlignment.Right;
                    Grid.SetColumn(v, 2); g.Children.Add(v);
                    _details.Children.Add(g);
                }
            }

            // Services
            if (d.Group == "lan" && !string.IsNullOrEmpty(d.Ip) && !d.IsThisPc)
            {
                _details.Children.Add(Ui.Heading("Services"));
                if (!d.PortsKnown) _details.Children.Add(Ui.Wrap("Not checked yet.", 12.5));
                else if (d.Ports.Count == 0) _details.Children.Add(Ui.Wrap("No common services answered.", 12.5));
                foreach (int p in d.Ports)
                {
                    var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
                    var port = Ui.Text(p.ToString(), 12, "MutedBrush");
                    port.Width = 52;
                    row.Children.Add(port);
                    bool web = p == 80 || p == 443 || p == 8080 || p == 8443 || p == 8123 || p == 5000 || p == 5001 || p == 3000 || p == 3100 || p == 8000 || p == 8888 || p == 9000 || p == 9443 || p == 32400;
                    if (web)
                    {
                        string url = (p == 443 || p == 8443 || p == 5001 || p == 9443 ? "https://" : "http://") + d.Ip + ":" + p + (p == 32400 ? "/web" : "");
                        var link = Ui.Button(PortScan.Describe(p), () => Shell.OpenUrl(url), "GhostButton");
                        link.Foreground = Ui.B("AccentBrush");
                        link.Padding = new Thickness(0, 1, 0, 1);
                        link.ToolTip = "Open " + url;
                        row.Children.Add(link);
                    }
                    else row.Children.Add(Ui.Text(PortScan.Describe(p), 12.5));
                    _details.Children.Add(row);
                }
                var scan = Ui.Button(d.PortsKnown ? "Check again" : "Check services", async () => { await NetworkModel.ScanPorts(d); }, null, Ui.GlyphSearch);
                scan.HorizontalAlignment = HorizontalAlignment.Left;
                scan.Margin = new Thickness(0, 8, 0, 0);
                _details.Children.Add(scan);
            }

            // Options
            if (d.Record != null && !d.IsThisPc)
            {
                _details.Children.Add(Ui.Heading("Options"));
                var r = d.Record;
                _details.Children.Add(Ui.Switch("Trusted", "Known devices. New ones start as not trusted.", r.Trusted, v => { r.Trusted = v; Db.SaveDevice(r); NetworkModel.Rebuild(); }));
                _details.Children.Add(Ui.Switch("Alert me when it goes offline", "Handy for servers, cameras and the NAS.", r.AlertOffline, v => { r.AlertOffline = v; Db.SaveDevice(r); }));
                if (NetworkModel.UniFiOk)
                {
                    var cap = new TextBox { Text = r.CapGb > 0 ? r.CapGb.ToString(CultureInfo.CurrentCulture) : "", Width = 80 };
                    cap.LostKeyboardFocus += (s, e) =>
                    {
                        r.CapGb = double.TryParse(cap.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out double gb) && gb > 0 ? gb : 0;
                        Db.SaveDevice(r);
                    };
                    _details.Children.Add(Ui.Row("Monthly limit (GB)", "Alert when it uses more. Blank for none.", cap));
                }
                var forget = Ui.Button("Forget this device", () =>
                {
                    if (MessageBox.Show("Forget " + d.DisplayName + "? If it shows up again you'll get a new device alert.", "NetPull", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
                    Db.ForgetDevice(r.Mac);
                    _selected = null;
                    NetworkModel.Rebuild();
                }, null, Ui.GlyphDelete);
                forget.HorizontalAlignment = HorizontalAlignment.Left;
                forget.Margin = new Thickness(0, 8, 0, 0);
                _details.Children.Add(forget);
            }
        }

        private void Summary(List<NetDevice> all)
        {
            var lan = all.Where(d => d.Group == "lan").ToList();
            _details.Children.Add(Ui.Text("Your network", 17, "TextBrush", FontWeights.SemiBold));
            var sub = Ui.Wrap("Pick a device on the map or in the list to see what it is and what it's doing.", 12.5);
            sub.Margin = new Thickness(0, 4, 0, 10);
            _details.Children.Add(sub);
            Fact("Devices online", lan.Count(d => d.Online) + " of " + lan.Count);
            Fact("Not trusted", lan.Count(d => !d.Trusted).ToString());
            int ts = all.Count(d => d.Group == "tailscale");
            if (ts > 0) Fact("Tailscale machines", all.Count(d => d.Group == "tailscale" && d.Online) + " of " + ts + " online");
            Fact("UniFi", Settings.Current.UniFi ? NetworkModel.UniFiStatus : "Not connected");

            var busy = lan.Where(d => d.DownRate + d.UpRate > 0 && !d.IsInfra).OrderByDescending(d => d.DownRate + d.UpRate).Take(6).ToList();
            if (busy.Count > 0)
            {
                _details.Children.Add(Ui.Heading("Busiest right now"));
                foreach (var d in busy)
                {
                    var g = new DockPanel { Margin = new Thickness(0, 3, 0, 3), Cursor = Cursors.Hand, Background = System.Windows.Media.Brushes.Transparent };
                    var rate = Ui.Text("↓ " + Format.Rate(d.DownRate), 12, "DownBrush");
                    DockPanel.SetDock(rate, Dock.Right);
                    g.Children.Add(rate);
                    g.Children.Add(Ui.Text(d.DisplayName, 13));
                    string key = d.Key;
                    g.MouseLeftButtonUp += (s, e) => { _selected = key; Refresh(); };
                    _details.Children.Add(g);
                }
            }
            if (!Settings.Current.UniFi)
            {
                _details.Children.Add(Ui.Heading("Want data for every device?"));
                _details.Children.Add(Ui.Wrap("A PC can't see other devices' traffic by itself, but your UniFi console counts it. Add a view-only UniFi user in Settings and NetPull shows data used, live speed, Wi-Fi details and what each device is doing.", 12.5));
                var go = Ui.Button("Open Settings", () => App.Instance.ShowMain("settings"), "AccentButton");
                go.HorizontalAlignment = HorizontalAlignment.Left;
                go.Margin = new Thickness(0, 10, 0, 0);
                _details.Children.Add(go);
            }
        }

        private void Rename(NetDevice d)
        {
            var box = new TextBox { Text = d.Record.Name ?? d.DisplayName };
            var win = new Window
            {
                Title = "Rename device", Width = 360, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = Application.Current.Windows.OfType<HubWindow>().FirstOrDefault(), Background = Ui.B("BgBrush"), Foreground = Ui.B("TextBrush"), ResizeMode = ResizeMode.NoResize
            };
            var sp = new StackPanel { Margin = new Thickness(18) };
            sp.Children.Add(Ui.Text("Name", 12.5, "MutedBrush"));
            box.Margin = new Thickness(0, 6, 0, 14);
            sp.Children.Add(box);
            var ok = Ui.Button("Save", () => { d.Record.Name = box.Text.Trim(); Db.SaveDevice(d.Record); win.Close(); NetworkModel.Rebuild(); }, "AccentButton");
            ok.HorizontalAlignment = HorizontalAlignment.Right;
            ok.IsDefault = true;
            sp.Children.Add(ok);
            win.Content = sp;
            win.SourceInitialized += (s, e) => Native.DarkTitleBar(new System.Windows.Interop.WindowInteropHelper(win).Handle);
            win.Loaded += (s, e) => { box.Focus(); box.SelectAll(); };
            win.ShowDialog();
        }

        private void Fact(string label, string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            var g = new Grid { Margin = new Thickness(0, 3, 0, 3) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.Children.Add(Ui.Text(label, 12.5, "MutedBrush"));
            var v = Ui.Wrap(value, 12.5);
            v.Foreground = Ui.B("TextBrush");
            Grid.SetColumn(v, 1);
            g.Children.Add(v);
            _details.Children.Add(g);
        }

        private static FrameworkElement Big(string label, string value, string brush)
        {
            var sp = new StackPanel();
            sp.Children.Add(Ui.Text(label, 11.5, "MutedBrush"));
            var v = Ui.Text(value, 17, brush, FontWeights.SemiBold);
            v.Margin = new Thickness(0, 2, 0, 0);
            sp.Children.Add(v);
            return sp;
        }

        private static string Duration(long seconds)
        {
            var t = TimeSpan.FromSeconds(seconds);
            if (t.TotalDays >= 1) return (int)t.TotalDays + " days, " + t.Hours + " hr";
            if (t.TotalHours >= 1) return (int)t.TotalHours + " hr " + t.Minutes + " min";
            return Math.Max(1, (int)t.TotalMinutes) + " min";
        }
    }
}
