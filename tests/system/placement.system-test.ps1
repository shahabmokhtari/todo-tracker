# End-to-end placement test against the real Todo Tracker sidebar on the live Windows shell.
# Drives the UI like a user (UI Automation): Menu > Position > Dock left / Float, Always on top,
# then restarts the app to verify the placement is remembered.
param([string]$Exe, [string]$DataDir = (Join-Path $env:TEMP ("tt-system-" + [guid]::NewGuid().ToString('N'))), [int]$Port = 5402)
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

$original = WorkArea; $screen = Bounds
Write-Host "Primary screen $screen, work area $original"
# Never sync into this computer's real OneDrive or iCloud Drive.
$env:TODOTRACKER_CLOUD = 'off'
$proc = Start-Process $Exe -ArgumentList '--data', $DataDir, '--port', $Port -PassThru
try {
  $win = MainWindow $proc

  # 1. Default: docked right, reserving screen space.
  Wait { (WorkArea).Right -lt $original.Right } 'work area to shrink on the right'
  $r = Rect $hwnd
  Check ((WorkArea).Right -lt $original.Right) "default docks right and reserves space (work area right $($original.Right) -> $((WorkArea).Right))"
  Check ([Math]::Abs($r.R - $screen.Right) -le 2) 'window hugs the right edge'
  Check (IsTopmost $hwnd) 'docked sidebar is topmost'

  # 2. Menu > Position > Dock left.
  ChoosePosition $win 'Dock left'
  Wait { (WorkArea).Left -gt $original.Left -and (WorkArea).Right -eq $original.Right } 'work area to move to the left'
  $r = Rect $hwnd
  Check ((WorkArea).Left -gt $original.Left -and (WorkArea).Right -eq $original.Right) "dock left reserves the left edge (work area left -> $((WorkArea).Left))"
  Eventually { $r = Rect $hwnd; [Math]::Abs($r.L - $screen.Left) -le 2 } 'window hugs the left edge'

  # 3. Menu > Position > Float as a window: space is given back; window can move and resize; not topmost.
  ChoosePosition $win 'Float as a window'
  Wait { (WorkArea).Equals($original) } 'work area restored'
  Check ((WorkArea).Equals($original)) 'floating gives the screen edge back'
  Eventually { -not (IsTopmost $hwnd) } 'floating window is not topmost by default'
  $transform = $win.GetCurrentPattern([System.Windows.Automation.TransformPattern]::Pattern)
  Check ($transform.Current.CanMove -and $transform.Current.CanResize) 'floating window can be moved and resized'
  # Target fits any work area (CI runners are 1024x768), so on-screen clamping never kicks in.
  $X = $original.Left + 100; $Y = $original.Top + 100; $W = 500; $H = [Math]::Min(700, $original.Height - 200)
  $transform.Move($X, $Y); $transform.Resize($W, $H)
  Start-Sleep -Milliseconds 300
  $r = Rect $hwnd
  Check ($r.L -eq $X -and $r.T -eq $Y -and ($r.R - $r.L) -eq $W -and ($r.B - $r.T) -eq $H) "moved/resized to $X,$Y ${W}x$H (got $($r.L),$($r.T) $($r.R - $r.L)x$($r.B - $r.T))"

  # 4. Always on top (only meaningful while floating). Toggled right after the move: the window must not jump.
  ToggleAlwaysOnTop $win
  Eventually { $r = Rect $hwnd; $r.L -eq $X -and $r.T -eq $Y } 'toggling always on top keeps the window where it is'
  Wait { IsTopmost $hwnd } 'topmost'
  Check (IsTopmost $hwnd) 'Always on top makes the floating window topmost'
  Start-Sleep -Milliseconds 900  # placement save is debounced

  # 5. Restart: placement is remembered.
  $win.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close()
  Check ($proc.WaitForExit(15000)) 'app exits cleanly'
  Check ((WorkArea).Equals($original)) 'no screen space left reserved after exit'
  $json = Get-Content (Join-Path $DataDir 'desktop.json') -Raw | ConvertFrom-Json
  Check ($json.mode -eq 'floating' -and $json.alwaysOnTop -eq $true) "desktop.json remembers floating + always on top"

  $proc = Start-Process $Exe -ArgumentList '--data', $DataDir, '--port', $Port -PassThru
  $win = MainWindow $proc
  Start-Sleep -Milliseconds 800
  $r = Rect $hwnd
  Check ((WorkArea).Equals($original)) 'restarted floating: no screen space reserved'
  Check ([Math]::Abs($r.L - $X) -le 2 -and [Math]::Abs($r.T - $Y) -le 2 -and [Math]::Abs(($r.R - $r.L) - $W) -le 2) "restarted at remembered bounds ($($r.L),$($r.T) $($r.R - $r.L)x$($r.B - $r.T))"
  Eventually { IsTopmost $hwnd } 'restarted with always on top'

  # 5b. Monitor unplugged while floating: a display change pulls an off-screen window back onto a monitor.
  [void][Native]::SetWindowPos($hwnd, [IntPtr]::Zero, 30000, 30000, 0, 0, 0x0001 -bor 0x0004 -bor 0x0010)
  Eventually { (Rect $hwnd).L -ge 30000 } 'window pushed off-screen (simulated unplug)'
  [void][Native]::PostMessage($hwnd, 0x007E, [IntPtr]32, [IntPtr]0)  # WM_DISPLAYCHANGE
  Eventually { $r = Rect $hwnd; $r.L -ge $original.Left -and $r.R -le $original.Right -and $r.T -ge $original.Top -and $r.B -le $original.Bottom } 'display change brings the floating window back on screen'

  # 6. Back to docked right, then exit: work area must be restored.
  ChoosePosition $win 'Dock right'
  Wait { (WorkArea).Right -lt $original.Right } 'docked right again'
  Check ((WorkArea).Right -lt $original.Right) 'dock right again from floating'
  $win.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close()
  [void]$proc.WaitForExit(15000)
  Wait { (WorkArea).Equals($original) } 'work area restored after exit'
  Check ((WorkArea).Equals($original)) 'exit while docked releases the screen edge'
  Write-Host "`nALL $($results.Count) SYSTEM CHECKS PASSED"
}
finally {
  # Close gracefully so a docked instance releases its screen edge; kill only as a last resort.
  if (-not $proc.HasExited) { [void]$proc.CloseMainWindow(); if (-not $proc.WaitForExit(10000)) { $proc.Kill() } }
  Remove-Item $DataDir -Recurse -Force -ErrorAction SilentlyContinue
}
