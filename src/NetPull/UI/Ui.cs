using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace NetPull.UI
{
    /// <summary>Small helpers so the pages can be built in code without repeating themselves.</summary>
    public static class Ui
    {
        public static readonly FontFamily IconFont = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");

        public const string GlyphGraph = "";
        public const string GlyphUsage = "";
        public const string GlyphConnections = "";
        public const string GlyphDevices = "";
        public const string GlyphAlerts = "";
        public const string GlyphSettings = "";
        public const string GlyphDown = "";
        public const string GlyphUp = "";
        public const string GlyphSearch = "";
        public const string GlyphRefresh = "";
        public const string GlyphBack = "";
        public const string GlyphForward = "";
        public const string GlyphExport = "";
        public const string GlyphApp = "";
        public const string GlyphShield = "";
        public const string GlyphDelete = "";
        public const string GlyphEdit = "";
        public const string GlyphInfo = "";

        public static Brush B(string key) => (Brush)Application.Current.Resources[key];
        public static Color C(string key) => (Color)Application.Current.Resources[key];

        public static TextBlock Text(string text, double size = 13, string brush = "TextBrush", FontWeight? weight = null)
        {
            return new TextBlock
            {
                Text = text,
                FontSize = size,
                Foreground = B(brush),
                FontWeight = weight ?? FontWeights.Normal,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            };
        }

        public static TextBlock Wrap(string text, double size = 13, string brush = "MutedBrush")
        {
            var t = Text(text, size, brush);
            t.TextWrapping = TextWrapping.Wrap;
            t.TextTrimming = TextTrimming.None;
            return t;
        }

        public static TextBlock Icon(string glyph, double size = 14, string brush = "TextBrush")
        {
            return new TextBlock
            {
                Text = glyph,
                FontFamily = IconFont,
                FontSize = size,
                Foreground = B(brush),
                VerticalAlignment = VerticalAlignment.Center
            };
        }

        public static Border Card(UIElement child, double padding = 18)
        {
            return new Border
            {
                Background = B("PanelBrush"),
                BorderBrush = B("LineBrush"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(padding),
                Child = child
            };
        }

        public static Button Button(string text, Action click, string style = null, string glyph = null)
        {
            object content = text;
            if (glyph != null)
            {
                var sp = new StackPanel { Orientation = Orientation.Horizontal };
                sp.Children.Add(new TextBlock { Text = glyph, FontFamily = IconFont, FontSize = 13, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, string.IsNullOrEmpty(text) ? 0 : 8, 0) });
                if (!string.IsNullOrEmpty(text)) sp.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
                content = sp;
            }
            var b = new Button { Content = content };
            if (style != null) b.Style = (Style)Application.Current.Resources[style];
            b.Click += (s, e) => click?.Invoke();
            return b;
        }

        public static CheckBox Switch(string label, string help, bool value, Action<bool> changed)
        {
            var sp = new StackPanel();
            sp.Children.Add(Text(label, 14));
            if (!string.IsNullOrEmpty(help))
            {
                var h = Wrap(help, 12.5);
                h.Margin = new Thickness(0, 2, 0, 0);
                sp.Children.Add(h);
            }
            var cb = new CheckBox { Content = sp, IsChecked = value, Margin = new Thickness(0, 8, 0, 8) };
            cb.Checked += (s, e) => changed(true);
            cb.Unchecked += (s, e) => changed(false);
            return cb;
        }

        public static ComboBox Combo(IEnumerable<string> items, int selected, Action<int> changed, double width = 160)
        {
            var c = new ComboBox { Width = width, VerticalAlignment = VerticalAlignment.Center };
            foreach (var i in items) c.Items.Add(new ComboBoxItem { Content = i });
            c.SelectedIndex = Math.Max(0, Math.Min(selected, c.Items.Count - 1));
            c.SelectionChanged += (s, e) => { if (c.SelectedIndex >= 0) changed(c.SelectedIndex); };
            return c;
        }

        public static RadioButton Chip(string text, string group, bool isChecked, Action onChecked)
        {
            var r = new RadioButton { Content = text, GroupName = group, IsChecked = isChecked, Style = (Style)Application.Current.Resources["Chip"] };
            r.Checked += (s, e) => onChecked();
            return r;
        }

        /// <summary>A row with a label on the left and a control on the right.</summary>
        public static Grid Row(string label, string help, FrameworkElement control)
        {
            var g = new Grid { Margin = new Thickness(0, 8, 0, 8) };
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var sp = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            sp.Children.Add(Text(label, 14));
            if (!string.IsNullOrEmpty(help))
            {
                var h = Wrap(help, 12.5);
                h.Margin = new Thickness(0, 2, 16, 0);
                sp.Children.Add(h);
            }
            g.Children.Add(sp);
            Grid.SetColumn(control, 1);
            control.VerticalAlignment = VerticalAlignment.Center;
            g.Children.Add(control);
            return g;
        }

        public static TextBlock Heading(string text)
        {
            var t = Text(text, 12, "MutedBrush", FontWeights.SemiBold);
            t.Margin = new Thickness(0, 18, 0, 6);
            return t;
        }

        public static Image AppIcon(ImageSource src, double size = 20)
        {
            var img = new Image { Source = src, Width = size, Height = size, VerticalAlignment = VerticalAlignment.Center };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            return img;
        }

        /// <summary>A small coloured square with a label, for legends.</summary>
        public static StackPanel Legend(string brush, string text)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 14, 0) };
            sp.Children.Add(new Border { Width = 10, Height = 10, CornerRadius = new CornerRadius(3), Background = B(brush), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
            sp.Children.Add(Text(text, 12.5, "MutedBrush"));
            return sp;
        }

        public static FrameworkElement Empty(string title, string body)
        {
            var sp = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 40, 0, 40), MaxWidth = 420 };
            var t = Text(title, 15, "TextBrush", FontWeights.SemiBold);
            t.HorizontalAlignment = HorizontalAlignment.Center;
            sp.Children.Add(t);
            var b = Wrap(body, 13);
            b.TextAlignment = TextAlignment.Center;
            b.Margin = new Thickness(0, 6, 0, 0);
            sp.Children.Add(b);
            return sp;
        }

        public static string Plural(int n, string one, string many) => n + " " + (n == 1 ? one : many);

        public static T Pick<T>(this IList<T> list, int i, T fallback) => i >= 0 && i < list.Count ? list[i] : fallback;
    }
}
