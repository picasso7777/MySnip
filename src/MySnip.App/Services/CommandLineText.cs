using System.Runtime.InteropServices;

namespace MySnip.App.Services;

internal static partial class CommandLineText
{
    public static string[] Split(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) return [];
        var buffer = CommandLineToArgv("mysnip " + command, out var count);
        if (buffer == 0) return [];
        try
        {
            var args = new string[count - 1];
            for (var i = 1; i < count; i++)
                args[i - 1] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(buffer, i * nint.Size)) ?? "";
            return args;
        }
        finally { LocalFree(buffer); }
    }

    [LibraryImport("shell32.dll", EntryPoint = "CommandLineToArgvW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CommandLineToArgv(string commandLine, out int count);

    [LibraryImport("kernel32.dll")]
    private static partial nint LocalFree(nint memory);
}
