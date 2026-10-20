using System.Runtime.InteropServices;

namespace MakeItColor.Interop;

internal static class Native
{
    // ---------------------------------------------------------------- window styles (32-bit safe)

    public const int GWL_STYLE = -16;
    public const int WS_MINIMIZEBOX = 0x00020000;
    public const int WS_MAXIMIZEBOX = 0x00010000;

    // 64-bit Windows exports Get/SetWindowLongPtrW; 32-bit Windows only has Get/SetWindowLongW.
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

    public static IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex) =>
        IntPtr.Size == 8 ? GetWindowLongPtr64(hWnd, nIndex) : new IntPtr(GetWindowLong32(hWnd, nIndex));

    public static IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong) =>
        IntPtr.Size == 8 ? SetWindowLongPtr64(hWnd, nIndex, dwNewLong) : new IntPtr(SetWindowLong32(hWnd, nIndex, dwNewLong.ToInt32()));

    /// <summary>Removes the minimize / maximize buttons (dialogs).</summary>
    public static void MakeDialogFrame(IntPtr hwnd)
    {
        var style = GetWindowLongPtr(hwnd, GWL_STYLE).ToInt64();
        style &= ~(WS_MINIMIZEBOX | WS_MAXIMIZEBOX);
        SetWindowLongPtr(hwnd, GWL_STYLE, new IntPtr(style));
    }

    // ---------------------------------------------------------------- DWM: Mica, dark title bar

    [StructLayout(LayoutKind.Sequential)]
    public struct MARGINS { public int Left, Right, Top, Bottom; }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS margins);

    [DllImport("dwmapi.dll")]
    private static extern int DwmIsCompositionEnabled(out bool enabled);

    public static Version OsVersion { get; } = ReadOsVersion();

    /// <summary>The real Windows version (Environment.OSVersion is capped without a manifest entry on .NET Framework).</summary>
    private static Version ReadOsVersion()
    {
        try
        {
            var info = new OSVERSIONINFOEX { dwOSVersionInfoSize = Marshal.SizeOf<OSVERSIONINFOEX>() };
            if (RtlGetVersion(ref info) == 0)
                return new Version(info.dwMajorVersion, info.dwMinorVersion, info.dwBuildNumber);
        }
        catch
        {
        }
        return Environment.OSVersion.Version;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OSVERSIONINFOEX
    {
        public int dwOSVersionInfoSize;
        public int dwMajorVersion;
        public int dwMinorVersion;
        public int dwBuildNumber;
        public int dwPlatformId;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szCSDVersion;
        public ushort wServicePackMajor;
        public ushort wServicePackMinor;
        public ushort wSuiteMask;
        public byte wProductType;
        public byte wReserved;
    }

    [DllImport("ntdll.dll")]
    private static extern int RtlGetVersion(ref OSVERSIONINFOEX info);

    /// <summary>Windows 11 22H2 (build 22621) or later: the documented Mica backdrop.</summary>
    public static bool SupportsMica => OsVersion.Major >= 10 && OsVersion.Build >= 22621;

    /// <summary>Dark (or light) title bar. Windows 10 1809+ only; older Windows keeps its normal title bar.</summary>
    public static void SetDarkTitleBar(IntPtr hwnd, bool dark)
    {
        if (OsVersion.Major < 10 || OsVersion.Build < 17763) return;
        try
        {
            var value = dark ? 1 : 0;
            // DWMWA_USE_IMMERSIVE_DARK_MODE is 20 from Windows 10 20H1, 19 before that.
            var attribute = OsVersion.Build >= 18985 ? 20 : 19;
            DwmSetWindowAttribute(hwnd, attribute, ref value, sizeof(int));
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
        }
    }

    /// <summary>Turns on the Mica backdrop (Windows 11 22H2+). Returns false where it isn't available.</summary>
    public static bool TrySetMica(IntPtr hwnd)
    {
        if (!SupportsMica) return false;
        try
        {
            var margins = new MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
            DwmExtendFrameIntoClientArea(hwnd, ref margins);
            var backdrop = 2; // DWMSBT_MAINWINDOW (Mica)
            return DwmSetWindowAttribute(hwnd, 38, ref backdrop, sizeof(int)) == 0; // DWMWA_SYSTEMBACKDROP_TYPE
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    /// <summary>Rounded window corners for small dialogs on Windows 11 (they are the default for normal windows).</summary>
    public static void SetRoundCorners(IntPtr hwnd)
    {
        if (OsVersion.Build < 22000) return;
        try
        {
            var preference = 2; // DWMWCP_ROUND
            DwmSetWindowAttribute(hwnd, 33, ref preference, sizeof(int)); // DWMWA_WINDOW_CORNER_PREFERENCE
        }
        catch
        {
        }
    }

    // ---------------------------------------------------------------- shell

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    public const uint SHGFI_ICON = 0x000000100;
    public const uint SHGFI_TYPENAME = 0x000000400;
    public const uint SHGFI_SMALLICON = 0x000000001;
    public const uint SHGFI_LARGEICON = 0x000000000;
    public const uint SHGFI_USEFILEATTRIBUTES = 0x000000010;
    public const uint FILE_ATTRIBUTE_NORMAL = 0x80;
    public const uint FILE_ATTRIBUTE_DIRECTORY = 0x10;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr SHGetFileInfo(string path, uint attributes, ref SHFILEINFO info, uint size, uint flags);

    [DllImport("user32.dll")]
    public static extern bool DestroyIcon(IntPtr icon);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHParseDisplayName(string name, IntPtr bindingContext, out IntPtr pidl, uint sfgaoIn, out uint sfgaoOut);

    [DllImport("shell32.dll")]
    private static extern int SHOpenFolderAndSelectItems(IntPtr pidlFolder, uint count, IntPtr[] items, uint flags);

    [DllImport("ole32.dll")]
    private static extern void CoTaskMemFree(IntPtr pv);

    /// <summary>Opens Explorer at <paramref name="folder"/> with <paramref name="items"/> selected (just the folder if none).</summary>
    public static bool OpenFolderAndSelect(string folder, IReadOnlyList<string> items)
    {
        var pidls = new List<IntPtr>();
        IntPtr folderPidl = IntPtr.Zero;
        try
        {
            if (SHParseDisplayName(folder, IntPtr.Zero, out folderPidl, 0, out _) != 0) return false;
            foreach (var item in items.Take(64))
                if (SHParseDisplayName(item, IntPtr.Zero, out var pidl, 0, out _) == 0) pidls.Add(pidl);
            return SHOpenFolderAndSelectItems(folderPidl, (uint)pidls.Count, pidls.ToArray(), 0) == 0;
        }
        catch
        {
            return false;
        }
        finally
        {
            foreach (var pidl in pidls) CoTaskMemFree(pidl);
            if (folderPidl != IntPtr.Zero) CoTaskMemFree(folderPidl);
        }
    }

    // shellapi.h packs this structure to 1 byte in 32-bit Windows only, so there are two layouts.
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 1)]
    private struct SHFILEOPSTRUCT32
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT op);

    [DllImport("shell32.dll", EntryPoint = "SHFileOperation", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation32(ref SHFILEOPSTRUCT32 op);

    /// <summary>Moves files and folders to the Recycle Bin (silently). Returns false if anything failed.</summary>
    public static bool MoveToRecycleBin(IEnumerable<string> paths)
    {
        var list = string.Join("\0", paths) + "\0\0";
        const ushort flags = 0x0040 | 0x0010 | 0x0004 | 0x0400; // FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI
        if (IntPtr.Size == 4)
        {
            var op32 = new SHFILEOPSTRUCT32 { wFunc = 0x0003, pFrom = list, fFlags = flags }; // FO_DELETE
            return SHFileOperation32(ref op32) == 0 && !op32.fAnyOperationsAborted;
        }
        var op = new SHFILEOPSTRUCT { wFunc = 0x0003, pFrom = list, fFlags = flags };
        return SHFileOperation(ref op) == 0 && !op.fAnyOperationsAborted;
    }

    // ---------------------------------------------------------------- attention, focus

    [StructLayout(LayoutKind.Sequential)]
    private struct FLASHWINFO
    {
        public uint cbSize;
        public IntPtr hwnd;
        public uint dwFlags;
        public uint uCount;
        public uint dwTimeout;
    }

    [DllImport("user32.dll")]
    private static extern bool FlashWindowEx(ref FLASHWINFO info);

    /// <summary>Flashes the taskbar button until the window is activated.</summary>
    public static void Flash(IntPtr hwnd)
    {
        var info = new FLASHWINFO
        {
            cbSize = (uint)Marshal.SizeOf<FLASHWINFO>(),
            hwnd = hwnd,
            dwFlags = 0x00000002 | 0x0000000C, // FLASHW_TRAY | FLASHW_TIMERNOFG
            uCount = 0,
        };
        FlashWindowEx(ref info);
    }

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out int state);

    /// <summary>True while a full-screen app, game or presentation runs: Trove doesn't pop windows up then.</summary>
    public static bool IsUserBusy()
    {
        try
        {
            if (SHQueryUserNotificationState(out var state) != 0) return false;
            // QUNS_BUSY = 2, QUNS_RUNNING_D3D_FULL_SCREEN = 3, QUNS_PRESENTATION_MODE = 4
            return state is 2 or 3 or 4;
        }
        catch
        {
            return false;
        }
    }

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    public static extern int StrCmpLogicalW(string a, string b);
}
