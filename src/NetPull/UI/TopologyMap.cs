using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using NetPull.Core;
using NetPull.Monitor;

namespace NetPull.UI
{
    /// <summary>Draws the network as a tree: Internet, gateway, switches and access points, then the devices on each.</summary>
    public sealed class TopologyMap : ScrollViewer
    {
        private const double CardW = 210, CardH = 52, InfraW = 220, InfraH = 66, ColGap = 26, RowGap = 8, LevelGap = 46, PerColumn = 8;
        private readonly Canvas _canvas = new Canvas();
        private readonly ScaleTransform _zoom = new ScaleTransform(1, 1);
        public string SelectedKey;
        public event Action<NetDevice> Selected;

        public TopologyMap()
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            _canvas.LayoutTransform = _zoom;
            Content = _canvas;
            PreviewMouseWheel += (s, e) =>
            {
                if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
                double z = Math.Max(0.5, Math.Min(1.6, _zoom.ScaleX + (e.Delta > 0 ? 0.1 : -0.1)));
                _zoom.ScaleX = _zoom.ScaleY = z;
                e.Handled = true;
            };
        }

        public static string Glyph(string kind) => kind switch
        {
            "internet" => "",
            "gateway" => "",
            "switch" => "",
            "ap" => "",
            "computer" => "",
            "phone" => "",
            "tv" => "",
            "camera" => "",
            "printer" => "",
            "server" => "",
            "smart" => "",
            "web" => "",
            "tailscale" => "",
            _ => ""
        };

        public static string KindName(string kind) => kind switch
        {
            "gateway" => "Gateway / router",
            "switch" => "Switch",
            "ap" => "Wi-Fi access point",
            "computer" => "Computer",
            "phone" => "Phone or tablet",
            "tv" => "TV or streamer",
            "camera" => "Camera",
            "printer" => "Printer",
            "server" => "Server",
            "smart" => "Smart home",
            "web" => "Device with a web page",
            "tailscale" => "Tailscale machine",
            _ => "Device"
        };

        private sealed class Node
        {
            public NetDevice Dev;
            public string Key;
            public string Title;
            public string Kind;
            public bool Infra;
            public bool Dashed;
            public List<Node> Children = new List<Node>();
            public List<NetDevice> Clients = new List<NetDevice>();
            public double Width;   // of the whole subtree
            public double X, Y;    // top-left of the node card
        }

        public void Show(List<NetDevice> devices)
        {
            _canvas.Children.Clear();
            _blockLeft.Clear();
            var byKey = devices.ToDictionary(d => d.Key);
            var internet = new Node { Key = "internet", Title = "Internet", Kind = "internet", Infra = true };

            // Gateway: from UniFi, or the router found by the scan, or a stand-in.
            var gwDev = devices.FirstOrDefault(d => d.IsGateway && d.Group == "lan");
            var gw = new Node { Dev = gwDev, Key = gwDev?.Key ?? "router", Title = gwDev?.DisplayName ?? "Router", Kind = "gateway", Infra = true };
            internet.Children.Add(gw);

            var infraNodes = new Dictionary<string, Node> { [gw.Key] = gw };
            foreach (var d in devices.Where(d => d.IsInfra && !d.IsGateway))
                infraNodes[d.Key] = new Node { Dev = d, Key = d.Key, Title = d.DisplayName, Kind = d.Kind, Infra = true };
            foreach (var n in infraNodes.Values.Where(n => n != gw))
            {
                string parent = n.Dev?.ParentKey;
                var p = parent != null && infraNodes.TryGetValue(parent, out var pn) && pn != n ? pn : gw;
                p.Children.Add(n);
            }
            foreach (var d in devices.Where(d => d.Group == "lan" && !d.IsInfra && !d.IsGateway))
            {
                var p = d.ParentKey != null && infraNodes.TryGetValue(d.ParentKey, out var pn) ? pn : gw;
                p.Clients.Add(d);
            }
            var tsPeers = devices.Where(d => d.Group == "tailscale").ToList();
            if (tsPeers.Count > 0)
                internet.Children.Add(new Node { Key = "tailscale", Title = "Tailscale", Kind = "tailscale", Infra = true, Dashed = true, Clients = tsPeers });

            Measure(internet);
            Place(internet, 20, 16);
            Draw(internet);
            double w = 0, h = 0;
            foreach (UIElement c in _canvas.Children)
                if (c is Border fe) { w = Math.Max(w, Canvas.GetLeft(fe) + fe.Width); h = Math.Max(h, Canvas.GetTop(fe) + fe.Height); }
            _canvas.Width = w + 30;
            _canvas.Height = h + 30;
        }

        private static IEnumerable<NetDevice> Sorted(IEnumerable<NetDevice> list) =>
            list.OrderByDescending(d => d.IsThisPc).ThenByDescending(d => d.Online).ThenByDescending(d => d.DownRate + d.UpRate > 0).ThenBy(d => d.DisplayName, StringComparer.OrdinalIgnoreCase);

        private static int Columns(Node n) => n.Clients.Count == 0 ? 0 : (int)Math.Ceiling(n.Clients.Count / PerColumn);

        private static double BlockWidth(Node n) => Columns(n) == 0 ? 0 : Columns(n) * CardW + (Columns(n) - 1) * ColGap;

        private static void Measure(Node n)
        {
            foreach (var c in n.Children) Measure(c);
            double kids = n.Children.Sum(c => c.Width) + BlockWidth(n);
            int parts = n.Children.Count + (n.Clients.Count > 0 ? 1 : 0);
            kids += Math.Max(0, parts - 1) * ColGap * 1.4;
            n.Width = Math.Max(InfraW, kids);
        }

        private void Place(Node n, double left, double top)
        {
            n.X = left + (n.Width - InfraW) / 2;
            n.Y = top;
            double x = left + (n.Width - (n.Children.Sum(c => c.Width) + BlockWidth(n) + Math.Max(0, n.Children.Count + (n.Clients.Count > 0 ? 1 : 0) - 1) * ColGap * 1.4)) / 2;
            double childTop = top + InfraH + LevelGap;
            if (n.Clients.Count > 0)
            {
                _blockLeft[n] = x;
                x += BlockWidth(n) + ColGap * 1.4;
            }
            foreach (var c in n.Children)
            {
                Place(c, x, childTop);
                x += c.Width + ColGap * 1.4;
            }
        }

        private readonly Dictionary<Node, double> _blockLeft = new Dictionary<Node, double>();

        private void Draw(Node n)
        {
            double cx = n.X + InfraW / 2, bottom = n.Y + InfraH;
            foreach (var c in n.Children)
            {
                Elbow(cx, bottom, c.X + InfraW / 2, c.Y, c.Dashed, c.Dev);
                Draw(c);
            }
            if (n.Clients.Count > 0 && _blockLeft.TryGetValue(n, out double bx))
            {
                var list = Sorted(n.Clients).ToList();
                double top = n.Y + InfraH + LevelGap;
                for (int col = 0; col < Columns(n); col++)
                {
                    var items = list.Skip(col * (int)PerColumn).Take((int)PerColumn).ToList();
                    double colX = bx + col * (CardW + ColGap);
                    double trunkX = colX + 12;
                    Elbow(cx, bottom, trunkX, top - 10, n.Dashed, null);
                    double lastY = top + (items.Count - 1) * (CardH + RowGap) + CardH / 2;
                    AddLine(trunkX, top - 10, trunkX, lastY, Ui.B("Line2Brush"), 1.5, n.Dashed);
                    for (int i = 0; i < items.Count; i++)
                    {
                        double y = top + i * (CardH + RowGap);
                        var d = items[i];
                        bool busy = d.DownRate + d.UpRate > 2048;
                        AddLine(trunkX, y + CardH / 2, colX + 28, y + CardH / 2, busy ? Ui.B("DownBrush") : Ui.B("Line2Brush"), busy ? 2 : 1.5, false);
                        AddCard(d, d.Key, d.DisplayName, d.Kind, colX + 28, y, CardW - 28, CardH, false);
                    }
                }
            }
            AddCard(n.Dev, n.Key, n.Title, n.Kind, n.X, n.Y, InfraW, InfraH, true);
        }

        private void Elbow(double x1, double y1, double x2, double y2, bool dashed, NetDevice to)
        {
            double mid = y1 + (y2 - y1) / 2;
            bool busy = to != null && to.DownRate + to.UpRate > 2048;
            var brush = busy ? Ui.B("DownBrush") : Ui.B("Line2Brush");
            var path = new Polyline
            {
                Points = new PointCollection { new Point(x1, y1), new Point(x1, mid), new Point(x2, mid), new Point(x2, y2) },
                Stroke = brush, StrokeThickness = busy ? 2.2 : 1.5, StrokeLineJoin = PenLineJoin.Round
            };
            if (dashed) path.StrokeDashArray = new DoubleCollection { 4, 3 };
            if (busy) path.Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = Ui.C("DownColor"), BlurRadius = 8, ShadowDepth = 0, Opacity = 0.7 };
            _canvas.Children.Insert(0, path);
        }

        private void AddLine(double x1, double y1, double x2, double y2, Brush b, double thick, bool dashed)
        {
            var l = new Line { X1 = x1, Y1 = y1, X2 = x2, Y2 = y2, Stroke = b, StrokeThickness = thick };
            if (dashed) l.StrokeDashArray = new DoubleCollection { 4, 3 };
            _canvas.Children.Insert(0, l);
        }

        private void AddCard(NetDevice d, string key, string title, string kind, double x, double y, double w, double h, bool infra)
        {
            bool online = d?.Online ?? true;
            bool selected = key == SelectedKey;
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(infra ? 42 : 34) });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            var circle = new Border
            {
                Width = infra ? 32 : 26, Height = infra ? 32 : 26, CornerRadius = new CornerRadius(infra ? 16 : 13),
                Background = new SolidColorBrush(Color.FromArgb(0x22, 0x22, 0xE1, 0xF0)), HorizontalAlignment = HorizontalAlignment.Left,
                Child = new TextBlock
                {
                    Text = Glyph(kind), FontFamily = Ui.IconFont, FontSize = infra ? 15 : 13, Foreground = Ui.B(kind == "tailscale" ? "UpBrush" : "DownBrush"),
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
                }
            };
            g.Children.Add(circle);
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var t = Ui.Text(title, infra ? 13.5 : 12.5, "TextBrush", FontWeights.SemiBold);
            text.Children.Add(t);
            string sub;
            if (d == null) sub = kind == "internet" ? "" : kind == "tailscale" ? "Your tailnet" : "Found by your default route";
            else if (!d.Online) sub = "Offline";
            else if (d.DownRate + d.UpRate > 0) sub = "↓ " + Format.Rate(d.DownRate) + "  ↑ " + Format.Rate(d.UpRate);
            else sub = d.Ip ?? (infra ? d.Model : "") ?? "";
            if (infra && d != null && !string.IsNullOrEmpty(d.Model) && d.Online && d.DownRate + d.UpRate == 0) sub = (d.Model + "  " + (d.Ip ?? "")).Trim();
            var s = Ui.Text(sub, 11, d != null && d.Online && d.DownRate + d.UpRate > 0 ? "DownBrush" : "MutedBrush");
            text.Children.Add(s);
            Grid.SetColumn(text, 1);
            g.Children.Add(text);

            if (d != null && !d.Trusted)
            {
                var warn = new Border { Width = 8, Height = 8, CornerRadius = new CornerRadius(4), Background = Ui.B("WarnBrush"), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, ToolTip = "Not trusted yet" };
                Grid.SetColumnSpan(warn, 2);
                g.Children.Add(warn);
            }

            var card = new Border
            {
                Width = w, Height = h, Padding = new Thickness(10, 6, 10, 6), CornerRadius = new CornerRadius(10),
                Background = Ui.B(infra ? "Panel2Brush" : "PanelBrush"),
                BorderBrush = selected ? Ui.B("AccentBrush") : Ui.B("LineBrush"), BorderThickness = new Thickness(selected ? 1.5 : 1),
                Opacity = online ? 1 : 0.45, Child = g, Cursor = d != null ? Cursors.Hand : Cursors.Arrow,
                ToolTip = d == null ? null : string.Join("\n", new[] { title, d.Ip, d.Mac, d.Connection }.Where(x => !string.IsNullOrEmpty(x)))
            };
            if (d != null)
            {
                card.MouseEnter += (o, e) => { if (key != SelectedKey) card.BorderBrush = Ui.B("Line2Brush"); };
                card.MouseLeave += (o, e) => { if (key != SelectedKey) card.BorderBrush = Ui.B("LineBrush"); };
                card.MouseLeftButtonUp += (o, e) => { SelectedKey = key; Selected?.Invoke(d); };
            }
            Canvas.SetLeft(card, x);
            Canvas.SetTop(card, y);
            _canvas.Children.Add(card);
        }
    }
}
