using MySnip.Core.Models;

namespace MySnip.Core.Services;

public interface IScreenCaptureService
{
    CapturedFrame CaptureVirtualDesktop();
    IReadOnlyList<ScreenDescriptor> GetScreens();
    IReadOnlyList<CaptureCandidate> GetCaptureCandidates();
}

public interface IGlobalHotkeyService : IDisposable
{
    event EventHandler<int>? HotkeyPressed;
    bool TryRegister(int id, HotkeyGesture gesture);
    void Unregister(int id);
}

public interface IClipboardService
{
    byte[]? TryGetPng();
    void SetPng(byte[] pngBytes);
}

public interface ISettingsStore
{
    string SettingsPath { get; }
    AppSettings Load();
    void Save(AppSettings settings);
}

public interface IStartupService
{
    bool IsEnabled { get; }
    void SetEnabled(bool enabled, string executablePath);
}
