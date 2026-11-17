using System;
using System.Linq;
using System.Net;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using NetPull.Core;
using NetPull.Monitor;

namespace NetPull.UI
{
    /// <summary>Everything NetPull has seen on your local network, with names you can set and a trusted switch.</summary>
    public sealed class DevicesPage : IPage
    {
        public string Key => "devices";
        public string Title => "Devices";
        public string Glyph => Ui.GlyphDevices;
        public FrameworkElement View { get; }

        private readonly StackPanel _list = new StackPanel();
        private readonly TextBlock _status = Ui.Text("", 12.5, "MutedBrush");
        private readonly Button _scan;
        private double _progress;

        public DevicesPage()
        {
            var root = new DockPanel();
            var head = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
            DockPanel.SetDock(head, Dock.Top);
            var right = new StackPanel { Orientation = Orientation.Horizontal };
            DockPanel.SetDock(right, Dock.Right);
            _status.Margin = new Thickness(0, 0, 12, 0);
            right.Children.Add(_status);
            _scan = Ui.Button("Scan now", () => { _ = LanWatch.ScanNow(); UpdateStatus(); }, "AccentButton", Ui.GlyphRefresh);
            right.Children.Add(_scan);
            head.Children.Add(right);
            head.Children.Add(Ui.Text("Devices", 24, "TextBrush", FontWeights.SemiBold));
            root.Children.Add(head);

            var intro = Ui.Wrap("Devices on your home network. NetPull checks quietly every few minutes and alerts you when something new joins. Mark the ones you know as trusted and give them names.", 13);
            intro.Margin = new Thickness(0, 0, 0, 14);
            DockPanel.SetDock(intro, Dock.Top);
            root.Children.Add(intro);

            root.Children.Add(new ScrollViewer { Content = _list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
            View = root;

            LanWatch.ScanFinished += () => View.Dispatcher.BeginInvoke(new Action(() => { if (View.IsVisible) Fill(); UpdateStatus(); }));
            LanWatch.Progress += p => { _progress = p; };
        }

        public void Shown()
        {
            Fill();
            UpdateStatus();
        }

        public void Tick(SecondSample s)
        {
            if (LanWatch.Scanning || s.Time % 15 == 0) UpdateStatus();
        }

        private void UpdateStatus()
        {
            _scan.IsEnabled = !LanWatch.Scanning;
            if (LanWatch.Scanning) _status.Text = "Scanning... " + (int)(_progress * 100) + "%";
            else if (!Settings.Current.LanWatch) _status.Text = "Automatic checks are off (Settings)";
            else if (LanWatch.LastScan == DateTime.MinValue) _status.Text = "First check starts shortly";
            else _status.Text = "Last checked " + Format.Ago(LanWatch.LastScan) + " · every " + Settings.Current.LanScanMinutes + " min";
        }

        private static Version IpKey(string ip) => IPAddress.TryParse(ip ?? "", out var a) && a.GetAddressBytes().Length == 4
            ? new Version(a.GetAddressBytes()[0], a.GetAddressBytes()[1], a.GetAddressBytes()[2], a.GetAddressBytes()[3]) : new Version(255, 0);

        private void Fill()
        {
            _list.Children.Clear();
            var online = LanWatch.OnlineNow;
            var devices = Db.Devices()
                .OrderByDescending(d => online.Contains(d.Mac))
                .ThenBy(d => d.Trusted)
                .ThenBy(d => IpKey(d.Ip)).ToList();
            if (devices.Count == 0)
            {
                _list.Children.Add(Ui.Empty("No devices yet", "Press Scan now, or wait for the first automatic check."));
                return;
            }
            foreach (var d in devices) _list.Children.Add(Row(d, online.Contains(d.Mac)));
        }

        private FrameworkElement Row(DeviceRecord d, bool isOnline)
        {
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var dot = new Border { Width = 9, Height = 9, CornerRadius = new CornerRadius(5), Background = Ui.B(isOnline ? "GoodBrush" : "LineBrush"), VerticalAlignment = VerticalAlignment.Center, ToolTip = isOnline ? "Online now" : "Not seen in the last check" };
            g.Children.Add(dot);

            var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var nameRow = new StackPanel { Orientation = Orientation.Horizontal };
            var name = Ui.Text(d.DisplayName, 14.5, "TextBrush", FontWeights.SemiBold);
            nameRow.Children.Add(name);
            if (!d.Trusted)
            {
                var tag = new Border { Background = Ui.B("Panel2Brush"), CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 1, 6, 1), Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Child = Ui.Text("Not trusted", 11, "WarnBrush") };
                nameRow.Children.Add(tag);
            }
            info.Children.Add(nameRow);
            string details = string.Join("  ·  ", new[] { d.Ip, d.Mac, d.Vendor, string.IsNullOrEmpty(d.Name) ? null : d.Hostname }.Where(x => !string.IsNullOrEmpty(x)));
            var det = Ui.Text(details, 12, "MutedBrush");
            det.Margin = new Thickness(0, 3, 0, 0);
            info.Children.Add(det);
            var seen = Ui.Text("First seen " + Format.Ago(Format.FromUnix(d.FirstSeen)) + "  ·  last seen " + Format.Ago(Format.FromUnix(d.LastSeen)), 11.5, "MutedBrush");
            seen.Margin = new Thickness(0, 2, 0, 0);
            info.Children.Add(seen);
            Grid.SetColumn(info, 1);
            g.Children.Add(info);

            var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var trusted = new CheckBox { Content = Ui.Text("Trusted", 12.5, "MutedBrush"), IsChecked = d.Trusted, Margin = new Thickness(0, 0, 14, 0) };
            trusted.Checked += (s, e) => { d.Trusted = true; Db.SaveDevice(d); Fill(); };
            trusted.Unchecked += (s, e) => { d.Trusted = false; Db.SaveDevice(d); Fill(); };
            actions.Children.Add(trusted);

            var rename = Ui.Button("", null, "GhostButton", Ui.GlyphEdit);
            rename.ToolTip = "Rename";
            rename.Click += (s, e) =>
            {
                var box = new TextBox { Text = d.Name ?? d.DisplayName, Width = 220 };
                nameRow.Children[0] = box;
                box.Focus();
                box.SelectAll();
                bool done = false;
                void Save()
                {
                    if (done) return;
                    done = true;
                    d.Name = box.Text.Trim();
                    Db.SaveDevice(d);
                    Fill();
                }
                box.KeyDown += (s2, e2) =>
                {
                    if (e2.Key == System.Windows.Input.Key.Enter) Save();
                    else if (e2.Key == System.Windows.Input.Key.Escape) { done = true; Fill(); }
                };
                box.LostKeyboardFocus += (s2, e2) => Save();
            };
            actions.Children.Add(rename);

            var forget = Ui.Button("", () =>
            {
                if (MessageBox.Show("Forget " + d.DisplayName + "? If it shows up again you'll get a new device alert.", "NetPull", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
                Db.ForgetDevice(d.Mac);
                Fill();
            }, "GhostButton", Ui.GlyphDelete);
            forget.ToolTip = "Forget this device";
            actions.Children.Add(forget);
            Grid.SetColumn(actions, 2);
            g.Children.Add(actions);

            var card = Ui.Card(g, 14);
            card.Margin = new Thickness(0, 0, 0, 8);
            return card;
        }
    }
}
