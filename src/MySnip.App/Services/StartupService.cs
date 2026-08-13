using Microsoft.Win32;
using MySnip.Core.Services;

namespace MySnip.App.Services;

internal sealed class StartupService : IStartupService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "MySnip";

    public bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string;
        }
    }

    public void SetEnabled(bool enabled, string executablePath)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue(ValueName, $"\"{executablePath}\"");
        else key.DeleteValue(ValueName, false);
    }
}
