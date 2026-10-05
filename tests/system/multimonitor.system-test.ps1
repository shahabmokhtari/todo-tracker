# Multi-monitor placement test against the real Todo Tracker sidebar on the live Windows shell.
# For every display: Menu > Position > Display N, then Dock right / Dock left / Float. Checks the window lands on that
# monitor, only that monitor's work area changes, and every work area is restored on exit.
# Skips (exit 0) on machines with a single monitor, such as CI runners.
param([string]$Exe, [string]$DataDir = (Join-Path $env:TEMP ("tt-system-" + [guid]::NewGuid().ToString('N'))), [int]$Port = 5403)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Windows.Forms, System.Drawing
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class Native {
  [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr h, int i);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool SystemParametersInfo(int action, int param, out RECT r, int winIni);
  [DllImport("user32.dll")] public static extern int GetSystemMetrics(int index);
  [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr value);
  [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
  [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, int flags);
  public struct RECT { public int L, T, R, B; }
}
"@
$results = [System.Collections.Generic.List[string]]::new()
function Check($cond, $what) { if (-not $cond) { throw "FAILED: $what" }; $results.Add("PASS  $what"); Write-Host "PASS  $what" }
# Live, physical-pixel values (System.Windows.Forms.Screen caches and is DPI-virtualized in a DPI-unaware host).
[void][Native]::SetProcessDpiAwarenessContext([IntPtr]-4)
function WorkArea { $r = New-Object Native+RECT; [void][Native]::SystemParametersInfo(0x30, 0, [ref]$r, 0); [System.Drawing.Rectangle]::FromLTRB($r.L, $r.T, $r.R, $r.B) }
function Bounds { [System.Drawing.Rectangle]::new(0, 0, [Native]::GetSystemMetrics(0), [Native]::GetSystemMetrics(1)) }
function Root { [System.Windows.Automation.AutomationElement]::RootElement }
function FindByName($scope, $name, $type) {
  $cond = New-Object System.Windows.Automation.AndCondition(
    (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $name)),
    (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, $type)))
  for ($i = 0; $i -lt 50; $i++) { $e = $scope.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond); if ($e) { return $e }; Start-Sleep -Milliseconds 100 }
  throw "UI element not found: $name"
}
function Invoke($e) { $e.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
# The shell updates the work area before the app finishes moving its window, so state checks poll briefly.
function Eventually($cond, $what) { for ($i = 0; $i -lt 50 -and -not (& $cond); $i++) { Start-Sleep -Milliseconds 100 }; Check (& $cond) $what }
function Wait($cond, $what) { for ($i = 0; $i -lt 150; $i++) { if (& $cond) { return }; Start-Sleep -Milliseconds 100 }; throw "Timed out waiting for: $what" }
function MainWindow($proc) {
  # Find the sidebar by process + automation name; Process.MainWindowHandle can briefly point at a WPF helper window.
  $cond = New-Object System.Windows.Automation.AndCondition(
    (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $proc.Id)),
    (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, 'Todo Tracker sidebar')))
  for ($i = 0; $i -lt 100; $i++) {
    $w = (Root).FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
    if ($w -and $w.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, 'Menu')))) {
      $script:hwnd = [IntPtr]$w.Current.NativeWindowHandle
      return $w
    }
    Start-Sleep -Milliseconds 200
  }
  throw 'Sidebar window did not appear'
}
function TryFind($scope, $name, $type, $tries) {
  $cond = New-Object System.Windows.Automation.AndCondition(
    (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $name)),
    (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, $type)))
  for ($i = 0; $i -lt $tries; $i++) { $e = $scope.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond); if ($e) { return $e }; Start-Sleep -Milliseconds 100 }
  $null
}
function OpenMenuItem($win, $name) {
  # A click can land while the window is still moving (right after a re-dock); retry opening the menu.
  for ($attempt = 0; $attempt -lt 3; $attempt++) {
    Invoke (FindByName $win 'Menu' ([System.Windows.Automation.ControlType]::Button))
    $item = TryFind (Root) $name ([System.Windows.Automation.ControlType]::MenuItem) 20
    if ($item) { return $item }
    [System.Windows.Forms.SendKeys]::SendWait('{ESC}'); Start-Sleep -Milliseconds 500
  }
  throw "UI element not found: $name"
}
function ChoosePosition($win, $option) {
  $position = OpenMenuItem $win 'Position'
  $position.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
  Invoke (FindByName (Root) $option ([System.Windows.Automation.ControlType]::MenuItem))
}
function ToggleAlwaysOnTop($win) {
  $item = OpenMenuItem $win 'Always on top'
  $item.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()
  [System.Windows.Forms.SendKeys]::SendWait('{ESC}')
}
function IsTopmost($hwnd) { ([Native]::GetWindowLong($hwnd, -20) -band 0x8) -ne 0 }
function Rect($hwnd) { $r = New-Object Native+RECT; [void][Native]::GetWindowRect($hwnd, [ref]$r); $r }

Add-Type @"
using System; using System.Runtime.InteropServices; using System.Collections.Generic;
public struct R4 { public int L, T, R, B; }
public static class Mon2 {
  public delegate bool P(IntPtr h, IntPtr dc, ref R4 r, IntPtr d);
  [DllImport("user32.dll")] public static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, P cb, IntPtr d);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern bool GetMonitorInfo(IntPtr h, ref MI mi);
  [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)] public struct MI { public int cb; public R4 m; public R4 w; public int f; [MarshalAs(UnmanagedType.ByValTStr, SizeConst=32)] public string n; }
  public static List<MI> All() { var l = new List<MI>(); EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr h, IntPtr dc, ref R4 r, IntPtr d) => { var mi = new MI(); mi.cb = Marshal.SizeOf(mi); GetMonitorInfo(h, ref mi); l.Add(mi); return true; }, IntPtr.Zero); return l; }
}
"@
function Mons { [Mon2]::All() }
function MonByName($n) { Mons | ? { $_.n -eq $n } }
$initial = Mons
if ($initial.Count -lt 2) { Write-Host 'SKIPPED: needs at least two monitors'; exit 0 }
$proc = Start-Process $Exe -ArgumentList '--data', $DataDir, '--port', $Port -PassThru
try {
  $win = MainWindow $proc
  Start-Sleep 2
  for ($i = 0; $i -lt $initial.Count; $i++) {
    $m = $initial[$i]; $label = "Display $($i + 1)"
    Write-Host "--- $label $($m.n) bounds=$($m.m.L),$($m.m.T),$($m.m.R),$($m.m.B)"
    # Choose the display (prefix match on the menu item name).
    $position = OpenMenuItem $win 'Position'
    $position.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand(); Start-Sleep -Milliseconds 300
    $items = (Root).FindAll([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::MenuItem)))
    $item = $items | ? { $_.Current.Name -like "$label*" } | Select-Object -First 1
    if (-not $item) { throw "menu item $label not found: $(($items | % { $_.Current.Name }) -join ' | ')" }
    Invoke $item
    foreach ($edge in 'Dock right', 'Dock left') {
      ChoosePosition $win $edge
      Eventually { $r = Rect $hwnd; $mm = MonByName $m.n; $r.T -ge $mm.m.T -and $r.B -le $mm.m.B -and (($edge -eq 'Dock right' -and [Math]::Abs($r.R - $mm.m.R) -le 2) -or ($edge -eq 'Dock left' -and [Math]::Abs($r.L - $mm.m.L) -le 2)) } "$label ${edge}: window on that monitor's edge"
      Eventually { $mm = MonByName $m.n; ($edge -eq 'Dock right' -and $mm.w.R -lt $m.w.R) -or ($edge -eq 'Dock left' -and $mm.w.L -gt $m.w.L) } "$label ${edge}: that monitor's work area shrinks"
      foreach ($o in $initial | ? { $_.n -ne $m.n }) { $oo = MonByName $o.n; Check ($oo.w.L -eq $o.w.L -and $oo.w.R -eq $o.w.R) "$label ${edge}: other monitor $($o.n) untouched" }
    }
    # Saved by stable device path (survives reconnects), and a display change keeps the sidebar on this monitor.
    Start-Sleep -Milliseconds 300
    $saved = (Get-Content (Join-Path $DataDir 'desktop.json') -Raw | ConvertFrom-Json).monitor
    Check ($saved -like '\\?\*') "$label saved by stable device path ($saved)"
    [void][Native]::PostMessage($hwnd, 0x007E, [IntPtr]32, [IntPtr]0)  # WM_DISPLAYCHANGE
    Start-Sleep -Milliseconds 1200
    Eventually { $r = Rect $hwnd; $mm = MonByName $m.n; [Math]::Abs($r.L - $mm.m.L) -le 2 -and $r.T -ge $mm.m.T -and $r.B -le $mm.m.B } "$label display change: still docked on this monitor"
    ChoosePosition $win 'Float as a window'
    Eventually { $mm = MonByName $m.n; $mm.w.L -eq $m.w.L -and $mm.w.R -eq $m.w.R } "$label float: work area restored"
    Eventually { $r = Rect $hwnd; $cx = ($r.L + $r.R) / 2; $cy = ($r.T + $r.B) / 2; $cx -ge $m.m.L -and $cx -le $m.m.R -and $cy -ge $m.m.T -and $cy -le $m.m.B } "$label float: window centered on that monitor"
  }
  ChoosePosition $win 'Dock right'
  Start-Sleep 1
  $win.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close()
  [void]$proc.WaitForExit(15000)
  Start-Sleep 1
  foreach ($o in $initial) { $oo = MonByName $o.n; Check ($oo.w.L -eq $o.w.L -and $oo.w.R -eq $o.w.R -and $oo.w.T -eq $o.w.T -and $oo.w.B -eq $o.w.B) "after exit: $($o.n) work area restored" }
  Write-Host "`nALL $($results.Count) MULTI-MONITOR CHECKS PASSED"
}
finally {
  if (-not $proc.HasExited) { [void]$proc.CloseMainWindow(); if (-not $proc.WaitForExit(10000)) { $proc.Kill() } }
  Remove-Item $DataDir -Recurse -Force -ErrorAction SilentlyContinue
}


