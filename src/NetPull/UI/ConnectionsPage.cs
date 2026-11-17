using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using NetPull.Core;
using NetPull.Monitor;

namespace NetPull.UI
{
    /// <summary>One line in the connections table.</summary>
    public sealed class ConnRow : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;
        private void Set<T>(ref T field, T value, string name)
        {
            if (Equals(field, value)) return;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        public string Key { get; set; }
        public string AppPath { get; set; }
        public ImageSource Icon { get; set; }

        private string _app; public string App { get => _app; set => Set(ref _app, value, nameof(App)); }
        private string _remote; public string Remote { get => _remote; set => Set(ref _remote, value, nameof(Remote)); }
        private string _remoteIp; public string RemoteIp { get => _remoteIp; set => Set(ref _remoteIp, value, nameof(RemoteIp)); }
        private string _country; public string Country { get => _country; set => Set(ref _country, value, nameof(Country)); }
        private string _isp; public string Isp { get => _isp; set => Set(ref _isp, value, nameof(Isp)); }
        private int _port; public int Port { get => _port; set => Set(ref _port, value, nameof(Port)); }
        private int _localPort; public int LocalPort { get => _localPort; set => Set(ref _localPort, value, nameof(LocalPort)); }
        private string _proto; public string Protocol { get => _proto; set => Set(ref _proto, value, nameof(Protocol)); }
        private string _state; public string State { get => _state; set => Set(ref _state, value, nameof(State)); }
        private long _rxRate; public long RxRate { get => _rxRate; set { Set(ref _rxRate, value, nameof(RxRate)); Changed(nameof(RxRateText)); } }
        private long _txRate; public long TxRate { get => _txRate; set { Set(ref _txRate, value, nameof(TxRate)); Changed(nameof(TxRateText)); } }
        private long _rx; public long RxTotal { get => _rx; set { Set(ref _rx, value, nameof(RxTotal)); Changed(nameof(RxTotalText)); } }
        private long _tx; public long TxTotal { get => _tx; set { Set(ref _tx, value, nameof(TxTotal)); Changed(nameof(TxTotalText)); } }

        public string RxRateText => _rxRate > 0 ? Format.Rate(_rxRate) : "";
        public string TxRateText => _txRate > 0 ? Format.Rate(_txRate) : "";
        public string RxTotalText => _rx > 0 ? Format.Size(_rx) : "";
        public string TxTotalText => _tx > 0 ? Format.Size(_tx) : "";
        public string PortText => _port > 0 ? _port.ToString() : _localPort.ToString();

        private void Changed(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        public void RefreshUnits() { Changed(nameof(RxRateText)); Changed(nameof(TxRateText)); }
    }

    /// <summary>Every app's open connections: who it's talking to, where they are, and how fast.</summary>
    public sealed class ConnectionsPage : IPage
    {
        public string Key => "connections";
        public string Title => "Connections";
        public string Glyph => Ui.GlyphConnections;
        public FrameworkElement View { get; }

        private readonly ObservableCollection<ConnRow> _rows = new ObservableCollection<ConnRow>();
        private readonly ICollectionView _view;
        private readonly ListView _list;
        private readonly TextBox _search = new TextBox { Width = 260 };
        private readonly TextBlock _count = Ui.Text("", 12.5, "MutedBrush");
        private bool _activeOnly = false, _listening = false;
        private string _sortBy = "RxRate";
        private ListSortDirection _sortDir = ListSortDirection.Descending;
        private DateTime _last = DateTime.MinValue;
        private bool _busy;

        public ConnectionsPage()
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
            head.Children.Add(Ui.Text("Connections", 24, "TextBrush", FontWeights.SemiBold));
            root.Children.Add(head);
            AdapterPicker.Changed += () => _ = Refresh();

            var bar = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
            DockPanel.SetDock(bar, Dock.Top);
            var searchHost = new Grid();
            searchHost.Children.Add(_search);
            var placeholder = Ui.Text("Search apps, hosts, countries, ports...", 13, "MutedBrush");
            placeholder.Margin = new Thickness(11, 0, 0, 0);
            placeholder.IsHitTestVisible = false;
            searchHost.Children.Add(placeholder);
            _search.TextChanged += (s, e) =>
            {
                placeholder.Visibility = _search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
                _view.Refresh();
                UpdateCount();
            };
            bar.Children.Add(searchHost);
            var toggles = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(20, 0, 0, 0) };
            var active = new CheckBox { Content = Ui.Text("Active only", 13), IsChecked = false, Margin = new Thickness(0, 0, 18, 0) };
            active.Checked += (s, e) => { _activeOnly = true; _ = Refresh(); };
            active.Unchecked += (s, e) => { _activeOnly = false; _ = Refresh(); };
            var listen = new CheckBox { Content = Ui.Text("Show listening ports", 13), IsChecked = false, Margin = new Thickness(0, 0, 18, 0) };
            listen.Checked += (s, e) => { _listening = true; _ = Refresh(); };
            listen.Unchecked += (s, e) => { _listening = false; _ = Refresh(); };
            toggles.Children.Add(active);
            toggles.Children.Add(listen);
            toggles.Children.Add(_count);
            bar.Children.Add(toggles);
            root.Children.Add(bar);

            _list = new ListView { ItemContainerStyle = (Style)Application.Current.Resources["GridRow"], ItemsSource = _rows };
            VirtualizingPanel.SetIsVirtualizing(_list, true);
            ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Auto);
            var grid = new GridView();
            grid.Columns.Add(AppColumn());
            grid.Columns.Add(TextColumn("Remote", "Remote", 230, "Remote"));
            grid.Columns.Add(TextColumn("Country", "Country", 130, "Country"));
            grid.Columns.Add(TextColumn("ISP / owner", "Isp", 170, "Isp"));
            grid.Columns.Add(TextColumn("Port", "PortText", 64, "Port"));
            grid.Columns.Add(TextColumn("Type", "Protocol", 58, "Protocol"));
            grid.Columns.Add(TextColumn("State", "State", 92, "State"));
            grid.Columns.Add(TextColumn("↓ now", "RxRateText", 92, "RxRate", "DownBrush"));
            grid.Columns.Add(TextColumn("↑ now", "TxRateText", 92, "TxRate", "UpBrush"));
            grid.Columns.Add(TextColumn("↓ total", "RxTotalText", 86, "RxTotal"));
            grid.Columns.Add(TextColumn("↑ total", "TxTotalText", 86, "TxTotal"));
            _list.View = grid;
            _list.AddHandler(GridViewColumnHeader.ClickEvent, new RoutedEventHandler(HeaderClicked));
            _list.ContextMenu = BuildMenu();

            _view = CollectionViewSource.GetDefaultView(_rows);
            _view.Filter = Matches;
            ApplySort();

            root.Children.Add(Ui.Card(_list, 8));
            View = root;
        }

        private GridViewColumn AppColumn()
        {
            var sp = new FrameworkElementFactory(typeof(StackPanel));
            sp.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
            var img = new FrameworkElementFactory(typeof(Image));
            img.SetValue(FrameworkElement.WidthProperty, 18.0);
            img.SetValue(FrameworkElement.HeightProperty, 18.0);
            img.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 8, 0));
            img.SetBinding(Image.SourceProperty, new Binding("Icon"));
            sp.AppendChild(img);
            var tb = new FrameworkElementFactory(typeof(TextBlock));
            tb.SetBinding(TextBlock.TextProperty, new Binding("App"));
            tb.SetBinding(FrameworkElement.ToolTipProperty, new Binding("AppPath"));
            tb.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
            tb.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            sp.AppendChild(tb);
            var col = new GridViewColumn { Width = 210, CellTemplate = new DataTemplate { VisualTree = sp } };
            col.Header = new GridViewColumnHeader { Content = "App", Tag = "App" };
            return col;
        }

        private GridViewColumn TextColumn(string header, string path, double width, string sortKey, string brush = null)
        {
            var tb = new FrameworkElementFactory(typeof(TextBlock));
            tb.SetBinding(TextBlock.TextProperty, new Binding(path));
            tb.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
            if (brush != null) tb.SetValue(TextBlock.ForegroundProperty, Ui.B(brush));
            if (path == "Remote") tb.SetBinding(FrameworkElement.ToolTipProperty, new Binding("RemoteIp"));
            var col = new GridViewColumn { Width = width, CellTemplate = new DataTemplate { VisualTree = tb } };
            col.Header = new GridViewColumnHeader { Content = header, Tag = sortKey };
            return col;
        }

        private void HeaderClicked(object sender, RoutedEventArgs e)
        {
            if (!(e.OriginalSource is GridViewColumnHeader h) || !(h.Tag is string key)) return;
            if (_sortBy == key) _sortDir = _sortDir == ListSortDirection.Ascending ? ListSortDirection.Descending : ListSortDirection.Ascending;
            else
            {
                _sortBy = key;
                _sortDir = key.Contains("Rx") || key.Contains("Tx") ? ListSortDirection.Descending : ListSortDirection.Ascending;
            }
            ApplySort();
        }

        private void ApplySort()
        {
            _view.SortDescriptions.Clear();
            _view.SortDescriptions.Add(new SortDescription(_sortBy, _sortDir));
            if (_sortBy != "RxTotal") _view.SortDescriptions.Add(new SortDescription("RxTotal", ListSortDirection.Descending));
        }

        private bool Matches(object o)
        {
            string q = _search.Text.Trim();
            if (q.Length == 0) return true;
            var r = (ConnRow)o;
            return Has(r.App, q) || Has(r.Remote, q) || Has(r.RemoteIp, q) || Has(r.Country, q) || Has(r.Isp, q) || Has(r.State, q)
                   || r.Port.ToString() == q || r.LocalPort.ToString() == q || Has(r.AppPath, q);
        }

        private static bool Has(string s, string q) => s != null && s.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0;

        private ContextMenu BuildMenu()
        {
            var m = new ContextMenu();
            ConnRow Sel() => _list.SelectedItem as ConnRow;
            void Item(string text, Action<ConnRow> act)
            {
                var mi = new MenuItem { Header = text };
                mi.Click += (s, e) => { var r = Sel(); if (r != null) act(r); };
                m.Items.Add(mi);
            }
            Item("Copy address", r => { try { Clipboard.SetText(r.RemoteIp ?? ""); } catch { } });
            Item("Copy host name", r => { try { Clipboard.SetText(r.Remote ?? ""); } catch { } });
            Item("Look up this address online", r => { if (!string.IsNullOrEmpty(r.RemoteIp)) Shell.OpenUrl("https://ipinfo.io/" + r.RemoteIp); });
            m.Items.Add(new Separator());
            Item("Show the app in its folder", r => { if (!string.IsNullOrEmpty(r.AppPath)) Shell.ShowInFolder(r.AppPath); });
            return m;
        }

        public void Shown() => _ = Refresh();

        public void Tick(SecondSample s)
        {
            if (DateTime.UtcNow - _last >= TimeSpan.FromSeconds(2)) _ = Refresh();
        }

        private async Task Refresh()
        {
            if (_busy) return;
            _busy = true;
            _last = DateTime.UtcNow;
            try
            {
                bool listening = _listening, activeOnly = _activeOnly;
                int adapter = AdapterPicker.Current;
                var list = await Task.Run(() => Connections.Snapshot(listening, activeOnly));
                if (adapter != 0) list = list.Where(c => c.Adapter == adapter).ToList();

                var byKey = _rows.ToDictionary(r => r.Key);
                var seen = new HashSet<string>();
                foreach (var c in list)
                {
                    if (!seen.Add(c.Key)) continue;
                    if (!byKey.TryGetValue(c.Key, out var r))
                    {
                        var app = c.AppId != 0 ? Db.GetApp(c.AppId) : new AppRecord { Path = c.AppPath };
                        r = new ConnRow { Key = c.Key, AppPath = c.AppPath, Icon = AppCatalog.IconFor(app) };
                        _rows.Add(r);
                    }
                    var info = string.IsNullOrEmpty(c.RemoteIp) ? null : IpLookup.Get(c.RemoteIp);
                    r.App = c.AppName;
                    r.RemoteIp = c.RemoteIp;
                    r.Remote = c.Listening ? "Listening on " + (c.LocalIp == "0.0.0.0" || c.LocalIp == "::" ? "all addresses" : c.LocalIp) : info?.Display ?? c.RemoteIp;
                    r.Country = info?.Where ?? "";
                    r.Isp = info?.Isp ?? "";
                    r.Port = c.RemotePort;
                    r.LocalPort = c.LocalPort;
                    r.Protocol = c.Udp ? "UDP" : "TCP";
                    r.State = c.State;
                    r.RxRate = c.RxRate; r.TxRate = c.TxRate;
                    r.RxTotal = c.RxTotal; r.TxTotal = c.TxTotal;
                }
                for (int i = _rows.Count - 1; i >= 0; i--)
                    if (!seen.Contains(_rows[i].Key)) _rows.RemoveAt(i);
                _view.Refresh();
                UpdateCount();
            }
            catch (Exception ex) { Log.Write("Connections: " + ex.Message); }
            finally { _busy = false; }
        }

        private void UpdateCount()
        {
            int shown = _view.Cast<object>().Count();
            int apps = _rows.Select(r => r.App).Distinct().Count();
            _count.Text = Ui.Plural(shown, "connection", "connections") + " · " + Ui.Plural(apps, "app", "apps");
        }

        private void Export()
        {
            try
            {
                var rows = _view.Cast<ConnRow>().Select(r => new[]
                {
                    r.App, r.AppPath ?? "", r.Remote, r.RemoteIp, r.Country, r.Isp, r.Port.ToString(), r.LocalPort.ToString(), r.Protocol, r.State,
                    r.RxRate.ToString(), r.TxRate.ToString(), r.RxTotal.ToString(), r.TxTotal.ToString()
                }).ToList();
                string saved = Csv.Save("NetPull connections " + DateTime.Now.ToString("yyyy-MM-dd HHmm") + ".csv",
                    new[] { "App", "Path", "Remote", "Address", "Country", "ISP", "Remote port", "Local port", "Type", "State", "Down B/s", "Up B/s", "Down total (bytes)", "Up total (bytes)" }, rows);
                if (saved != null) App.Instance.Tray?.Notify("Exported", saved);
            }
            catch (Exception ex) { MessageBox.Show("Couldn't export: " + ex.Message, "NetPull"); }
        }
    }
}
