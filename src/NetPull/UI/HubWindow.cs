using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NetPull.Core;
using NetPull.Monitor;

namespace NetPull.UI
{
    public interface IPage
    {
        string Key { get; }
        string Title { get; }
        string Glyph { get; }
        FrameworkElement View { get; }
        void Shown();
        void Tick(SecondSample s);
    }

    /// <summary>The main window: a side bar with the pages, live speeds, and the current page.</summary>
    public sealed class HubWindow : Window
    {
        private readonly List<IPage> _pages = new List<IPage>();
        private readonly Dictionary<string, RadioButton> _nav = new Dictionary<string, RadioButton>();
        private readonly ContentControl _host = new ContentControl();
        private IPage _current;
        private readonly TextBlock _down = new TextBlock();
        private readonly TextBlock _up = new TextBlock();
        private readonly TextBlock _status = new TextBlock();
        private readonly Border _statusDot = new Border();
        private Border _badge;
        private TextBlock _badgeText;

        public HubWindow()
        {
            Title = "NetPull";
            Width = Math.Max(940, Settings.Current.WindowWidth);
            Height = Math.Max(600, Settings.Current.WindowHeight);
            MinWidth = 940;
            MinHeight = 600;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = Ui.B("BgBrush");
            Foreground = Ui.B("TextBrush");
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
            FontSize = 13;
            UseLayoutRounding = true;
            try { Icon = BitmapFrame.Create(new Uri("pack://application:,,,/NetPull;component/Assets/netpull.ico", UriKind.Absolute)); } catch { }

            _pages.Add(new GraphPage());
            _pages.Add(new UsagePage());
            _pages.Add(new ConnectionsPage());
            _pages.Add(new DevicesPage());
            _pages.Add(new AlertsPage());
            _pages.Add(new SettingsPage());

            var root = new Grid();
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(212) });
            root.ColumnDefinitions.Add(new ColumnDefinition());
            root.Children.Add(BuildSidebar());
            var content = new Border { Padding = new Thickness(28, 22, 28, 22), Child = _host };
            Grid.SetColumn(content, 1);
            root.Children.Add(content);
            Content = root;

            Navigate("graph");
            RefreshBadge();
            UpdateStatus();
            TrafficMonitor.Instance.StatusChanged += () => Dispatcher.BeginInvoke(new Action(UpdateStatus));
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            Native.DarkTitleBar(new WindowInteropHelper(this).Handle);
        }

        private FrameworkElement BuildSidebar()
        {
            var side = new Border { Background = Ui.B("SideBrush"), BorderBrush = Ui.B("LineBrush"), BorderThickness = new Thickness(0, 0, 1, 0) };
            var dock = new DockPanel { Margin = new Thickness(14, 18, 14, 16) };
            side.Child = dock;

            var brand = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 0, 0, 22) };
            DockPanel.SetDock(brand, Dock.Top);
            try
            {
                var logo = new Image { Width = 26, Height = 26, Source = new BitmapImage(new Uri("pack://application:,,,/NetPull;component/Assets/netpull-64.png", UriKind.Absolute)) };
                RenderOptions.SetBitmapScalingMode(logo, BitmapScalingMode.HighQuality);
                brand.Children.Add(logo);
            }
            catch { }
            var name = Ui.Text("NetPull", 19, "TextBrush", FontWeights.SemiBold);
            name.Margin = new Thickness(10, 0, 0, 0);
            brand.Children.Add(name);
            dock.Children.Add(brand);

            // Live speeds and status, at the bottom.
            var bottom = new StackPanel { Margin = new Thickness(8, 12, 0, 0) };
            DockPanel.SetDock(bottom, Dock.Bottom);
            bottom.Children.Add(SpeedLine(Ui.GlyphDown, "DownBrush", _down));
            bottom.Children.Add(SpeedLine(Ui.GlyphUp, "UpBrush", _up));
            var st = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
            _statusDot.Width = 8; _statusDot.Height = 8; _statusDot.CornerRadius = new CornerRadius(4);
            _statusDot.VerticalAlignment = VerticalAlignment.Center;
            _statusDot.Margin = new Thickness(0, 0, 8, 0);
            st.Children.Add(_statusDot);
            _status.FontSize = 12; _status.Foreground = Ui.B("MutedBrush"); _status.TextWrapping = TextWrapping.Wrap; _status.MaxWidth = 160;
            st.Children.Add(_status);
            bottom.Children.Add(st);
            dock.Children.Add(bottom);

            var nav = new StackPanel();
            foreach (var p in _pages)
            {
                var row = new Grid();
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
                row.ColumnDefinitions.Add(new ColumnDefinition());
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var icon = new TextBlock { Text = p.Glyph, FontFamily = Ui.IconFont, FontSize = 15, VerticalAlignment = VerticalAlignment.Center };
                row.Children.Add(icon);
                var label = new TextBlock { Text = p.Title, VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(label, 1);
                row.Children.Add(label);
                if (p.Key == "alerts")
                {
                    _badgeText = new TextBlock { FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Color.FromRgb(0x06, 0x10, 0x18)), HorizontalAlignment = HorizontalAlignment.Center };
                    _badge = new Border { Background = Ui.B("UpBrush"), CornerRadius = new CornerRadius(9), MinWidth = 18, Height = 18, Padding = new Thickness(5, 0, 5, 0), Child = _badgeText, Visibility = Visibility.Collapsed };
                    _badgeText.VerticalAlignment = VerticalAlignment.Center;
                    Grid.SetColumn(_badge, 2);
                    row.Children.Add(_badge);
                }
                var rb = new RadioButton { Content = row, GroupName = "nav", Style = (Style)Application.Current.Resources["Nav"], HorizontalContentAlignment = HorizontalAlignment.Stretch };
                string key = p.Key;
                rb.Checked += (s, e) => Show(key);
                _nav[p.Key] = rb;
                nav.Children.Add(rb);
            }
            dock.Children.Add(nav);
            return side;
        }

        private static FrameworkElement SpeedLine(string glyph, string brush, TextBlock value)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 3) };
            var i = Ui.Icon(glyph, 13, brush);
            i.Margin = new Thickness(0, 0, 10, 0);
            sp.Children.Add(i);
            value.FontSize = 15; value.FontWeight = FontWeights.SemiBold; value.Foreground = Ui.B(brush);
            value.Text = Format.Rate(0);
            sp.Children.Add(value);
            return sp;
        }

        private void UpdateStatus()
        {
            var m = TrafficMonitor.Instance;
            _statusDot.Background = Ui.B(m.IsRunning ? "GoodBrush" : "BadBrush");
            _status.Text = m.IsRunning ? (AlertEngine.Learning ? "Watching (learning your apps)" : "Watching your network") : (m.Error ?? "Starting...");
        }

        public void Navigate(string key)
        {
            if (_nav.TryGetValue(key, out var rb))
            {
                if (rb.IsChecked == true) Show(key); else rb.IsChecked = true;
            }
        }

        private void Show(string key)
        {
            foreach (var p in _pages)
            {
                if (p.Key != key) continue;
                _current = p;
                _host.Content = p.View;
                try { p.Shown(); } catch (Exception ex) { Log.Write("Page " + key + ": " + ex); }
            }
        }

        public void OnTick(SecondSample s)
        {
            if (!IsVisible) return;
            int adapter = AdapterPicker.Current;
            var (rx, tx) = s.For(adapter);
            _down.Text = Format.Rate(rx);
            _up.Text = Format.Rate(tx);
            if (s.Time % 10 == 0) UpdateStatus();
            try { _current?.Tick(s); } catch (Exception ex) { Log.Write("Tick " + _current?.Key + ": " + ex.Message); }
        }

        public void RefreshBadge()
        {
            if (_badge == null) return;
            int n = 0;
            try { n = Db.UnreadAlerts(); } catch { }
            _badgeText.Text = n > 99 ? "99+" : n.ToString();
            _badge.Visibility = n > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            // Closing the window keeps NetPull running in the tray.
            e.Cancel = true;
            if (WindowState == WindowState.Normal)
            {
                Settings.Current.WindowWidth = ActualWidth;
                Settings.Current.WindowHeight = ActualHeight;
            }
            if (!Settings.Current.TrayHintShown)
            {
                Settings.Current.TrayHintShown = true;
                App.Instance.Tray?.Notify("NetPull is still watching", "It keeps recording in the tray. Right-click the tray icon to exit.");
            }
            Settings.Save();
            Hide();
        }
    }
}
