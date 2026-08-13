using System.Windows.Interop;
using MySnip.App.Interop;
using MySnip.Core.Models;
using MySnip.Core.Services;

namespace MySnip.App.Services;

internal sealed class GlobalHotkeyService : IGlobalHotkeyService
{
    internal const string WindowTitle = "MySnip.MessageWindow";
    private readonly HwndSource _source;
    private readonly HashSet<int> _ids = [];

    public GlobalHotkeyService()
    {
        _source = new HwndSource(new HwndSourceParameters(WindowTitle)
        {
            Width = 0,
            Height = 0,
            WindowStyle = 0
        });
        _source.AddHook(WndProc);
    }

    public event EventHandler<int>? HotkeyPressed;
    public nint Handle => _source.Handle;

    public bool TryRegister(int id, HotkeyGesture gesture)
    {
        Unregister(id);
        var success = NativeMethods.RegisterHotKey(_source.Handle, id, (uint)gesture.Modifiers, gesture.VirtualKey);
        if (success) _ids.Add(id);
        return success;
    }

    public void Unregister(int id)
    {
        if (_ids.Remove(id)) NativeMethods.UnregisterHotKey(_source.Handle, id);
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == NativeMethods.WmHotkey)
        {
            handled = true;
            HotkeyPressed?.Invoke(this, wParam.ToInt32());
        }
        else if (msg == NativeMethods.WmAppActivate)
        {
            handled = true;
            _source.Dispatcher.BeginInvoke(() => HotkeyPressed?.Invoke(this, 0));
        }

        return 0;
    }

    public void Dispose()
    {
        foreach (var id in _ids.ToArray()) Unregister(id);
        _source.RemoveHook(WndProc);
        _source.Dispose();
    }
}
