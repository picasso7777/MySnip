using System.Runtime.InteropServices;

namespace MySnip.App.Interop;

internal static partial class DesktopInput
{
    internal const int MouseHook = 14;
    internal const int MouseMove = 0x0200;
    internal const int LeftDown = 0x0201;
    internal const int LeftUp = 0x0202;
    internal const int RightDown = 0x0204;
    internal const int Injected = 1;
    internal const int KeyEscape = 0x1B;
    internal const int KeyControl = 0x11;
    internal const int KeyShift = 0x10;
    internal const int KeyAlt = 0x12;
    internal const int KeyWinLeft = 0x5B;
    internal const int KeyWinRight = 0x5C;
    internal const int KeyF24 = 0x87;
    private const uint KeyUp = 2;
    private const uint Keyboard = 1;

    internal delegate nint MouseCallback(int code, nint message, nint data);
    internal delegate void WinEventCallback(nint hook, uint @event, nint window, int objectId, int childId, uint thread, uint time);

    [DllImport("user32.dll", EntryPoint = "SetWindowsHookExW", SetLastError = true)]
    internal static extern nint SetWindowsHookEx(int type, MouseCallback callback, nint module, uint thread);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool UnhookWindowsHookEx(nint hook);

    [LibraryImport("user32.dll")]
    internal static partial nint CallNextHookEx(nint hook, int code, nint message, nint data);

    [DllImport("user32.dll")]
    internal static extern nint SetWinEventHook(uint first, uint last, nint module, WinEventCallback callback,
        uint process, uint thread, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool UnhookWinEvent(nint hook);

    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial nint GetModuleHandle(string? module);

    [LibraryImport("user32.dll")]
    private static partial short GetAsyncKeyState(int key);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static unsafe partial uint SendInput(uint count, Input* inputs, int size);

    internal static bool Down(int key) => (GetAsyncKeyState(key) & 0x8000) != 0;
    internal static bool WindowsKeyDown => Down(KeyWinLeft) || Down(KeyWinRight);

    internal static bool SendKey(ushort key) => Send([Keystroke(key), Keystroke(key, KeyUp)]);

    internal static bool Paste() => Send([Keystroke(KeyControl), Keystroke('V'), Keystroke('V', KeyUp), Keystroke(KeyControl, KeyUp)]);

    private static Input Keystroke(ushort key, uint flags = 0) =>
        new() { Type = Keyboard, Payload = new InputPayload { Keyboard = new KeyboardInput { Key = key, Flags = flags } } };

    private static unsafe bool Send(ReadOnlySpan<Input> inputs)
    {
        fixed (Input* pointer = inputs) return SendInput((uint)inputs.Length, pointer, sizeof(Input)) == inputs.Length;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MouseEvent
    {
        internal NativeMethods.Point Position;
        internal uint MouseData, Flags, Time;
        internal nuint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Input { public uint Type; public InputPayload Payload; }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputPayload
    {
        [FieldOffset(0)] public KeyboardInput Keyboard;
        [FieldOffset(0)] public MouseEvent Mouse;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort Key, Scan;
        public uint Flags, Time;
        public nuint ExtraInfo;
    }
}
