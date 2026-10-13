# End-to-end test of the docked sidebar next to full-screen windows (Remote Desktop, videos) and of hiding it, on the
# real Windows shell. A stand-in full-screen window (FakeFullScreen.cs) plays the part of the other app; the app is told
# that only the stand-in is a Remote Desktop client, so a real session on the test machine is never resized.
# It covers the primary screen and takes the keyboard focus for about half a minute: don't run it while you work.
param([string]$Exe, [string]$DataDir = (Join-Path $env:TEMP ("tt-fullscreen-" + [guid]::NewGuid().ToString('N'))), [int]$Port = 5403)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Windows.Forms, System.Drawing
Add-Type @"
using System; using System.Collections.Generic; using System.Runtime.InteropServices;
public static class Native {
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc p, IntPtr l);
  [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr h, int i);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern int GetWindowThreadProcessId(IntPtr h, out int pid);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, System.Text.StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern bool SystemParametersInfo(int action, int param, out RECT r, int winIni);
  [DllImport("user32.dll")] public static extern int GetSystemMetrics(int index);
  [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr value);
  [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
  public struct RECT { public int L, T, R, B; }
  // Top-level windows, frontmost first.
  public static List<IntPtr> ZOrder() { var list = new List<IntPtr>(); EnumWindows((h, l) => { list.Add(h); return true; }, IntPtr.Zero); return list; }
  // The process's top-level window titled "Todo Tracker", shown or hidden.
  public static IntPtr Sidebar(int pid) {
    var found = IntPtr.Zero;
    EnumWindows((h, l) => { int p; GetWindowThreadProcessId(h, out p); var s = new System.Text.StringBuilder(64); GetWindowText(h, s, 64);
      if (p == pid && s.ToString() == "Todo Tracker") { found = h; return false; } return true; }, IntPtr.Zero);
    return found;
  }
}
"@
$results = [System.Collections.Generic.List[string]]::new()
function Check($cond, $what) { if (-not $cond) { throw "FAILED: $what" }; $results.Add("PASS  $what"); Write-Host "PASS  $what" }
function Wait($cond, $what) { for ($i = 0; $i -lt 100; $i++) { if (& $cond) { return }; Start-Sleep -Milliseconds 100 }; throw "Timed out waiting for: $what" }
function Eventually($cond, $what) { for ($i = 0; $i -lt 60 -and -not (& $cond); $i++) { Start-Sleep -Milliseconds 100 }; Check (& $cond) $what }
[void][Native]::SetProcessDpiAwarenessContext([IntPtr]-4)
function WorkArea { $r = New-Object Native+RECT; [void][Native]::SystemParametersInfo(0x30, 0, [ref]$r, 0); [System.Drawing.Rectangle]::FromLTRB($r.L, $r.T, $r.R, $r.B) }
function Bounds { [System.Drawing.Rectangle]::new(0, 0, [Native]::GetSystemMetrics(0), [Native]::GetSystemMetrics(1)) }
function Rect($hwnd) { $r = New-Object Native+RECT; [void][Native]::GetWindowRect($hwnd, [ref]$r); [System.Drawing.Rectangle]::FromLTRB($r.L, $r.T, $r.R, $r.B) }
function IsTopmost($hwnd) { ([Native]::GetWindowLong($hwnd, -20) -band 0x8) -ne 0 }
function IsBehind($hwnd, $other) { $z = [Native]::ZOrder(); $z.IndexOf($hwnd) -gt $z.IndexOf($other) }
function Root { [System.Windows.Automation.AutomationElement]::RootElement }
function TryFind($scope, $name, $type, $tries) {
  $cond = New-Object System.Windows.Automation.AndCondition(
    (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $name)),
    (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, $type)))
  for ($i = 0; $i -lt $tries; $i++) { $e = $scope.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond); if ($e) { return $e }; Start-Sleep -Milliseconds 100 }
  $null
}
function SidebarElement($proc) {
  $cond = New-Object System.Windows.Automation.AndCondition(
    (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $proc.Id)),
    (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, 'Todo Tracker sidebar')))
  for ($i = 0; $i -lt 100; $i++) { $w = (Root).FindFirst([System.Windows.Automation.TreeScope]::Children, $cond); if ($w) { return $w }; Start-Sleep -Milliseconds 200 }
  throw 'Sidebar window did not appear'
}
function MenuItem($win, $name) {
  for ($attempt = 0; $attempt -lt 3; $attempt++) {
    $menu = TryFind $win 'Menu' ([System.Windows.Automation.ControlType]::Button) 50
    $menu.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    $item = TryFind (Root) $name ([System.Windows.Automation.ControlType]::MenuItem) 20
    if ($item) { return $item }
    [System.Windows.Forms.SendKeys]::SendWait('{ESC}'); Start-Sleep -Milliseconds 500
  }
  throw "Menu item not found: $name"
}
# Another app's full-screen window on the primary monitor (a developer's own Remote Desktop session): the sidebar
# rightly stays behind it, so "back on top" can't be checked.
function ForeignFullScreen($exclude) {
  $screen = Bounds
  foreach ($h in [Native]::ZOrder()) {
    if (-not [Native]::IsWindowVisible($h)) { continue }
    $p = 0; [void][Native]::GetWindowThreadProcessId($h, [ref]$p)
    if ($exclude -contains $p) { continue }
    $r = Rect $h
    if ($r.Left -le $screen.Left -and $r.Top -le $screen.Top -and $r.Right -ge $screen.Right -and $r.Bottom -ge $screen.Bottom -and -not ((Get-Process -Id $p -ErrorAction SilentlyContinue).ProcessName -eq 'explorer')) { return $true }
  }
  $false
}

# The stand-in full-screen app (Windows PowerShell compiles it; PowerShell 7's Add-Type can't make an exe).
$fakeDir = Join-Path $env:TEMP ("tt-fake-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $fakeDir | Out-Null
$fakeExe = Join-Path $fakeDir 'tt-fake-rdp.exe'
$source = Join-Path $PSScriptRoot 'FakeFullScreen.cs'
powershell.exe -NoProfile -Command "Add-Type -Path '$source' -OutputAssembly '$fakeExe' -OutputType ConsoleApplication"
if (-not (Test-Path $fakeExe)) { throw 'Could not build the stand-in full-screen app' }
$fakes = [System.Collections.Generic.List[System.Diagnostics.Process]]::new()
function StartFake($kind, [switch]$Resist) {
  $s = Bounds
  $info = [System.Diagnostics.ProcessStartInfo]::new($fakeExe, "$kind $($s.Left) $($s.Top) $($s.Width) $($s.Height) $(if ($Resist) { 'resist' })")
  $info.RedirectStandardOutput = $true; $info.UseShellExecute = $false
  $p = [System.Diagnostics.Process]::Start($info)
  $fakes.Add($p)
  $hwnd = [IntPtr][long]$p.StandardOutput.ReadLine()
  Wait { [Native]::IsWindowVisible($hwnd) } "stand-in $kind window"
  @{ Process = $p; Hwnd = $hwnd }
}
function StopFake($fake) { if (-not $fake.Process.HasExited) { $fake.Process.Kill(); [void]$fake.Process.WaitForExit(5000) } }
function CloseApp($proc) {
  # Hidden or not, the sidebar's window takes WM_CLOSE (Process.CloseMainWindow needs a visible window).
  [void][Native]::PostMessage([Native]::Sidebar($proc.Id), 0x0010, [IntPtr]::Zero, [IntPtr]::Zero)
  Check ($proc.WaitForExit(15000)) 'app exits cleanly'
}

$original = WorkArea; $screen = Bounds
Write-Host "Primary screen $screen, work area $original"
$env:TODOTRACKER_CLOUD = 'off'
$env:TODOTRACKER_REMOTE_DESKTOP_CLIENTS = 'tt-fake-rdp'
$proc = Start-Process $Exe -ArgumentList '--data', $DataDir, '--port', $Port -PassThru
try {
  $win = SidebarElement $proc
  $sidebar = [Native]::Sidebar($proc.Id)
  Wait { (WorkArea).Right -lt $original.Right } 'docked right'
  $mine = @($proc.Id)

  # 1. A full-screen window (a video, a slide show) on its monitor: the sidebar goes behind it instead of covering it.
  $video = StartFake 'plain'
  Eventually { -not (IsTopmost $sidebar) -and (IsBehind $sidebar $video.Hwnd) } 'a full-screen window goes in front of the docked sidebar'
  StopFake $video
  if (ForeignFullScreen ($mine + $video.Process.Id)) {
    Write-Host 'SKIP  back on top (another full-screen window is on this screen)'
  }
  else {
    Eventually { IsTopmost $sidebar } 'the sidebar is back on top when it closes'
  }

  # 2. A full-screen Remote Desktop session in front: fitted next to the sidebar, the sidebar stays visible.
  $rdp = StartFake 'rdp'
  $mine += $rdp.Process.Id
  Eventually { (Rect $rdp.Hwnd).Equals((WorkArea)) } 'the Remote Desktop window is fitted into the room next to the sidebar'
  Check ((Rect $rdp.Hwnd).Right -le (Rect $sidebar).Left) 'the session sits next to the sidebar, not under it'

  # 2b. Collapsing to the strip and opening again: the session follows the room, never under the sidebar.
  $button = TryFind $win 'Collapse' ([System.Windows.Automation.ControlType]::Button) 50
  $button.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
  Eventually { (Rect $sidebar).Width -lt 150 -and (Rect $rdp.Hwnd).Equals((WorkArea)) } 'collapsed: the session grows into the freed room'
  $button = TryFind $win 'Expand' ([System.Windows.Automation.ControlType]::Button) 50
  $button.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
  Eventually { (Rect $sidebar).Width -gt 150 -and (Rect $rdp.Hwnd).Equals((WorkArea)) -and (Rect $rdp.Hwnd).Right -le (Rect $sidebar).Left } 'expanded: the session shrinks back next to it'

  # 3. Hide from the sidebar's menu: the edge is given back, the session goes back to full screen, and it's remembered.
  (MenuItem $win 'Hide the sidebar').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
  Eventually { -not [Native]::IsWindowVisible($sidebar) } 'Hide the sidebar hides it'
  Eventually { (WorkArea).Equals($original) } 'hiding gives the screen edge back'
  Eventually { (Rect $rdp.Hwnd).Equals($screen) } 'the Remote Desktop window is full screen again'
  Eventually { (Get-Content (Join-Path $DataDir 'desktop.json') -Raw | ConvertFrom-Json).hidden -eq $true } 'desktop.json remembers it is hidden'
  StopFake $rdp
  CloseApp $proc

  # 4. Started again: it stays in the notification area until it's asked for (here: starting it once more).
  $proc = Start-Process $Exe -ArgumentList '--data', $DataDir, '--port', $Port -PassThru
  Wait { [Native]::Sidebar($proc.Id) -ne [IntPtr]::Zero } 'the sidebar window exists'
  Start-Sleep -Seconds 2
  $sidebar = [Native]::Sidebar($proc.Id)
  Check (-not [Native]::IsWindowVisible($sidebar)) 'restarted hidden'
  Check ((WorkArea).Equals($original)) 'restarted hidden: no screen space reserved'
  $second = Start-Process $Exe -ArgumentList '--data', $DataDir, '--port', $Port -PassThru
  [void]$second.WaitForExit(15000)
  Eventually { [Native]::IsWindowVisible($sidebar) } 'starting it again brings the sidebar back'
  Eventually { (WorkArea).Right -lt $original.Right } 'shown again: docked'
  Eventually { (Get-Content (Join-Path $DataDir 'desktop.json') -Raw | ConvertFrom-Json).hidden -eq $false } 'desktop.json remembers it is shown'

  # 5. A client that won't be resized (it goes straight back to full screen): left alone, the sidebar goes behind it.
  $stubborn = StartFake 'rdp' -Resist
  Eventually { -not (IsTopmost $sidebar) -and (IsBehind $sidebar $stubborn.Hwnd) } 'a session that stays full screen goes in front of the sidebar'
  Check ((Rect $stubborn.Hwnd).Equals($screen)) 'and stays full screen'
  StopFake $stubborn
  CloseApp $proc
  Check ((WorkArea).Equals($original)) 'no screen space left reserved after exit'
  Write-Host "`nALL $($results.Count) SYSTEM CHECKS PASSED"
}
finally {
  foreach ($p in $fakes) { if (-not $p.HasExited) { $p.Kill() } }
  if (-not $proc.HasExited) { [void][Native]::PostMessage([Native]::Sidebar($proc.Id), 0x0010, [IntPtr]::Zero, [IntPtr]::Zero); if (-not $proc.WaitForExit(10000)) { $proc.Kill() } }
  Remove-Item $DataDir, $fakeDir -Recurse -Force -ErrorAction SilentlyContinue
}
