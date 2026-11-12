using System.Windows.Threading;
using MySnip.App.Interop;
using MySnip.Core.Models;

namespace MySnip.App.Services;

internal sealed class HotCornerService : IDisposable
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private HotCornerSettings _settings = new();
    private (int X, int Y, HotCornerAction Action)? _candidate;
    private long _started;
    private bool _triggered;

    public HotCornerService() => _timer.Tick += (_, _) => Update();

    public event Action<HotCornerAction>? Triggered;

    public void Configure(HotCornerSettings settings)
    {
        _settings = settings;
        _candidate = null;
        if (_settings == new HotCornerSettings()) _timer.Stop(); else _timer.Start();
    }

    private void Update()
    {
        if (!NativeMethods.GetCursorPos(out var cursor)) return;
        var displays = ScreenCaptureService.GetDisplayBounds();
        var screen = DesktopGeometry.DisplayAt(displays, new PixelPoint(cursor.X, cursor.Y), default);
        if (screen.IsEmpty) return;
        var left = cursor.X <= screen.Left + 1;
        var right = cursor.X >= screen.Right - 2;
        var top = cursor.Y <= screen.Top + 1;
        var bottom = cursor.Y >= screen.Bottom - 2;
        var action = (left, right, top, bottom) switch
        {
            (true, _, true, _) => _settings.TopLeft,
            (_, true, true, _) => _settings.TopRight,
            (true, _, _, true) => _settings.BottomLeft,
            (_, true, _, true) => _settings.BottomRight,
            _ => HotCornerAction.Disabled
        };
        if (action == HotCornerAction.Disabled) { _candidate = null; return; }
        var candidate = (left ? screen.Left : screen.Right, top ? screen.Top : screen.Bottom, action);
        var now = Environment.TickCount64;
        if (_candidate != candidate)
        {
            _candidate = candidate;
            _started = now;
            _triggered = false;
        }
        else if (!_triggered && now - _started >= 350)
        {
            _triggered = true;
            Triggered?.Invoke(action);
        }
    }

    public void Dispose() => _timer.Stop();
}
