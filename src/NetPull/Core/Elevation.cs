using System;
using System.Diagnostics;
using System.IO;
using System.Security;
using System.Security.Principal;
using System.Text;

namespace NetPull.Core
{
    /// <summary>NetPull needs admin rights to see which app owns each byte. These helpers keep that to a single
    /// prompt at install time: a scheduled task starts NetPull with admin rights, at sign-in and on demand.</summary>
    public static class Elevation
    {
        public const string TaskName = "NetPull";

        public static bool IsAdmin
        {
            get
            {
                try
                {
                    using var id = WindowsIdentity.GetCurrent();
                    return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
                }
                catch { return false; }
            }
        }

        private static int Schtasks(string args, out string output)
        {
            output = "";
            try
            {
                var psi = new ProcessStartInfo("schtasks.exe", args)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using var p = Process.Start(psi);
                output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
                p.WaitForExit(15000);
                return p.ExitCode;
            }
            catch (Exception ex)
            {
                output = ex.Message;
                return -1;
            }
        }

        public static bool TaskExists() => Schtasks("/Query /TN \"" + TaskName + "\"", out _) == 0;

        /// <summary>Starts the task, which runs NetPull with admin rights without a prompt.</summary>
        public static bool RunTask() => Schtasks("/Run /TN \"" + TaskName + "\"", out _) == 0;

        /// <summary>Creates or updates the task. With atSignIn off the task stays, without a trigger, so the
        /// Start menu shortcut can still open NetPull without a prompt.</summary>
        public static void ApplyTask(bool atSignIn)
        {
            if (!IsAdmin) return;
            try
            {
                string exe = Environment.ProcessPath ?? "";
                string user = WindowsIdentity.GetCurrent().Name;
                string trigger = atSignIn
                    ? "<Triggers><LogonTrigger><Enabled>true</Enabled><UserId>" + SecurityElement.Escape(user) + "</UserId><Delay>PT5S</Delay></LogonTrigger></Triggers>"
                    : "<Triggers />";
                string xml =
"<?xml version=\"1.0\" encoding=\"UTF-16\"?>\r\n" +
"<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">\r\n" +
"  <RegistrationInfo><Description>Starts NetPull so it can record which apps use the network.</Description></RegistrationInfo>\r\n" +
"  " + trigger + "\r\n" +
"  <Principals><Principal id=\"Author\"><UserId>" + SecurityElement.Escape(user) + "</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>\r\n" +
"  <Settings>\r\n" +
"    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>\r\n" +
"    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>\r\n" +
"    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>\r\n" +
"    <AllowHardTerminate>true</AllowHardTerminate>\r\n" +
"    <StartWhenAvailable>false</StartWhenAvailable>\r\n" +
"    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>\r\n" +
"    <IdleSettings><StopOnIdleEnd>false</StopOnIdleEnd><RestartOnIdle>false</RestartOnIdle></IdleSettings>\r\n" +
"    <AllowStartOnDemand>true</AllowStartOnDemand>\r\n" +
"    <Enabled>true</Enabled>\r\n" +
"    <Hidden>false</Hidden>\r\n" +
"    <RunOnlyIfIdle>false</RunOnlyIfIdle>\r\n" +
"    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>\r\n" +
"    <Priority>7</Priority>\r\n" +
"  </Settings>\r\n" +
"  <Actions Context=\"Author\"><Exec><Command>\"" + SecurityElement.Escape(exe) + "\"</Command><Arguments>--background</Arguments></Exec></Actions>\r\n" +
"</Task>\r\n";
                string file = Path.Combine(Path.GetTempPath(), "NetPull-task.xml");
                File.WriteAllText(file, xml, Encoding.Unicode);
                int code = Schtasks("/Create /TN \"" + TaskName + "\" /XML \"" + file + "\" /F", out string output);
                try { File.Delete(file); } catch { }
                if (code != 0) Log.Write("Couldn't create the startup task: " + output.Trim());
            }
            catch (Exception ex) { Log.Write("Startup task: " + ex.Message); }
        }

        /// <summary>Re-opens NetPull with a UAC prompt. Returns false if the user said no.</summary>
        public static bool RelaunchAsAdmin(string args)
        {
            try
            {
                Process.Start(new ProcessStartInfo(Environment.ProcessPath, args) { UseShellExecute = true, Verb = "runas" });
                return true;
            }
            catch { return false; }
        }

        /// <summary>Asks the running copy of NetPull to open its window.</summary>
        public static void SignalShow()
        {
            try { File.WriteAllText(Paths.ShowSignal, DateTime.UtcNow.ToString("o")); } catch { }
        }

        public static bool ConsumeFreshSignal()
        {
            try
            {
                if (!File.Exists(Paths.ShowSignal)) return false;
                bool fresh = DateTime.UtcNow - File.GetLastWriteTimeUtc(Paths.ShowSignal) < TimeSpan.FromSeconds(90);
                File.Delete(Paths.ShowSignal);
                return fresh;
            }
            catch { return false; }
        }
    }

    public static class Shell
    {
        /// <summary>Opens a web page. Goes through Explorer so the browser doesn't run with NetPull's admin rights.</summary>
        public static void OpenUrl(string url)
        {
            try { Process.Start(new ProcessStartInfo("explorer.exe", "\"" + url + "\"") { UseShellExecute = true }); }
            catch (Exception ex) { Log.Write("Open url: " + ex.Message); }
        }

        public static void ShowInFolder(string path)
        {
            try
            {
                if (File.Exists(path)) Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + path + "\"") { UseShellExecute = true });
                else if (Directory.Exists(path)) Process.Start(new ProcessStartInfo("explorer.exe", "\"" + path + "\"") { UseShellExecute = true });
            }
            catch (Exception ex) { Log.Write("Show in folder: " + ex.Message); }
        }
    }
}
