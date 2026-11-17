using System;
using System.Runtime.InteropServices;
using System.Text;

namespace NetPull.Core
{
    public static class Native
    {
        public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GetProcessTimes(IntPtr process, out long creation, out long exit, out long kernel, out long user);

        public const int AF_INET = 2;
        public const int AF_INET6 = 23;
        public const int TCP_TABLE_OWNER_PID_ALL = 5;
        public const int UDP_TABLE_OWNER_PID = 1;

        [DllImport("iphlpapi.dll", SetLastError = true)]
        public static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool sort, int af, int tableClass, uint reserved);

        [DllImport("iphlpapi.dll", SetLastError = true)]
        public static extern uint GetExtendedUdpTable(IntPtr table, ref int size, bool sort, int af, int tableClass, uint reserved);

        [DllImport("iphlpapi.dll")]
        public static extern int SendARP(uint destIp, uint srcIp, byte[] macAddr, ref int physAddrLen);

        [DllImport("iphlpapi.dll")]
        public static extern int GetIpNetTable(IntPtr table, ref int size, bool sort);

        [DllImport("user32.dll")]
        public static extern bool DestroyIcon(IntPtr handle);

        [DllImport("dwmapi.dll")]
        public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        /// <summary>Gives a window the dark title bar.</summary>
        public static void DarkTitleBar(IntPtr hwnd)
        {
            try
            {
                int on = 1;
                if (DwmSetWindowAttribute(hwnd, 20, ref on, 4) != 0) DwmSetWindowAttribute(hwnd, 19, ref on, 4);
                int caption = 0x00140E0B; // COLORREF (BGR) of the window background, Windows 11 only
                DwmSetWindowAttribute(hwnd, 35, ref caption, 4);
            }
            catch { }
        }

        public static string ProcessPath(int pid, out long startTime)
        {
            startTime = 0;
            IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero) return null;
            try
            {
                if (GetProcessTimes(h, out long c, out _, out _, out _)) startTime = c;
                var sb = new StringBuilder(1024);
                int size = sb.Capacity;
                return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString(0, size) : null;
            }
            finally { CloseHandle(h); }
        }

        public static long ProcessStartTime(int pid)
        {
            IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero) return -1;
            try { return GetProcessTimes(h, out long c, out _, out _, out _) ? c : -1; }
            finally { CloseHandle(h); }
        }

        /// <summary>Ports in Windows' tables are stored in network byte order.</summary>
        public static int Port(uint raw) => (int)(((raw & 0xFF) << 8) | ((raw >> 8) & 0xFF));
    }
}
