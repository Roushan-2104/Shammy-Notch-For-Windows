using System.Net;
using System.Runtime.InteropServices;
using System.Text;

namespace Shammy.Modules.Agents;

/// <summary>
/// Finds the window a Claude Code session lives in, so "Open" can bring it forward. A hook's request comes from a
/// short-lived curl; its process leads up through the shell and Claude Code to the terminal or editor that hosts
/// them. When that chain shows no window (a terminal that adopted the console afterwards), a terminal or editor
/// window whose title names the session's folder is taken instead, if there is exactly one. Best effort: when
/// nothing is found the caller offers no Open.
/// </summary>
internal static class AgentWindows
{
    static readonly HashSet<string> Hosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "windowsterminal.exe", "code.exe", "code - insiders.exe", "cursor.exe", "windsurf.exe", "antigravity.exe", "zed.exe",
        "wezterm-gui.exe", "alacritty.exe", "mintty.exe", "conhost.exe", "openconsole.exe", "cmd.exe", "powershell.exe",
        "pwsh.exe", "warp.exe", "hyper.exe", "tabby.exe", "rider64.exe", "idea64.exe", "devenv.exe",
    };

    /// <summary>The process on the other end of a local connection (the hook's curl), or 0.</summary>
    /// <param name="clientPort">The port the request came from.</param>
    /// <param name="listenPort">Shammy's own port.</param>
    public static int ClientProcess(int clientPort, int listenPort)
    {
        int size = 0;
        _ = GetExtendedTcpTable(IntPtr.Zero, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_CONNECTIONS, 0);
        if (size <= 0 || size > 16 * 1024 * 1024) return 0;
        // The table can grow between the two calls.
        size += 4096;
        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedTcpTable(buffer, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_CONNECTIONS, 0) != 0) return 0;
            int rows = Marshal.ReadInt32(buffer);
            for (int i = 0; i < rows; i++)
            {
                IntPtr row = buffer + 4 + i * 24;
                int local = Port(Marshal.ReadInt32(row, 8)), remote = Port(Marshal.ReadInt32(row, 16));
                if (local == clientPort && remote == listenPort) return Marshal.ReadInt32(row, 20);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
        return 0;
    }

    static int Port(int networkOrder) => (ushort)IPAddress.NetworkToHostOrder((short)(networkOrder & 0xFFFF));

    /// <summary>The window for a session whose hook ran as <paramref name="pid"/> in <paramref name="folder"/>, or zero.</summary>
    public static IntPtr Find(int pid, string folder)
    {
        var processes = Processes();
        var windows = Windows();
        int me = Environment.ProcessId;

        for (int depth = 0; depth < 16 && pid > 4 && processes.TryGetValue(pid, out var p); depth++)
        {
            if (pid == me || p.Exe.Equals("explorer.exe", StringComparison.OrdinalIgnoreCase)) break;
            var own = windows.Where(w => w.Pid == pid).ToList();
            if (own.Count > 0) return (Named(own, folder) ?? own[0]).Handle;
            pid = p.Parent;
        }

        if (folder.Length < 3) return IntPtr.Zero;
        var titled = windows.Where(w => w.Pid != me && processes.TryGetValue(w.Pid, out var p) && Hosts.Contains(p.Exe)
            && w.Title.Contains(folder, StringComparison.OrdinalIgnoreCase)).ToList();
        return titled.Count == 1 ? titled[0].Handle : IntPtr.Zero;
    }

    static Window? Named(List<Window> windows, string folder) =>
        folder.Length == 0 ? null : windows.FirstOrDefault(w => w.Title.Contains(folder, StringComparison.OrdinalIgnoreCase));

    public static bool IsAlive(IntPtr hwnd) => hwnd != IntPtr.Zero && IsWindow(hwnd);

    /// <summary>Brings the window forward (restoring it if minimised); false when it is gone.</summary>
    public static bool Focus(IntPtr hwnd)
    {
        if (!IsAlive(hwnd)) return false;
        if (IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);
        Interop.Native.ForceForeground(hwnd);
        return true;
    }

    sealed record Window(IntPtr Handle, int Pid, string Title);

    static List<Window> Windows()
    {
        var list = new List<Window>();
        var title = new StringBuilder(260);
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd) || GetWindow(hwnd, GW_OWNER) != IntPtr.Zero) return true;
            if ((GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64() & WS_EX_TOOLWINDOW) != 0) return true;
            title.Clear();
            if (GetWindowText(hwnd, title, title.Capacity) <= 0) return true;
            GetWindowThreadProcessId(hwnd, out uint pid);
            list.Add(new Window(hwnd, (int)pid, title.ToString()));
            return true;
        }, IntPtr.Zero);
        return list;
    }

    /// <summary>Every process's parent and program name, from one snapshot.</summary>
    static Dictionary<int, (int Parent, string Exe)> Processes()
    {
        var map = new Dictionary<int, (int, string)>();
        IntPtr snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1)) return map;
        try
        {
            var entry = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
            if (!Process32FirstW(snapshot, ref entry)) return map;
            do map[(int)entry.th32ProcessID] = ((int)entry.th32ParentProcessID, entry.szExeFile ?? "");
            while (Process32NextW(snapshot, ref entry));
        }
        finally
        {
            CloseHandle(snapshot);
        }
        return map;
    }

    const int AF_INET = 2, TCP_TABLE_OWNER_PID_CONNECTIONS = 4, SW_RESTORE = 9, GWL_EXSTYLE = -20;
    const uint TH32CS_SNAPPROCESS = 0x2, GW_OWNER = 4;
    const long WS_EX_TOOLWINDOW = 0x80;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct PROCESSENTRY32W
    {
        public uint dwSize, cntUsage, th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID, cntThreads, th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
    }

    delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool sort, int family, int tableClass, uint reserved);

    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32FirstW(IntPtr snapshot, ref PROCESSENTRY32W entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32NextW(IntPtr snapshot, ref PROCESSENTRY32W entry);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hwnd, int command);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr hwnd, uint command);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
}
