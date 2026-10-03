param([string]$AppDirectory = '')
# Native UI checks using fixture providers only. No model calls or project edits.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class MissionWindow {
 [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr handle);
 [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr handle, int command);
 [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr handle);
 [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr handle, IntPtr dc, uint flags);
}
'@
$hubRoot = Split-Path $PSScriptRoot -Parent
if (-not $AppDirectory) { $AppDirectory = Join-Path $hubRoot 'app' }
$hubData = Join-Path $hubRoot ('artifacts\mission-smoke-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $hubData -Force | Out-Null
$hubFixture = Join-Path $PSScriptRoot 'AIHub.Tests\bin\Release\net10.0\AIHub.Tests.exe'
if (-not (Test-Path -LiteralPath $hubFixture)) { throw 'Build the fixture runner first.' }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'fixtures\visual-room.json') -Destination (Join-Path $hubData 'rooms.json')
@{Workspace=$hubData; CodexPath=$hubFixture; ClaudePath=$hubFixture; AllowEdits=$true; AutoExchange=$true; MaxAutoRounds=1; ReduceMotion=$true} | ConvertTo-Json | Set-Content -Encoding UTF8 -LiteralPath (Join-Path $hubData 'settings.json')
$env:AIHUB_DATA_DIR = $hubData; $env:AIHUB_UI_TEST = '1'
$hubApp = Start-Process -FilePath (Join-Path $AppDirectory 'AI Hub.exe') -WindowStyle Hidden -PassThru
try {
 $hubDeadline = (Get-Date).AddSeconds(15)
 do { Start-Sleep -Milliseconds 100; $hubApp.Refresh() } until ($hubApp.MainWindowHandle -ne 0 -or $hubApp.HasExited -or (Get-Date) -gt $hubDeadline)
 if ($hubApp.MainWindowHandle -eq 0) { throw 'Mission-control window not available.' }
 $hubWindow = [System.Windows.Automation.AutomationElement]::FromHandle($hubApp.MainWindowHandle)
 function Control([string]$id, $root=$hubWindow) {
  $condition = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty,$id)
  $deadline = (Get-Date).AddSeconds(5)
  do { $element = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$condition); if ($null -eq $element) { Start-Sleep -Milliseconds 75 } } until ($null -ne $element -or (Get-Date) -gt $deadline)
  if ($null -eq $element) { throw "Missing control: $id" }; return $element
 }
 function Invoke-Control([string]$id) { (Control $id).GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
 function Capture([string]$name) {
  $rect = $hubWindow.Current.BoundingRectangle
  $bitmap = [System.Drawing.Bitmap]::new([int]$rect.Width,[int]$rect.Height)
  $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
  $dc = $graphics.GetHdc()
  try { if (-not [MissionWindow]::PrintWindow($hubApp.MainWindowHandle,$dc,2)) { throw 'Window capture failed.' } }
  finally { $graphics.ReleaseHdc($dc); $graphics.Dispose() }
  try { $bitmap.Save((Join-Path $hubData $name),[System.Drawing.Imaging.ImageFormat]::Png) } finally { $bitmap.Dispose() }
 }
 $null = Control 'Composer'
 [MissionWindow]::ShowWindow($hubApp.MainWindowHandle,9) | Out-Null
 $hubTransform = $hubWindow.GetCurrentPattern([System.Windows.Automation.TransformPattern]::Pattern)
 $hubDpi = [MissionWindow]::GetDpiForWindow($hubApp.MainWindowHandle)
 $hubScale = $hubDpi / 96.0
 $hubTransform.Resize((1080 * $hubScale),(700 * $hubScale)); Start-Sleep -Milliseconds 350
 if ((Control 'ChatScroll').Current.BoundingRectangle.Width -lt (800 * $hubScale)) { throw 'Compact layout did not automatically reclaim the activity rail.' }
 foreach ($id in @('Composer','SendButton','StopButton','Target','AutoToggle')) {
  $item = Control $id
  if ($item.Current.IsOffscreen -or -not $hubWindow.Current.BoundingRectangle.Contains($item.Current.BoundingRectangle)) { throw "Compact layout clips $id" }
 }
 Capture 'compact.png'
 Write-Output ('PASS compact layout and primary controls at ' + $hubDpi + ' DPI')
 (Control 'Composer').GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('Codex, review the test fixture project together and compare your results.')
 Invoke-Control 'SendButton'
 foreach ($agent in @('Codex','Claude')) {
  $deadline = (Get-Date).AddSeconds(12)
  $dialog = $null
  do {
   Start-Sleep -Milliseconds 100
   $cardCondition = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty,'AgentRequestCard')
   $agentLabel = if ($agent -eq 'Claude') { 'Claude Code' } else { 'Codex' }
   $dialog = $hubWindow.FindAll([System.Windows.Automation.TreeScope]::Descendants,$cardCondition) | Where-Object { $_.Current.Name -eq $agentLabel -and (Control 'RequestStatus' $_).Current.Name -eq 'Waiting for you' } | Select-Object -First 1
  } until ($null -ne $dialog -or (Get-Date) -gt $deadline)
  if ($null -eq $dialog) { throw "$agent approval request was not shown." }
  if ((Control ($agent + 'Status')).Current.Name -ne 'Needs your input') { throw "$agent has no accurate pending-input state." }
  if ($dialog.Current.IsOffscreen) { throw 'Approval card is not visible in the conversation.' }
  $decline = $dialog.FindFirst([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,'Decline'))
  if ($null -eq $decline) { throw 'Decline control is missing.' }
  $decline.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
 }
 Write-Output 'PASS both approval cards appear in chat and show the waiting agent'
 $deadline = (Get-Date).AddSeconds(12)
 do { Start-Sleep -Milliseconds 100; $paused = (Control 'RoundLabel').Current.Name -eq 'Paused' } until ($paused -or (Get-Date) -gt $deadline)
 if (-not $paused) { throw 'Round indicator did not reach the guarded pause.' }
 if ((Control 'HandoffLink').Current.Name -ne ('Codex ' + [char]0x2192 + ' Claude')) { throw 'Last handoff direction was not displayed.' }
 if ((Control 'PauseReasonLabel').Current.Name -notlike '*1-round*') { throw 'Round-limit reason is missing.' }
 if ((Control 'CodexStatus').Current.Name -ne 'Paused' -or (Control 'ClaudeStatus').Current.Name -ne 'Paused') { throw 'Agent states disagree with the pause.' }
 Write-Output 'PASS actual handoff direction, round-limit pause, and agent states'
 $hubTransform.Resize((1500 * $hubScale),(960 * $hubScale)); Start-Sleep -Milliseconds 250
 Capture 'handoff-paused.png'
 Invoke-Control 'ActivityToggle'; Start-Sleep -Milliseconds 100
 $wideChat = (Control 'ChatScroll').Current.BoundingRectangle.Width
 $hubTransform.Resize((1280 * $hubScale),(720 * $hubScale)); Start-Sleep -Milliseconds 150
 $hubTransform.Resize((1500 * $hubScale),(960 * $hubScale)); Start-Sleep -Milliseconds 150
 if ([Math]::Abs((Control 'ChatScroll').Current.BoundingRectangle.Width - $wideChat) -gt 3) { throw 'Resizing overrode the chosen activity visibility.' }
 Write-Output 'PASS explicit activity visibility survives resizing with reduced motion'
 Invoke-Control 'CloseButton'
 if (-not $hubApp.WaitForExit(10000) -or $hubApp.ExitCode -ne 0) { throw 'Mission control did not close cleanly.' }
 Write-Output "Isolated mission-control evidence: $hubData"
}
finally {
 if (-not $hubApp.HasExited) { $hubApp.CloseMainWindow() | Out-Null; $hubApp.WaitForExit(5000) | Out-Null }
 $hubApp.Dispose()
}
