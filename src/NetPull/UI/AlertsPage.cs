using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using NetPull.Core;
using NetPull.Monitor;

namespace NetPull.UI
{
    /// <summary>Everything NetPull has flagged: new apps, changed apps, heavy use, the data cap and new devices.</summary>
    public sealed class AlertsPage : IPage
    {
        public string Key => "alerts";
        public string Title => "Alerts";
        public string Glyph => Ui.GlyphAlerts;
        public FrameworkElement View { get; }

        private readonly StackPanel _list = new StackPanel();
        private int _filter;
        private static readonly string[] Filters = { "All", "New apps", "App changes", "Data use", "Network" };

        public AlertsPage()
        {
            var root = new DockPanel();
            var head = new DockPanel { Margin = new Thickness(0, 0, 0, 14) };
            DockPanel.SetDock(head, Dock.Top);
            var right = new StackPanel { Orientation = Orientation.Horizontal };
            DockPanel.SetDock(right, Dock.Right);
            var clear = Ui.Button("Clear all", () =>
            {
                if (MessageBox.Show("Delete every alert?", "NetPull", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
                Db.ClearAlerts();
                Fill();
                App.Instance.Hub?.RefreshBadge();
            }, null, Ui.GlyphDelete);
            right.Children.Add(clear);
            head.Children.Add(right);
            head.Children.Add(Ui.Text("Alerts", 24, "TextBrush", FontWeights.SemiBold));
            root.Children.Add(head);

            var chips = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 14) };
            DockPanel.SetDock(chips, Dock.Top);
            for (int i = 0; i < Filters.Length; i++)
            {
                int idx = i;
                chips.Children.Add(Ui.Chip(Filters[i], "alert-filter", i == 0, () => { _filter = idx; Fill(); }));
            }
            root.Children.Add(chips);

            root.Children.Add(new ScrollViewer { Content = _list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
            View = root;

            AlertEngine.Raised += a => View.Dispatcher.BeginInvoke(new Action(() => { if (View.IsVisible) Fill(); }));
        }

        public void Shown()
        {
            Fill();
            // Seeing the page counts as reading them.
            Db.MarkAlertsRead();
            App.Instance.Hub?.RefreshBadge();
        }

        public void Tick(SecondSample s) { }

        private bool Wanted(AlertRecord a)
        {
            switch (_filter)
            {
                case 1: return a.Kind == AlertKinds.NewApp;
                case 2: return a.Kind == AlertKinds.AppChanged;
                case 3: return a.Kind == AlertKinds.Spike || a.Kind == AlertKinds.DataCap || a.Kind == AlertKinds.DeviceSpike || a.Kind == AlertKinds.DeviceCap;
                case 4: return a.Kind == AlertKinds.NewDevice || a.Kind == AlertKinds.DeviceOffline || a.Kind == AlertKinds.DeviceOnline || a.Kind == AlertKinds.DeviceSpike || a.Kind == AlertKinds.DeviceCap;
                default: return true;
            }
        }

        private static (string glyph, string brush) Look(string kind)
        {
            switch (kind)
            {
                case AlertKinds.NewApp: return (Ui.GlyphApp, "AccentBrush");
                case AlertKinds.AppChanged: return (Ui.GlyphShield, "WarnBrush");
                case AlertKinds.Spike: return (Ui.GlyphDown, "UpBrush");
                case AlertKinds.DataCap: return (Ui.GlyphUsage, "BadBrush");
                case AlertKinds.NewDevice: return (Ui.GlyphDevices, "GoodBrush");
                case AlertKinds.DeviceOffline: return (Ui.GlyphDevices, "BadBrush");
                case AlertKinds.DeviceOnline: return (Ui.GlyphDevices, "GoodBrush");
                case AlertKinds.DeviceSpike: return (Ui.GlyphDown, "UpBrush");
                case AlertKinds.DeviceCap: return (Ui.GlyphUsage, "BadBrush");
                default: return (Ui.GlyphInfo, "MutedBrush");
            }
        }

        private void Fill()
        {
            _list.Children.Clear();
            var alerts = Db.Alerts().Where(Wanted).ToList();
            if (alerts.Count == 0)
            {
                string body = AlertEngine.Learning
                    ? "NetPull is spending its first few minutes learning the apps you already use, so it won't flag them."
                    : "When an app goes online for the first time, changes, uses a lot of data, or a new device joins your network, you'll see it here.";
                _list.Children.Add(Ui.Empty("All quiet", body));
                return;
            }
            foreach (var a in alerts)
            {
                var (glyph, brush) = Look(a.Kind);
                var g = new Grid();
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
                g.ColumnDefinitions.Add(new ColumnDefinition());
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var app = a.AppId != 0 ? Db.GetApp(a.AppId) : null;
                if (app != null && !string.IsNullOrEmpty(app.Path) && (a.Kind == AlertKinds.NewApp || a.Kind == AlertKinds.AppChanged || a.Kind == AlertKinds.Spike))
                {
                    var icon = Ui.AppIcon(AppCatalog.IconFor(app), 26);
                    icon.HorizontalAlignment = HorizontalAlignment.Left;
                    g.Children.Add(icon);
                }
                else
                {
                    var i = Ui.Icon(glyph, 20, brush);
                    i.HorizontalAlignment = HorizontalAlignment.Left;
                    g.Children.Add(i);
                }

                var text = new StackPanel();
                text.Children.Add(Ui.Text(a.Title, 14, "TextBrush", a.Unread ? FontWeights.SemiBold : FontWeights.Normal));
                if (!string.IsNullOrEmpty(a.Body))
                {
                    var b = Ui.Wrap(a.Body, 12.5);
                    b.Margin = new Thickness(0, 3, 0, 0);
                    text.Children.Add(b);
                }
                Grid.SetColumn(text, 1);
                g.Children.Add(text);

                var right = new StackPanel { VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(12, 0, 0, 0) };
                var when = Ui.Text(Format.Ago(Format.FromUnix(a.Time)), 12, "MutedBrush");
                when.ToolTip = Format.FromUnix(a.Time).ToLocalTime().ToString("f");
                when.HorizontalAlignment = HorizontalAlignment.Right;
                right.Children.Add(when);
                if (app != null && !string.IsNullOrEmpty(app.Path))
                {
                    var open = Ui.Button("Show file", () => Shell.ShowInFolder(app.Path), "GhostButton");
                    open.Margin = new Thickness(0, 4, -8, 0);
                    open.HorizontalAlignment = HorizontalAlignment.Right;
                    open.Foreground = Ui.B("AccentBrush");
                    right.Children.Add(open);
                }
                Grid.SetColumn(right, 2);
                g.Children.Add(right);

                var card = Ui.Card(g, 14);
                card.Margin = new Thickness(0, 0, 0, 8);
                if (a.Unread) card.BorderBrush = Ui.B("Line2Brush");
                _list.Children.Add(card);
            }
        }
    }
}
