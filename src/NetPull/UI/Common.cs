using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Controls;
using NetPull.Core;
using NetPull.Monitor;

namespace NetPull.UI
{
    /// <summary>The "All adapters / Ethernet / Wi-Fi / Tailscale..." filter shared by the pages.</summary>
    public static class AdapterPicker
    {
        public static event Action Changed;

        /// <summary>The adapter id to show, or 0 for all.</summary>
        public static int Current
        {
            get
            {
                int id = Db.AdapterIdFor(Settings.Current.AdapterFilter);
                return id < 0 ? 0 : id;
            }
        }

        public static ComboBox Create()
        {
            var c = new ComboBox { Width = 210 };
            var adapters = new List<AdapterRecord>();
            void Fill()
            {
                c.Items.Clear();
                adapters.Clear();
                var active = new HashSet<int>(Adapters.Active().Select(a => a.Id));
                adapters.AddRange(Db.AllAdapters()
                    .OrderByDescending(a => active.Contains(a.Id))
                    .ThenBy(a => a.Kind == "Virtual" ? 1 : 0)
                    .ThenBy(a => a.Name));
                c.Items.Add(new ComboBoxItem { Content = "All adapters" });
                foreach (var a in adapters)
                    c.Items.Add(new ComboBoxItem { Content = a.Name + (a.Kind != null && !a.Name.Contains(a.Kind) ? "  ·  " + a.Kind : "") + (active.Contains(a.Id) ? "" : "  (not connected)"), ToolTip = a.Description });
                int idx = adapters.FindIndex(a => a.Guid.Equals(Settings.Current.AdapterFilter ?? "", StringComparison.OrdinalIgnoreCase));
                c.SelectedIndex = idx + 1;
            }
            Fill();
            bool filling = false;
            c.DropDownOpened += (s, e) => { filling = true; Fill(); filling = false; };
            c.SelectionChanged += (s, e) =>
            {
                if (filling || c.SelectedIndex < 0) return;
                string guid = c.SelectedIndex == 0 ? "" : adapters[c.SelectedIndex - 1].Guid;
                if (guid == (Settings.Current.AdapterFilter ?? "")) return;
                Settings.Current.AdapterFilter = guid;
                Settings.Save();
                Changed?.Invoke();
            };
            Changed += () =>
            {
                filling = true;
                int idx = adapters.FindIndex(a => a.Guid.Equals(Settings.Current.AdapterFilter ?? "", StringComparison.OrdinalIgnoreCase));
                c.SelectedIndex = idx + 1;
                filling = false;
            };
            return c;
        }
    }

    public static class Csv
    {
        private static string Cell(string s)
        {
            s ??= "";
            return s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
        }

        /// <summary>Asks where to save, then writes the rows. Returns the file name, or null if cancelled.</summary>
        public static string Save(string suggestedName, string[] header, IEnumerable<string[]> rows)
        {
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                FileName = suggestedName,
                DefaultExt = ".csv",
                Filter = "CSV file (*.csv)|*.csv",
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
            };
            if (dlg.ShowDialog() != true) return null;
            var sb = new StringBuilder();
            sb.AppendLine(string.Join(",", header.Select(Cell)));
            foreach (var r in rows) sb.AppendLine(string.Join(",", r.Select(Cell)));
            File.WriteAllText(dlg.FileName, sb.ToString(), new UTF8Encoding(true));
            return dlg.FileName;
        }
    }

    /// <summary>Works out the start and end of the time ranges used on the pages (local time).</summary>
    public static class Periods
    {
        public static readonly string[] Names = { "Today", "Yesterday", "This week", "This month", "Last 30 days", "Last 90 days" };

        public static (DateTime from, DateTime to) Range(int index)
        {
            var today = DateTime.Today;
            switch (index)
            {
                case 0: return (today, today.AddDays(1));
                case 1: return (today.AddDays(-1), today);
                case 2:
                    int diff = ((int)today.DayOfWeek + 6) % 7;   // weeks start on Monday
                    return (today.AddDays(-diff), today.AddDays(1));
                case 3: return (new DateTime(today.Year, today.Month, 1), today.AddDays(1));
                case 4: return (today.AddDays(-29), today.AddDays(1));
                case 5: return (today.AddDays(-89), today.AddDays(1));
                case 6: return (AlertEngine.CapPeriodStart(), AlertEngine.CapPeriodStart().AddMonths(1));
                default: return (today, today.AddDays(1));
            }
        }
    }
}
