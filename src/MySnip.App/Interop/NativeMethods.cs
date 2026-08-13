using System.Runtime.InteropServices;
using System.Text;

namespace MySnip.App.Interop;

internal static partial class NativeMethods
{
    internal const int WmHotkey = 0x0312;
    internal const int WmAppActivate = 0x8001;
    internal const int GwlExStyle = -20;
    internal const int WsExTransparent = 0x20;
    internal const int WsExLayered = 0x80000;
    internal const uint SwpNoMove = 0x0002;
    internal const uint SwpNoSize = 0x0001;
    internal const uint SwpNoZOrder = 0x0004;
    internal const uint SwpFrameChanged = 0x0020;
    internal const int SmXVirtualScreen = 76;
    internal const int SmYVirtualScreen = 77;
    internal const int SmCxVirtualScreen = 78;
    internal const int SmCyVirtualScreen = 79;
    internal const int DwmwaExtendedFrameBounds = 9;
    internal const int DwmwaCloaked = 14;
    internal const uint MonitorDefaultToNearest = 2;
    internal const int WmDpiChanged = 0x02E0;
    internal const int WmDisplayChange = 0x007E;
    internal const uint SwpNoActivate = 0x0010;
    internal static readonly nint HwndTopmost = new(-1);
    internal delegate bool EnumWindowsProc(nint hwnd, nint lParam);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool RegisterHotKey(nint hWnd, int id, uint fsModifiers, uint vk);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool UnregisterHotKey(nint hWnd, int id);

    [LibraryImport("user32.dll")]
    internal static partial int GetSystemMetrics(int nIndex);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetCursorPos(out Point point);

    [LibraryImport("user32.dll")]
    internal static partial nint WindowFromPoint(Point point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumWindows(EnumWindowsProc callback, nint lParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsWindowVisible(nint hwnd);

    [LibraryImport("user32.dll")]
    internal static partial uint GetWindowThreadProcessId(nint hwnd, out uint processId);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsIconic(nint hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int GetClassName(nint hwnd, StringBuilder className, int maximumCount);

    [LibraryImport("user32.dll")]
    internal static partial nint GetAncestor(nint hwnd, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetWindowRect(nint hwnd, out Rect rect);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    internal static partial nint GetWindowLongPtr(nint hwnd, int index);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    internal static partial nint SetWindowLongPtr(nint hwnd, int index, nint value);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int cx, int cy, uint flags);

    [LibraryImport("dwmapi.dll")]
    internal static partial int DwmGetWindowAttribute(nint hwnd, int attribute, out Rect rect, int size);

    [LibraryImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
    internal static partial int DwmGetWindowAttributeInt(nint hwnd, int attribute, out int value, int size);

    [LibraryImport("user32.dll")]
    internal static partial nint MonitorFromPoint(Point point, uint flags);

    [LibraryImport("shcore.dll")]
    internal static partial int GetDpiForMonitor(nint monitor, int dpiType, out uint dpiX, out uint dpiY);

    [LibraryImport("user32.dll")]
    internal static partial uint GetDpiForWindow(nint hwnd);

    [StructLayout(LayoutKind.Sequential)]
    internal struct Point { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect { public int Left; public int Top; public int Right; public int Bottom; }
}
