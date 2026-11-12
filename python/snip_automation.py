"""Read visible control bounds from Windows UI Automation off the Tk thread."""

import base64
import os
from pathlib import Path
import subprocess


_SCRIPT = """
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName WindowsBase, UIAutomationTypes, UIAutomationClient
$cache = New-Object System.Windows.Automation.CacheRequest
$cache.AutomationElementMode = [System.Windows.Automation.AutomationElementMode]::None
$cache.Add([System.Windows.Automation.AutomationElement]::BoundingRectangleProperty)
$cache.Add([System.Windows.Automation.AutomationElement]::IsOffscreenProperty)
$condition = [System.Windows.Automation.Automation]::ControlViewCondition
$scope = [System.Windows.Automation.TreeScope]::Descendants
$watch = [Diagnostics.Stopwatch]::StartNew()
$total = 0
foreach ($handle in $env:MYSNIP_AUTOMATION_WINDOWS.Split(',')) {
    if ($watch.ElapsedMilliseconds -ge 1100 -or $total -ge 1200) { break }
    try {
        $root = [System.Windows.Automation.AutomationElement]::FromHandle([IntPtr][long]$handle)
        $request = $cache.Activate()
        try { $elements = $root.FindAll($scope, $condition) }
        finally { $request.Dispose() }
        $limit = [Math]::Min($elements.Count, 250)
        for ($i = 0; $i -lt $limit; $i++) {
            if ($watch.ElapsedMilliseconds -ge 1100 -or $total -ge 1200) { break }
            $total++
            try {
                $properties = $elements[$i].Cached
                $bounds = $properties.BoundingRectangle
                if (!$properties.IsOffscreen -and $bounds.Width -ge 6 -and $bounds.Height -ge 6) {
                    $left = [int]$bounds.Left
                    $top = [int]$bounds.Top
                    $right = [int]$bounds.Right
                    $bottom = [int]$bounds.Bottom
                    [Console]::WriteLine("$handle $left $top $right $bottom")
                }
            } catch { continue }
        }
    } catch { continue }
}
"""


def automation_bounds(handles):
    if not handles:
        return {}
    shell = Path(os.environ.get("SystemRoot", r"C:\Windows")) / "System32/WindowsPowerShell/v1.0/powershell.exe"
    encoded = base64.b64encode(_SCRIPT.encode("utf-16-le")).decode("ascii")
    try:
        result = subprocess.run(
            [str(shell), "-NoProfile", "-NonInteractive", "-EncodedCommand", encoded],
            env=dict(os.environ, MYSNIP_AUTOMATION_WINDOWS=",".join(map(str, handles))),
            stdout=subprocess.PIPE, stderr=subprocess.DEVNULL,
            creationflags=subprocess.CREATE_NO_WINDOW, timeout=3,
        )
        output = result.stdout
    except subprocess.TimeoutExpired as error:
        output = error.stdout or b""
    except OSError:
        return {}

    bounds = {}
    for line in output.splitlines():
        try:
            handle, left, top, right, bottom = map(int, line.split())
        except ValueError:
            continue
        bounds.setdefault(handle, []).append((left, top, right, bottom))
    return bounds
