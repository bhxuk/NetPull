using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using NetPull.Core;
using NetPull.Monitor;
using WinForms = System.Windows.Forms;

namespace NetPull.UI
{
    /// <summary>The tray icon. It draws a tiny live graph of the last few seconds; hover it for the speeds.</summary>
    public sealed class Tray : IDisposable
    {
        private readonly WinForms.NotifyIcon _icon;
        private readonly WinForms.ToolStripMenuItem _miniItem;
        private readonly WinForms.ToolStripMenuItem _popupsItem;
        private WinForms.ToolStripMenuItem _update;
        private readonly Queue<(long rx, long tx)> _history = new Queue<(long, long)>();
        private IntPtr _lastHandle = IntPtr.Zero;
        private Icon _lastIcon;
        private Action _onClick;

        private static readonly Color Down = Color.FromArgb(0x22, 0xE1, 0xF0);
        private static readonly Color Up = Color.FromArgb(0xFF, 0x4F, 0xD8);
        private static readonly Color Back = Color.FromArgb(0x0B, 0x0E, 0x14);

        public Tray()
        {
            _icon = new WinForms.NotifyIcon { Text = "NetPull", Visible = true };
            try { _icon.Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath); }
            catch { _icon.Icon = SystemIcons.Application; }

            var menu = new WinForms.ContextMenuStrip();
            menu.Items.Add("Open NetPull", null, (s, e) => App.Instance.ShowMain());
            menu.Items.Add("Usage", null, (s, e) => App.Instance.ShowMain("usage"));
            menu.Items.Add("Connections", null, (s, e) => App.Instance.ShowMain("connections"));
            menu.Items.Add(new WinForms.ToolStripSeparator());
            _miniItem = new WinForms.ToolStripMenuItem("Mini graph") { CheckOnClick = false };
            _miniItem.Click += (s, e) => App.Instance.ShowMiniGraph(!Settings.Current.MiniGraph);
            menu.Items.Add(_miniItem);
            _popupsItem = new WinForms.ToolStripMenuItem("Alert pop-ups");
            _popupsItem.Click += (s, e) =>
            {
                Settings.Current.Popups = !Settings.Current.Popups;
                Settings.Save();
                SyncMiniItem();
            };
            menu.Items.Add(_popupsItem);
            menu.Items.Add("Scan my network now", null, (s, e) => _ = LanWatch.ScanNow());
            menu.Items.Add(new WinForms.ToolStripSeparator());
            menu.Items.Add("Exit NetPull", null, (s, e) => App.Instance.Quit());
            menu.Opening += (s, e) => SyncMiniItem();
            _icon.ContextMenuStrip = menu;

            _icon.BalloonTipClicked += (s, e) => { var a = _onClick; _onClick = null; a?.Invoke(); };
            _icon.BalloonTipClosed += (s, e) => _onClick = null;
            _icon.MouseClick += (s, e) =>
            {
                if (e.Button == WinForms.MouseButtons.Left) App.Instance.ShowMain();
            };
            SyncMiniItem();
        }

        public void SyncMiniItem()
        {
            _miniItem.Checked = Settings.Current.MiniGraph;
            _popupsItem.Checked = Settings.Current.Popups;
        }

        public void Update(SecondSample s)
        {
            _history.Enqueue((s.Rx, s.Tx));
            while (_history.Count > 10) _history.Dequeue();
            string tip = "NetPull\n↓ " + Format.Rate(s.Rx) + "   ↑ " + Format.Rate(s.Tx);
            if (!TrafficMonitor.Instance.IsRunning) tip = "NetPull\nNot watching traffic";
            try { _icon.Text = tip.Length > 120 ? tip.Substring(0, 120) : tip; } catch { }
            DrawIcon();
        }

        private void DrawIcon()
        {
            try
            {
                int size = Math.Max(16, WinForms.SystemInformation.SmallIconSize.Width);
                using var bmp = new Bitmap(size, size);
                using (var g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.None;
                    g.Clear(Color.Transparent);
                    using (var bg = new SolidBrush(Back)) g.FillRectangle(bg, 0, 0, size, size);
                    using (var edge = new Pen(Color.FromArgb(90, Down))) g.DrawRectangle(edge, 0, 0, size - 1, size - 1);

                    var h = _history.ToArray();
                    double max = Math.Max(16 * 1024, h.Length == 0 ? 0 : h.Max(x => Math.Max(x.rx, x.tx)));
                    int inner = size - 2;
                    int cols = h.Length;
                    float colW = inner / 10f;
                    using var down = new SolidBrush(Down);
                    using var up = new SolidBrush(Up);
                    for (int i = 0; i < cols; i++)
                    {
                        float x = 1 + (10 - cols + i) * colW;
                        float hd = (float)(Math.Sqrt(h[i].rx / max) * (inner - 1));
                        float hu = (float)(Math.Sqrt(h[i].tx / max) * (inner - 1));
                        if (h[i].rx > 0) hd = Math.Max(1, hd);
                        if (h[i].tx > 0) hu = Math.Max(1, hu);
                        g.FillRectangle(down, x, size - 1 - hd, Math.Max(1, colW - 0.5f), hd);
                        g.FillRectangle(up, x + colW / 2, size - 1 - hu, Math.Max(1, colW / 2 - 0.5f), hu);
                    }
                    if (!TrafficMonitor.Instance.IsRunning)
                        using (var warn = new SolidBrush(Color.FromArgb(0xFF, 0x5C, 0x7A))) g.FillRectangle(warn, size - 5, 1, 4, 4);
                }
                IntPtr handle = bmp.GetHicon();
                var icon = Icon.FromHandle(handle);
                _icon.Icon = icon;
                if (_lastHandle != IntPtr.Zero) Native.DestroyIcon(_lastHandle);
                _lastIcon?.Dispose();
                _lastHandle = handle;
                _lastIcon = icon;
            }
            catch (Exception ex) { Log.Write("Tray icon: " + ex.Message); }
        }

        public void Notify(string title, string text, Action onClick = null)
        {
            _onClick = onClick;
            try { _icon.ShowBalloonTip(5000, title, string.IsNullOrEmpty(text) ? " " : text, WinForms.ToolTipIcon.None); }
            catch { }
        }

        public void ShowUpdateItem(string version)
        {
            var menu = _icon.ContextMenuStrip;
            if (_update == null)
            {
                _update = new WinForms.ToolStripMenuItem();
                _update.Click += (s, e) => _ = App.Instance.InstallUpdate();
                menu.Items.Insert(0, _update);
                menu.Items.Insert(1, new WinForms.ToolStripSeparator());
            }
            _update.Text = "Install update " + version;
        }

        public void Dispose()
        {
            _icon.Visible = false;
            _icon.Dispose();
            if (_lastHandle != IntPtr.Zero) Native.DestroyIcon(_lastHandle);
        }
    }
}
