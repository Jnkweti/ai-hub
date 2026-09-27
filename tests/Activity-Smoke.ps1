param([string]$AppDirectory = '')
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class ActivityWindow {
 [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr dc, uint f);
}
'@
$hubRoot = Split-Path $PSScriptRoot -Parent
if (-not $AppDirectory) { $AppDirectory = Join-Path $hubRoot 'app' }
$hubData = Join-Path $hubRoot ('artifacts\activity-smoke-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $hubData -Force | Out-Null
$hubFixture = Join-Path $PSScriptRoot 'AIHub.Tests\bin\Release\net10.0\AIHub.Tests.exe'
@{Workspace=$hubData;LastRoomId='activity';CodexPath=$hubFixture;ClaudePath=$hubFixture;AllowEdits=$true;AutoExchange=$false;ReduceMotion=$true} | ConvertTo-Json | Set-Content -Encoding UTF8 -LiteralPath (Join-Path $hubData 'settings.json')
ConvertTo-Json -Depth 8 -InputObject @(@{Id='activity';Title='Activity check';Workspace=$hubData;Messages=@()}) | Set-Content -Encoding UTF8 -LiteralPath (Join-Path $hubData 'rooms.json')
$hubPreviousData = $env:AIHUB_DATA_DIR
$env:AIHUB_DATA_DIR = $hubData
$hubApp = $null
function Wait-For([scriptblock]$condition, [string]$failure) {
 $deadline = (Get-Date).AddSeconds(15)
 do { if (& $condition) { return }; Start-Sleep -Milliseconds 100 } until ((Get-Date) -gt $deadline)
 throw $failure
}
function Control([string]$id, $root=$script:hubWindow) {
 $condition = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty,$id)
 $deadline = (Get-Date).AddSeconds(5)
 do { $element = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$condition); if (-not $element) { Start-Sleep -Milliseconds 75 } } until ($element -or (Get-Date) -gt $deadline)
 if (-not $element) { throw "Missing control: $id" }; return $element
}
function Invoke-Control([string]$id) { (Control $id).GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
function Start-Hub {
 $script:hubApp = Start-Process -FilePath (Join-Path $AppDirectory 'AI Hub.exe') -WindowStyle Hidden -PassThru
 Wait-For { $script:hubApp.Refresh(); $script:hubApp.MainWindowHandle -ne 0 } 'No activity test window'
 Start-Sleep -Milliseconds 1000
 $script:hubWindow = [System.Windows.Automation.AutomationElement]::FromHandle($script:hubApp.MainWindowHandle)
 $null = Control 'Composer'
 if ((Control 'ActivityList').Current.IsOffscreen) { Invoke-Control 'ActivityToggle' }
}
function Close-Hub {
 Invoke-Control 'CloseButton'
 if (-not $script:hubApp.WaitForExit(10000) -or $script:hubApp.ExitCode -ne 0) { throw 'Activity window did not close cleanly' }
 $script:hubApp.Dispose(); $script:hubApp = $null
}
function Activity-Text {
 $condition = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::Text)
 return @((Control 'ActivityList').FindAll([System.Windows.Automation.TreeScope]::Descendants,$condition) | ForEach-Object { $_.Current.Name })
}
function Capture {
 $rect = $script:hubWindow.Current.BoundingRectangle
 $bitmap = [System.Drawing.Bitmap]::new([int]$rect.Width,[int]$rect.Height)
 $graphics = [System.Drawing.Graphics]::FromImage($bitmap); $dc = $graphics.GetHdc()
 try { [ActivityWindow]::PrintWindow($script:hubApp.MainWindowHandle,$dc,2) | Out-Null }
 finally { $graphics.ReleaseHdc($dc); $graphics.Dispose() }
 try { $bitmap.Save((Join-Path $hubData 'activity.png'),[System.Drawing.Imaging.ImageFormat]::Png) } finally { $bitmap.Dispose() }
}
try {
 Start-Hub
 (Control 'Composer').GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('activity-fixture')
 Invoke-Control 'SendButton'
 Wait-For { (Control 'CodexStatus').Current.Name -eq 'Ready' -and (Control 'ClaudeStatus').Current.Name -eq 'Ready' } 'Fixture agents did not finish'
 Wait-For { (Control 'ActivityCount').Current.Name -eq '6' } 'Action feed did not consolidate to six meaningful entries'
 $text = Activity-Text
 if ($text -notcontains 'Read README.md' -or $text -notcontains 'Read missing.cs' -or $text -notcontains 'Failed' -or $text -notcontains 'Exit 2') { throw ('Missing useful action/result: ' + ($text -join '|')) }
 if (@($text | Where-Object { $_ -like '*thinking_tokens*' -or $_ -like '*Token usage updated*' -or $_ -eq 'Whitespace output' }).Count -ne 0) { throw 'Diagnostics leaked into actions' }
 Capture
 $listItemType = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::ListItem)
 $readName = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,'Read README.md')
 $row = (Control 'ActivityList').FindAll([System.Windows.Automation.TreeScope]::Descendants,$listItemType) | Where-Object { $_.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$readName) } | Select-Object -First 1
 $row.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
 $details = Control 'ActivityDetailsText'
 $body = $details.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value
 if ($body -notlike '*read output*') { throw 'Grouped action has no inspectable output' }
 $detailWindow = $details
 do { $detailWindow = [System.Windows.Automation.TreeWalker]::ControlViewWalker.GetParent($detailWindow) } until (-not $detailWindow -or $detailWindow.Current.ControlType -eq [System.Windows.Automation.ControlType]::Window)
 if (-not $detailWindow -or $detailWindow.Current.NativeWindowHandle -eq $script:hubApp.MainWindowHandle.ToInt64()) { throw 'Could not identify the owned activity-details window' }
 $detailWindow.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close()
 (Control 'DiagnosticsToggle').GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()
 Wait-For { (Control 'ActivityCount').Current.Name -eq '200' -and (Control 'ActivityScopeLabel').Current.Name -eq 'Recent diagnostic events' } 'Diagnostics toggle lost raw event history'
 $raw = @(Get-Content -LiteralPath (Join-Path $hubData 'activity-activity.jsonl') | ForEach-Object { $_ | ConvertFrom-Json })
 if ($raw.Count -lt 500 -or @($raw | Where-Object { $_.Kind -eq 4 -and [string]::IsNullOrWhiteSpace($_.Text) }).Count -lt 100) { throw 'Full diagnostic log discarded source events' }
 Close-Hub
 Start-Hub
 if ((Control 'DiagnosticsToggle').GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Current.ToggleState -ne [System.Windows.Automation.ToggleState]::On) { throw 'Diagnostic preference was not saved' }
 (Control 'DiagnosticsToggle').GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()
 $emptyName = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,'A window into the work')
 $empty = $script:hubWindow.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$emptyName)
 if ((Control 'ActivityCount').Current.Name -ne '0' -or -not $empty -or $empty.Current.IsOffscreen) { throw 'Old rows leaked into reopened session' }
 Close-Hub
 Write-Output ('PASS activity: 500+ raw events become six actions; errors, output detail, diagnostics, full logging and preference persistence; ' + $hubData)
}
finally {
 if ($hubApp -and -not $hubApp.HasExited) { $hubApp.Kill(); $hubApp.WaitForExit() }
 $env:AIHUB_DATA_DIR = $hubPreviousData
}
