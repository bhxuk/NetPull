using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using NetPull.Core;
using NetPull.Monitor;

namespace NetPull.UI
{
    /// <summary>A small floating speed graph. Drag to move, double-click to open NetPull, right-click for options.</summary>
    public sealed class MiniGraph : Window
    {
        private readonly Graph _graph = new Graph { Compact = true, IsHitTestVisible = false };
        private readonly TextBlock _down = Ui.Text("", 12.5, "DownBrush", FontWeights.SemiBold);
        private readonly TextBlock _up = Ui.Text("", 12.5, "UpBrush", FontWeights.SemiBold);
        private readonly Queue<(double rx, double tx)> _history = new Queue<(double, double)>();

        public MiniGraph()
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Topmost = Settings.Current.MiniGraphOnTop;
            Width = 230;
            Height = 78;
            Title = "NetPull mini graph";

            var s = Settings.Current;
            if (!double.IsNaN(s.MiniGraphLeft) && !double.IsNaN(s.MiniGraphTop)
                && s.MiniGraphLeft > SystemParameters.VirtualScreenLeft - 50 && s.MiniGraphLeft < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 50
                && s.MiniGraphTop > SystemParameters.VirtualScreenTop - 20 && s.MiniGraphTop < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 30)
            {
                Left = s.MiniGraphLeft; Top = s.MiniGraphTop;
            }
            else
            {
                Left = SystemParameters.WorkArea.Right - Width - 16;
                Top = SystemParameters.WorkArea.Bottom - Height - 16;
            }

            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition());
            var speeds = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 6, 10, 0) };
            speeds.Children.Add(new TextBlock { Text = "↓ ", Foreground = Ui.B("DownBrush"), FontSize = 12.5 });
            speeds.Children.Add(_down);
            speeds.Children.Add(new TextBlock { Text = "    ↑ ", Foreground = Ui.B("UpBrush"), FontSize = 12.5 });
            speeds.Children.Add(_up);
            grid.Children.Add(speeds);
            _graph.Margin = new Thickness(6, 2, 6, 6);
            Grid.SetRow(_graph, 1);
            grid.Children.Add(_graph);

            Content = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x0B, 0x0E, 0x14)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x55, 0x22, 0xE1, 0xF0)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Child = grid
            };

            MouseLeftButtonDown += (o, e) =>
            {
                if (e.ClickCount == 2) { App.Instance.ShowMain(); return; }
                try { DragMove(); } catch { }
                Settings.Current.MiniGraphLeft = Left;
                Settings.Current.MiniGraphTop = Top;
                Settings.Save();
            };

            var menu = new ContextMenu();
            var open = new MenuItem { Header = "Open NetPull" };
            open.Click += (o, e) => App.Instance.ShowMain();
            var top = new MenuItem { Header = "Keep on top" };
            top.Click += (o, e) =>
            {
                Settings.Current.MiniGraphOnTop = !Settings.Current.MiniGraphOnTop;
                Topmost = Settings.Current.MiniGraphOnTop;
                Settings.Save();
            };
            var hide = new MenuItem { Header = "Hide mini graph" };
            hide.Click += (o, e) => App.Instance.ShowMiniGraph(false);
            menu.Items.Add(open);
            menu.Items.Add(top);
            menu.Items.Add(new Separator());
            menu.Items.Add(hide);
            ContextMenu = menu;

            _down.Text = Format.Rate(0);
            _up.Text = Format.Rate(0);
        }

        public void Update(SecondSample s)
        {
            var (rx, tx) = s.For(AdapterPicker.Current);
            _down.Text = Format.Rate(rx);
            _up.Text = Format.Rate(tx);
            _history.Enqueue((rx, tx));
            while (_history.Count > 60) _history.Dequeue();
            var d = new double[60];
            var u = new double[60];
            int offset = 60 - _history.Count, i = 0;
            foreach (var h in _history) { d[offset + i] = h.rx; u[offset + i] = h.tx; i++; }
            _graph.SetData(d, u, s.Time - 59, 1);
        }
    }
}
