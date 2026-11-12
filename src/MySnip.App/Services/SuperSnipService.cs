using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using MySnip.App.Interop;
using MySnip.Core.Models;

namespace MySnip.App.Services;

internal sealed class SuperSnipService : IDisposable
{
    private readonly Dispatcher _ui = Dispatcher.CurrentDispatcher;
    private readonly DispatcherTimer _previewTimer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private readonly Func<bool> _available;
    private readonly DesktopInput.MouseCallback _callback;
    private readonly object _lifecycle = new();
    private Dispatcher? _hookDispatcher;
    private Thread? _thread;
    private volatile bool _enabled;
    private volatile bool _disposed;
    private bool _dragging;
    private bool _canceled;
    private PixelPoint _start;
    private long _position;
    private PixelPoint? _previewStart;
    private Window? _preview;
    private nint _previewHandle;
    private bool _previewVisible;

    public SuperSnipService(Func<bool> available)
    {
        _available = available;
        _callback = HandleMouse;
        _previewTimer.Tick += (_, _) => DrawPreview();
    }

    public event Action<PixelRect>? Completed;
    public event Action<string>? Failed;

    public void Configure(bool enabled)
    {
        _enabled = enabled;
        if (!enabled) { StopPreview(); return; }
        lock (_lifecycle)
        {
            if (_disposed || _thread is not null) return;
            _thread = new Thread(RunHook) { IsBackground = true, Name = "MySnip Super-snip input" };
            _thread.Start();
        }
    }

    private void RunHook()
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        lock (_lifecycle)
        {
            if (_disposed) return;
            _hookDispatcher = dispatcher;
        }
        var hook = DesktopInput.SetWindowsHookEx(DesktopInput.MouseHook, _callback, DesktopInput.GetModuleHandle(null), 0);
        try
        {
            if (hook == 0)
            {
                var message = new Win32Exception(Marshal.GetLastWin32Error()).Message;
                _ui.BeginInvoke(() => Failed?.Invoke(message));
                return;
            }
            Dispatcher.Run();
        }
        finally
        {
            if (hook != 0) DesktopInput.UnhookWindowsHookEx(hook);
            lock (_lifecycle) { _hookDispatcher = null; _thread = null; }
        }
    }

    private nint HandleMouse(int code, nint message, nint data)
    {
        if (code < 0 || !_enabled && !_dragging) return DesktopInput.CallNextHookEx(0, code, message, data);
        var kind = (int)message;
        if (kind is not (DesktopInput.LeftDown or DesktopInput.LeftUp or DesktopInput.MouseMove or DesktopInput.RightDown))
            return DesktopInput.CallNextHookEx(0, code, message, data);
        if (!_dragging && kind != DesktopInput.LeftDown) return DesktopInput.CallNextHookEx(0, code, message, data);
        var input = Marshal.PtrToStructure<DesktopInput.MouseEvent>(data);
        if ((input.Flags & DesktopInput.Injected) != 0) return DesktopInput.CallNextHookEx(0, code, message, data);
        var point = new PixelPoint(input.Position.X, input.Position.Y);

        if (!_dragging && _enabled && _available() && DesktopInput.WindowsKeyDown &&
            !DesktopInput.Down(DesktopInput.KeyAlt) && !DesktopInput.Down(DesktopInput.KeyControl) && !DesktopInput.Down(DesktopInput.KeyShift))
        {
            _dragging = true;
            _canceled = false;
            _start = point;
            WritePoint(point);
            DesktopInput.SendKey(DesktopInput.KeyF24);
            _ui.BeginInvoke(() => StartPreview(point), DispatcherPriority.Input);
            return 1;
        }
        if (!_dragging) return DesktopInput.CallNextHookEx(0, code, message, data);
        if (kind == DesktopInput.RightDown || DesktopInput.Down(DesktopInput.KeyEscape)) _canceled = true;
        WritePoint(point);
        if (kind == DesktopInput.LeftUp)
        {
            var region = PixelRect.FromPoints(_start, point);
            var accept = _enabled && !_canceled && region.Width >= 4 && region.Height >= 4;
            _dragging = false;
            _ui.BeginInvoke(() =>
            {
                StopPreview();
                if (accept && _enabled) Completed?.Invoke(region);
            }, DispatcherPriority.Input);
            return 1;
        }
        return kind == DesktopInput.MouseMove ? DesktopInput.CallNextHookEx(0, code, message, data) : 1;
    }

    private void WritePoint(PixelPoint point) =>
        Interlocked.Exchange(ref _position, ((long)point.X << 32) | (uint)point.Y);

    private void StartPreview(PixelPoint point)
    {
        if (!_enabled) return;
        _previewStart = point;
        _previewTimer.Start();
    }

    private void DrawPreview()
    {
        if (_previewStart is not { } start) return;
        if (DesktopInput.Down(DesktopInput.KeyEscape)) { StopPreview(); return; }
        var value = Interlocked.Read(ref _position);
        var bounds = PixelRect.FromPoints(start, new PixelPoint((int)(value >> 32), unchecked((int)value)));
        if (bounds.IsEmpty) return;
        if (_preview is null)
        {
            _preview = new Window
            {
                WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, AllowsTransparency = true,
                Background = Brushes.Transparent, ShowActivated = false, ShowInTaskbar = false, Topmost = true,
                Content = new Border { BorderBrush = Brushes.DeepSkyBlue, BorderThickness = new Thickness(2) }
            };
            _previewHandle = new WindowInteropHelper(_preview).EnsureHandle();
            var style = NativeMethods.GetWindowLongPtr(_previewHandle, NativeMethods.GwlExStyle).ToInt64();
            NativeMethods.SetWindowLongPtr(_previewHandle, NativeMethods.GwlExStyle,
                new nint(style | NativeMethods.WsExTransparent));
        }
        if (!_previewVisible) { _preview.Show(); _previewVisible = true; }
        NativeMethods.SetWindowPos(_previewHandle, NativeMethods.HwndTopmost, bounds.X, bounds.Y, bounds.Width, bounds.Height,
            NativeMethods.SwpNoActivate | NativeMethods.SwpNoSendChanging);
    }

    private void StopPreview()
    {
        _previewStart = null;
        _previewTimer.Stop();
        if (_previewVisible) { _preview?.Hide(); _previewVisible = false; }
    }

    public void Dispose()
    {
        _enabled = false;
        _disposed = true;
        StopPreview();
        _preview?.Close();
        _preview = null;
        lock (_lifecycle) _hookDispatcher?.BeginInvokeShutdown(DispatcherPriority.Send);
    }
}
