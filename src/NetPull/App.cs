using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using NetPull.Core;
using NetPull.Monitor;
using NetPull.UI;

namespace NetPull
{
    public static class Program
    {
        private static Mutex _mutex;

        [STAThread]
        public static int Main(string[] args)
        {
            bool background = args.Any(a => a.Equals("--background", StringComparison.OrdinalIgnoreCase));

            if (!Elevation.IsAdmin)
            {
                // NetPull runs with admin rights. Without them, hand over to the running copy or the startup task.
                int me = Environment.ProcessId;
                bool running = Process.GetProcessesByName("NetPull").Any(p => p.Id != me);
                if (running)
                {
                    if (!background) Elevation.SignalShow();
                    return 0;
                }
                if (Elevation.TaskExists())
                {
                    if (!background) Elevation.SignalShow();
                    if (Elevation.RunTask()) return 0;
                }
                if (Elevation.RelaunchAsAdmin(string.Join(" ", args))) return 0;
                MessageBox.Show(
                    "NetPull needs administrator rights to see which apps are using your network.\n\nOpen it again and choose Yes when Windows asks.",
                    "NetPull", MessageBoxButton.OK, MessageBoxImage.Information);
                return 1;
            }

            _mutex = new Mutex(true, "NetPull.SingleInstance.bhxuk", out bool isNew);
            if (!isNew)
            {
                if (!background) Elevation.SignalShow();
                return 0;
            }

            var app = new App { Background = background };
            int code = app.Run();
            GC.KeepAlive(_mutex);
            return code;
        }
    }

    public class App : Application
    {
        public bool Background { get; set; }
        public static App Instance => (App)Current;

        public Tray Tray { get; private set; }
        public HubWindow Hub { get; private set; }
        public MiniGraph Mini { get; private set; }

        private FileSystemWatcher _signalWatcher;
        private DispatcherTimer _housekeeping;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            ShutdownMode = ShutdownMode.OnExplicitShutdown;

            DispatcherUnhandledException += (s, ex) =>
            {
                Log.Write("Unhandled: " + ex.Exception);
                ex.Handled = true;
            };
            AppDomain.CurrentDomain.UnhandledException += (s, ex) => Log.Write("Fatal: " + ex.ExceptionObject);
            TaskScheduler.UnobservedTaskException += (s, ex) => { Log.Write("Task: " + ex.Exception); ex.SetObserved(); };

            Settings.Load();
            try
            {
                Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/NetPull;component/Themes/Styles.xaml", UriKind.Absolute) });
            }
            catch (Exception ex) { Log.Write("Styles: " + ex.Message); }

            Db.Open();
            Adapters.Start();
            IpLookup.Start();
            AlertEngine.Start();
            TrafficMonitor.Instance.Start();
            LanWatch.Start();
            NetworkModel.Start();

            // Keep the startup task in step with the setting (it also lets the Start menu open NetPull without a prompt).
            Task.Run(() => Elevation.ApplyTask(Settings.Current.StartWithWindows));

            Tray = new Tray();
            TrafficMonitor.Instance.Tick += s => Dispatcher.BeginInvoke(new Action(() => OnTick(s)));
            AlertEngine.Raised += a => Dispatcher.BeginInvoke(new Action(() => OnAlert(a)));

            if (Settings.Current.MiniGraph) ShowMiniGraph(true);

            // Another copy of NetPull (without admin rights) asks this one to open by touching a file.
            try
            {
                _signalWatcher = new FileSystemWatcher(Paths.Data, Path.GetFileName(Paths.ShowSignal))
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.CreationTime
                };
                FileSystemEventHandler handler = (s, ev) => Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (Elevation.ConsumeFreshSignal()) ShowMain();
                }));
                _signalWatcher.Created += handler;
                _signalWatcher.Changed += handler;
                _signalWatcher.EnableRaisingEvents = true;
            }
            catch (Exception ex) { Log.Write("Signal watcher: " + ex.Message); }

            bool signalled = Elevation.ConsumeFreshSignal();
            if (Settings.Current.FirstRun || !Background || signalled) ShowMain();
            if (Settings.Current.FirstRun)
            {
                Settings.Current.FirstRun = false;
                Settings.Save();
            }

            // Tidy old history once an hour; check the data cap and for updates now and then.
            _housekeeping = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
            int runs = 0;
            _housekeeping.Tick += async (s, ev) =>
            {
                _housekeeping.Interval = TimeSpan.FromHours(1);
                runs++;
                await Task.Run(() =>
                {
                    try { Db.Prune(Settings.Current.KeepDays); } catch (Exception ex) { Log.Write("Prune: " + ex.Message); }
                    try { if (Settings.Current.CapEnabled) AlertEngine.CheckCap(); } catch (Exception ex) { Log.Write("Cap: " + ex.Message); }
                });
                if (Settings.Current.CheckForUpdates && (runs == 1 || runs % 6 == 0)) await CheckForUpdate();
            };
            _housekeeping.Start();
        }

        private void OnTick(SecondSample s)
        {
            Tray?.Update(s);
            Mini?.Update(s);
            Hub?.OnTick(s);
        }

        private void OnAlert(AlertRecord a)
        {
            if (Settings.Current.Popups)
                Tray?.Notify(a.Title, a.Body, () => ShowMain("alerts"));
            Hub?.RefreshBadge();
        }

        public void ShowMain(string page = null)
        {
            if (Hub == null)
            {
                Hub = new HubWindow();
                Hub.Closed += (s, e) => Hub = null;
            }
            if (page != null) Hub.Navigate(page);
            Hub.Show();
            if (Hub.WindowState == WindowState.Minimized) Hub.WindowState = WindowState.Normal;
            Hub.Activate();
        }

        public void ShowMiniGraph(bool on)
        {
            Settings.Current.MiniGraph = on;
            Settings.Save();
            if (on)
            {
                if (Mini == null)
                {
                    Mini = new MiniGraph();
                    Mini.Closed += (s, e) => Mini = null;
                }
                Mini.Show();
            }
            else
            {
                Mini?.Close();
                Mini = null;
            }
            Tray?.SyncMiniItem();
        }

        private string _notifiedVersion;

        public async Task CheckForUpdate()
        {
            if (!await Updates.Check()) return;
            if (Settings.Current.AutoUpdate)
            {
                await InstallUpdate();
                return;
            }
            Tray.ShowUpdateItem(Updates.LatestVersion);
            if (_notifiedVersion == Updates.LatestVersion) return;
            _notifiedVersion = Updates.LatestVersion;
            Tray.Notify("NetPull " + Updates.LatestVersion + " is available", "Click here to install it. NetPull reopens by itself.", () => _ = InstallUpdate());
        }

        private bool _installing;

        public async Task<bool> InstallUpdate(Action<string> status = null)
        {
            if (_installing) return false;
            _installing = true;
            try
            {
                if (await Updates.DownloadAndInstall(status))
                {
                    Quit();
                    return true;
                }
                return false;
            }
            finally { _installing = false; }
        }

        public void Quit()
        {
            try { TrafficMonitor.Instance.Dispose(); } catch { }
            try { Tray?.Dispose(); } catch { }
            try { Mini?.Close(); } catch { }
            Shutdown();
        }
    }
}
