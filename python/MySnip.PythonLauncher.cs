using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

internal static class PythonLauncher
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            var root = PrepareRuntime(assembly);
            var runtime = Path.Combine(root, "runtime");
            var python = Path.Combine(runtime, "pythonw.exe");
            var script = Path.Combine(root, "app", "mysnip.py");
            var start = new ProcessStartInfo(python)
            {
                Arguments = "-B " + string.Join(" ", new[] { script }.Concat(args).Select(QuoteArgument)),
                UseShellExecute = false,
                CreateNoWindow = true
            };
            var systemPath = Environment.GetEnvironmentVariable("PATH") ?? "";
            start.EnvironmentVariables["PATH"] = runtime + ";" + Path.Combine(runtime, "DLLs") + ";" + systemPath;
            start.EnvironmentVariables["MYSNIP_SYSTEM_PATH"] = systemPath;
            var tcl = Path.Combine(runtime, "tcl");
            start.EnvironmentVariables["TCL_LIBRARY"] = FindTclLibrary(tcl, "init.tcl");
            start.EnvironmentVariables["TK_LIBRARY"] = FindTclLibrary(tcl, "tk.tcl");
            start.EnvironmentVariables["MYSNIP_SINGLE_FILE_EXE"] = assembly.Location;
            using (var process = Process.Start(start))
            {
                if (process == null) throw new InvalidOperationException("Could not launch the bundled Python runtime.");
                process.WaitForExit();
                if (process.ExitCode != 0)
                    throw new InvalidOperationException("The bundled Python app exited with code " + process.ExitCode + ".");
                return process.ExitCode;
            }
        }
        catch (Exception error)
        {
            MessageBox.Show("Could not start MySnip Python."
                + Environment.NewLine + error.Message, "MySnip Python", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }

    private static string FindTclLibrary(string directory, string script)
    {
        var path = Directory.GetDirectories(directory)
            .First(folder => File.Exists(Path.Combine(folder, script))).Replace('\\', '/');
        // The extended path preserves hidden or redirected profile directory components.
        if (path.StartsWith("//?/", StringComparison.Ordinal)) return path;
        return path.StartsWith("//", StringComparison.Ordinal) ? "//?/UNC/" + path.Substring(2) : "//?/" + path;
    }

    private static string PrepareRuntime(Assembly assembly)
    {
        var local = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        if (string.IsNullOrWhiteSpace(local))
            local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var cache = Path.Combine(local, "MySnip", "python-runtime");
        Directory.CreateDirectory(cache);

        using (var payload = assembly.GetManifestResourceStream("MySnip.Python.Payload"))
        using (var sha = SHA256.Create())
        {
            if (payload == null) throw new InvalidDataException("The executable is missing its bundled runtime.");
            var version = BitConverter.ToString(sha.ComputeHash(payload)).Replace("-", "").ToLowerInvariant();
            var target = Path.Combine(cache, version);
            if (IsReady(target)) return target;

            // The first launch creates the cache before later invocations can use it.
            using (var mutex = new Mutex(false, @"Local\MySnip.Python.Runtime." + version))
            {
                try { mutex.WaitOne(); }
                catch (AbandonedMutexException) { }
                try
                {
                    if (!IsReady(target))
                    {
                        payload.Position = 0;
                        ExtractRuntime(payload, cache, target);
                    }
                    return target;
                }
                finally { mutex.ReleaseMutex(); }
            }
        }
    }

    private static bool IsReady(string path)
    {
        return File.Exists(Path.Combine(path, "runtime", "pythonw.exe")) &&
            File.Exists(Path.Combine(path, "app", "mysnip.py")) && File.Exists(Path.Combine(path, ".ready"));
    }

    private static void ExtractRuntime(Stream payload, string cache, string target)
    {
        var stage = Path.Combine(cache, "extract-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        try
        {
            var prefix = Path.GetFullPath(stage) + Path.DirectorySeparatorChar;
            using (var archive = new ZipArchive(payload, ZipArchiveMode.Read, true))
            {
                foreach (var entry in archive.Entries)
                {
                    var destination = Path.GetFullPath(Path.Combine(stage, entry.FullName));
                    if (!destination.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("Invalid runtime archive path.");
                    Directory.CreateDirectory(Path.GetDirectoryName(destination));
                    using (var content = entry.Open())
                    using (var file = new FileStream(destination, FileMode.CreateNew, FileAccess.Write))
                        content.CopyTo(file);
                }
            }
            File.WriteAllText(Path.Combine(stage, ".ready"), "");
            if (Directory.Exists(target) && !IsReady(target))
                Directory.Move(target, Path.Combine(cache, "incomplete-" + Guid.NewGuid().ToString("N")));
            try { Directory.Move(stage, target); }
            catch (IOException) { if (!IsReady(target)) throw; }
        }
        finally
        {
            if (Directory.Exists(stage))
            {
                try { Directory.Delete(stage, true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    private static string QuoteArgument(string value)
    {
        var escaped = Regex.Replace(value, @"(\\*)""", "$1$1\\\"");
        return "\"" + Regex.Replace(escaped, @"(\\+)$", "$1$1") + "\"";
    }
}
