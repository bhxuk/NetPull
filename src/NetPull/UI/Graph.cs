using System;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using NetPull.Core;

namespace NetPull.UI
{
    /// <summary>The glowing download/upload graph. Values are bytes per second, one per point.</summary>
    public sealed class Graph : FrameworkElement
    {
        public double[] Down = Array.Empty<double>();
        public double[] Up = Array.Empty<double>();
        public long StartUnix;
        public int Step = 1;
        public bool Compact;          // the mini graph: no axes or labels
        public int Selected = -1;

        public event Action<int> PointClicked;

        private int _hover = -1;
        private static readonly Typeface Face = new Typeface("Segoe UI");
        private readonly Color _down = Color.FromRgb(0x22, 0xE1, 0xF0);
        private readonly Color _up = Color.FromRgb(0xFF, 0x4F, 0xD8);

        public Graph()
        {
            ClipToBounds = true;
            Cursor = Cursors.Cross;
        }

        public void SetData(double[] down, double[] up, long startUnix, int step)
        {
            Down = down ?? Array.Empty<double>();
            Up = up ?? Array.Empty<double>();
            StartUnix = startUnix;
            Step = Math.Max(1, step);
            if (Selected >= Down.Length) Selected = -1;
            InvalidateVisual();
        }

        private Rect Plot => Compact
            ? new Rect(1, 2, Math.Max(1, ActualWidth - 2), Math.Max(1, ActualHeight - 3))
            : new Rect(70, 12, Math.Max(1, ActualWidth - 82), Math.Max(1, ActualHeight - 40));

        private int Count => Math.Min(Down.Length, Up.Length);

        private double X(int i, Rect p) => Count <= 1 ? p.Left : p.Left + i * p.Width / (Count - 1);

        private static double Nice(double v)
        {
            if (v <= 0) return 1;
            double e = Math.Pow(10, Math.Floor(Math.Log10(v)));
            double m = v / e;
            double n = m <= 1 ? 1 : m <= 2 ? 2 : m <= 2.5 ? 2.5 : m <= 5 ? 5 : 10;
            return n * e;
        }

        protected override void OnRender(DrawingContext dc)
        {
            var p = Plot;
            double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));

            int n = Count;
            double max = 0;
            for (int i = 0; i < n; i++) max = Math.Max(max, Math.Max(Down[i], Up[i]));
            double floor = Settings.Current.UseBits ? 125_000 / 8.0 : 10 * 1024;
            double top = Compact ? Math.Max(max * 1.1, 4 * 1024) : Nice(Math.Max(max * 1.12, floor));

            var gridPen = new Pen(new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)), 1);
            gridPen.Freeze();
            var muted = new SolidColorBrush(Color.FromRgb(0x8A, 0x94, 0xA8));
            muted.Freeze();

            if (!Compact)
            {
                for (int g = 0; g <= 4; g++)
                {
                    double y = Math.Round(p.Bottom - g * p.Height / 4) + 0.5;
                    dc.DrawLine(gridPen, new Point(p.Left, y), new Point(p.Right, y));
                    var ft = new FormattedText(Format.Rate(top * g / 4), CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Face, 11, muted, dpi);
                    dc.DrawText(ft, new Point(p.Left - ft.Width - 10, y - ft.Height / 2));
                }
                DrawTimeLabels(dc, p, muted, dpi);
            }

            if (n >= 2)
            {
                if (Selected >= 0 && Selected < n && !Compact)
                {
                    double w = Math.Max(4, p.Width / (n - 1));
                    var band = new SolidColorBrush(Color.FromArgb(0x26, 0x22, 0xE1, 0xF0));
                    dc.DrawRectangle(band, null, new Rect(X(Selected, p) - w / 2, p.Top, w, p.Height));
                }
                DrawSeries(dc, p, Down, top, _down);
                DrawSeries(dc, p, Up, top, _up);
            }
            else if (!Compact)
            {
                var ft = new FormattedText("Waiting for traffic...", CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Face, 13, muted, dpi);
                dc.DrawText(ft, new Point(p.Left + (p.Width - ft.Width) / 2, p.Top + (p.Height - ft.Height) / 2));
            }

            if (_hover >= 0 && _hover < n && !Compact) DrawHover(dc, p, dpi);
        }

        private void DrawSeries(DrawingContext dc, Rect p, double[] v, double top, Color c)
        {
            int n = Count;
            var line = new StreamGeometry();
            var area = new StreamGeometry();
            using (var ctx = line.Open())
            using (var act = area.Open())
            {
                act.BeginFigure(new Point(X(0, p), p.Bottom), true, true);
                for (int i = 0; i < n; i++)
                {
                    var pt = new Point(X(i, p), p.Bottom - Math.Min(1, v[i] / top) * p.Height);
                    if (i == 0) ctx.BeginFigure(pt, false, false); else ctx.LineTo(pt, true, true);
                    act.LineTo(pt, true, true);
                }
                act.LineTo(new Point(X(n - 1, p), p.Bottom), true, true);
            }
            line.Freeze(); area.Freeze();

            var fill = new LinearGradientBrush(Color.FromArgb(0x70, c.R, c.G, c.B), Color.FromArgb(0x04, c.R, c.G, c.B), 90);
            fill.Freeze();
            dc.DrawGeometry(fill, null, area);

            // A soft wide stroke under a thin bright one gives the neon glow.
            var glow = new Pen(new SolidColorBrush(Color.FromArgb(0x30, c.R, c.G, c.B)), Compact ? 3 : 7) { LineJoin = PenLineJoin.Round };
            glow.Freeze();
            dc.DrawGeometry(null, glow, line);
            var pen = new Pen(new SolidColorBrush(c), Compact ? 1.3 : 1.8) { LineJoin = PenLineJoin.Round };
            pen.Freeze();
            dc.DrawGeometry(null, pen, line);
        }

        private string TimeText(long unix, bool withDay)
        {
            var t = Format.FromUnix(unix).ToLocalTime();
            if (Step >= 3600) return Count * Step > 3 * 86400 ? t.ToString("ddd d", CultureInfo.CurrentCulture) : t.ToString("HH:mm", CultureInfo.CurrentCulture);
            if (Step >= 60) return withDay ? t.ToString("ddd HH:mm", CultureInfo.CurrentCulture) : t.ToString("HH:mm", CultureInfo.CurrentCulture);
            return t.ToString("HH:mm:ss", CultureInfo.CurrentCulture);
        }

        private void DrawTimeLabels(DrawingContext dc, Rect p, Brush muted, double dpi)
        {
            int n = Count;
            if (n < 2) return;
            int labels = Math.Max(2, (int)(p.Width / 120));
            for (int k = 0; k <= labels; k++)
            {
                int i = (int)Math.Round(k * (n - 1) / (double)labels);
                var ft = new FormattedText(TimeText(StartUnix + (long)i * Step, false), CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Face, 11, muted, dpi);
                double x = X(i, p) - ft.Width / 2;
                x = Math.Max(p.Left, Math.Min(p.Right - ft.Width, x));
                dc.DrawText(ft, new Point(x, p.Bottom + 8));
            }
        }

        private void DrawHover(DrawingContext dc, Rect p, double dpi)
        {
            double x = X(_hover, p);
            var linePen = new Pen(new SolidColorBrush(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF)), 1) { DashStyle = DashStyles.Dash };
            dc.DrawLine(linePen, new Point(x, p.Top), new Point(x, p.Bottom));

            var text = new SolidColorBrush(Color.FromRgb(0xE8, 0xEC, 0xF3));
            string when = TimeText(StartUnix + (long)_hover * Step, Step >= 60 && Step < 3600);
            if (Step >= 3600) when = Format.FromUnix(StartUnix + (long)_hover * Step).ToLocalTime().ToString("ddd d MMM, HH:00", CultureInfo.CurrentCulture);
            var t1 = new FormattedText(when, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Face, 11.5, text, dpi);
            var t2 = new FormattedText("↓ " + Format.Rate(Down[_hover]), CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Face, 12.5, new SolidColorBrush(_down), dpi);
            var t3 = new FormattedText("↑ " + Format.Rate(Up[_hover]), CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Face, 12.5, new SolidColorBrush(_up), dpi);
            double w = Math.Max(t1.Width, Math.Max(t2.Width, t3.Width)) + 20;
            double h = t1.Height + t2.Height + t3.Height + 16;
            double bx = x + 12;
            if (bx + w > p.Right) bx = x - 12 - w;
            var box = new Rect(bx, p.Top + 6, w, h);
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(0xEE, 0x18, 0x20, 0x30)), new Pen(new SolidColorBrush(Color.FromRgb(0x34, 0x40, 0x5A)), 1), box, 6, 6);
            dc.DrawText(t1, new Point(box.Left + 10, box.Top + 6));
            dc.DrawText(t2, new Point(box.Left + 10, box.Top + 8 + t1.Height));
            dc.DrawText(t3, new Point(box.Left + 10, box.Top + 8 + t1.Height + t2.Height));
        }

        private int IndexAt(Point pos)
        {
            var p = Plot;
            int n = Count;
            if (n < 2 || pos.X < p.Left - 6 || pos.X > p.Right + 6) return -1;
            return Math.Max(0, Math.Min(n - 1, (int)Math.Round((pos.X - p.Left) / p.Width * (n - 1))));
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (Compact) return;
            int i = IndexAt(e.GetPosition(this));
            if (i != _hover) { _hover = i; InvalidateVisual(); }
        }

        protected override void OnMouseLeave(MouseEventArgs e)
        {
            base.OnMouseLeave(e);
            if (_hover != -1) { _hover = -1; InvalidateVisual(); }
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonUp(e);
            if (Compact) return;
            int i = IndexAt(e.GetPosition(this));
            if (i < 0) return;
            Selected = i;
            InvalidateVisual();
            PointClicked?.Invoke(i);
        }
    }
}
