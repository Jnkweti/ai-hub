param([string]$AppDirectory = '')
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Windows.Forms
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class HubNavigationWindow {
 [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr handle);
 [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr handle,int command);
 [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
}
'@
$hubRoot = Split-Path $PSScriptRoot -Parent
if (-not $AppDirectory) { $AppDirectory = Join-Path $hubRoot 'app' }
$hubData = Join-Path $hubRoot ('artifacts\navigation-smoke-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $hubData -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'fixtures\visual-room.json') -Destination (Join-Path $hubData 'rooms.json')
$env:AIHUB_DATA_DIR = $hubData; $env:AIHUB_UI_TEST = '1'
$hubApp = Start-Process -FilePath (Join-Path $AppDirectory 'AI Hub.exe') -WindowStyle Hidden -PassThru
try {
 $hubDeadline = (Get-Date).AddSeconds(15)
 do { Start-Sleep -Milliseconds 100; $hubApp.Refresh() } until ($hubApp.MainWindowHandle -ne 0 -or $hubApp.HasExited -or (Get-Date) -gt $hubDeadline)
 if ($hubApp.MainWindowHandle -eq 0) { throw 'No navigation test window' }
 $hubWindow = [System.Windows.Automation.AutomationElement]::FromHandle($hubApp.MainWindowHandle)
 function Control([string]$id,$root=$hubWindow) {
  $condition = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty,$id)
  $controlDeadline = (Get-Date).AddSeconds(5)
  do {
   $element = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$condition)
   if ($null -eq $element) { Start-Sleep -Milliseconds 75 }
  } until ($null -ne $element -or (Get-Date) -gt $controlDeadline)
  if ($null -eq $element) { throw "Missing $id" }; return $element
 }
 function Invoke-Control([string]$id,$root=$hubWindow) { (Control $id $root).GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
 function Key([string]$keys) {
  if ([HubNavigationWindow]::GetForegroundWindow() -ne $hubApp.MainWindowHandle) { throw 'Test window is not foreground; refusing to send keys' }
  [System.Windows.Forms.SendKeys]::SendWait($keys); Start-Sleep -Milliseconds 180
 }
 [HubNavigationWindow]::ShowWindow($hubApp.MainWindowHandle,9) | Out-Null
 [HubNavigationWindow]::SetForegroundWindow($hubApp.MainWindowHandle) | Out-Null
 (Control 'Composer').SetFocus(); Start-Sleep -Milliseconds 700
 $hubScroll = (Control 'ChatScroll').GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern)
 $hubScroll.SetScrollPercent(-1,0); Start-Sleep -Milliseconds 200
 if ((Control 'LatestButton').Current.IsOffscreen) { throw 'No way to jump to latest after scrolling up' }
 Invoke-Control 'LatestButton'; Start-Sleep -Milliseconds 200
 if ($hubScroll.Current.VerticalScrollPercent -lt 99) { throw 'Latest button did not scroll to the end' }
 Write-Output 'PASS jump to latest messages'
 $hubCopyCondition = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,'Copy message')
 $hubCopy = $hubWindow.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$hubCopyCondition)
 $hubCopy.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); Start-Sleep -Milliseconds 150
 if ($hubCopy.Current.HelpText -ne 'Copied') { throw 'Copy has no success feedback' }
 Write-Output 'PASS copy confirmation'
 Key '^k'; if (-not (Control 'SearchBox').Current.HasKeyboardFocus) { throw 'Ctrl+K did not focus search' }
 $hubSearch = (Control 'SearchBox').GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
 $hubSearch.SetValue('no such conversation'); Start-Sleep -Milliseconds 150
 $hubListItemCondition = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::ListItem)
 if ((Control 'RoomList').FindAll([System.Windows.Automation.TreeScope]::Children,$hubListItemCondition).Count -ne 0) { throw 'Search did not filter conversations' }
 if ((Control 'SearchEmpty').Current.IsOffscreen) { throw 'Empty search has no explanation' }
 Key '{ESC}'; if ($hubSearch.Current.Value -ne '') { throw 'Escape did not clear the idle search' }
 $hubSearch.SetValue('architecture'); Start-Sleep -Milliseconds 150
 if ((Control 'RoomList').FindAll([System.Windows.Automation.TreeScope]::Children,$hubListItemCondition).Count -ne 1) { throw 'Matching conversation not found' }
 Key '^l'; if (-not (Control 'Composer').Current.HasKeyboardFocus) { throw 'Ctrl+L did not focus composer' }
 Key '^n'; if ((Control 'SearchBox').GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value -ne '') { throw 'New conversation is hidden by the previous search' }
 Write-Output 'PASS search, composer, and new-conversation shortcuts'
 Key '{F2}'
 $hubWindowCondition = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::Window)
 $hubRename = $hubWindow.FindAll([System.Windows.Automation.TreeScope]::Descendants,$hubWindowCondition) | Where-Object { $_.Current.Name -eq 'Rename conversation' } | Select-Object -First 1
 if ($null -eq $hubRename) { throw 'F2 did not open rename' }
 (Control 'ConversationNameInput' $hubRename).GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('Planning board')
 Invoke-Control 'SaveConversationNameButton' $hubRename; Start-Sleep -Milliseconds 250
 if ((Control 'RoomTitle').Current.Name -ne 'Planning board') { throw 'Rename did not update the conversation' }
 Write-Output 'PASS conversation rename'
 Invoke-Control 'CloseButton'; if (-not $hubApp.WaitForExit(10000) -or $hubApp.ExitCode -ne 0) { throw 'Navigation test failed to close' }
 Write-Output "Isolated navigation evidence: $hubData"
}
finally {
 if (-not $hubApp.HasExited) { $hubApp.CloseMainWindow() | Out-Null; $hubApp.WaitForExit(5000) | Out-Null }
 $hubApp.Dispose()
}
