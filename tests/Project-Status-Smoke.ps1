param([string]$AppDirectory = '')
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class StatusWindow {
 [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr dc, uint f);
}
'@
$hubRoot = Split-Path $PSScriptRoot -Parent
if (-not $AppDirectory) { $AppDirectory = Join-Path $hubRoot 'app' }
$hubEvidence = Join-Path $hubRoot ('artifacts\status-smoke-' + [Guid]::NewGuid().ToString('N'))
$hubData = Join-Path $hubEvidence 'data'
$hubProject = Join-Path $hubEvidence 'project'
New-Item -ItemType Directory -Path $hubData,$hubProject -Force | Out-Null
'original' | Set-Content -Encoding UTF8 -LiteralPath (Join-Path $hubProject 'README.md')
$hubFixture = Join-Path $PSScriptRoot 'AIHub.Tests\bin\Release\net10.0\AIHub.Tests.exe'
# Property order matches the host's serialized session configuration.
$hubSignature = [ordered]@{CodexModel='';ClaudeModel='';AllowEdits=$true;Workspace=$hubProject} | ConvertTo-Json -Compress
$hubRooms = @(@{Id='status-room';Title='Status test';Workspace=$hubProject;Draft='Keep this draft';Target='Both';CodexSession='ordinary-codex';ClaudeSession='ordinary-claude';SessionOptions=$hubSignature;Messages=@()})
ConvertTo-Json -InputObject $hubRooms -Depth 8 | Set-Content -Encoding UTF8 -LiteralPath (Join-Path $hubData 'rooms.json')
@{Workspace=$hubProject;LastRoomId='status-room';CodexPath=$hubFixture;ClaudePath=$hubFixture;AllowEdits=$true;AutoExchange=$true;ReduceMotion=$true} | ConvertTo-Json | Set-Content -Encoding UTF8 -LiteralPath (Join-Path $hubData 'settings.json')
$hubPreviousData = $env:AIHUB_DATA_DIR
$env:AIHUB_DATA_DIR = $hubData; $env:AIHUB_UI_TEST = '1'
$hubApp = $null
function Wait-For([scriptblock]$condition, [string]$failure) {
 $deadline = (Get-Date).AddSeconds(15)
 do { if (& $condition) { return }; Start-Sleep -Milliseconds 100 } until ((Get-Date) -gt $deadline)
 throw $failure
}
function Control([string]$id, $root=$script:hubWindow) {
 $condition = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty,$id)
 $deadline = (Get-Date).AddSeconds(5)
 do { $element = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$condition); if ($null -eq $element) { Start-Sleep -Milliseconds 75 } } until ($null -ne $element -or (Get-Date) -gt $deadline)
 if ($null -eq $element) { throw "Missing control: $id" }; return $element
}
function Invoke-Control([string]$id, $root=$script:hubWindow) { (Control $id $root).GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
function Rooms { return Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $hubData 'rooms.json') | ConvertFrom-Json }
function Current-Room { return @(Rooms | Where-Object { $_.Id -eq 'status-room' })[0] }
function Report-Files { return @(Get-ChildItem -LiteralPath (Join-Path $hubData 'project-status') -Filter report.json -File -Recurse -ErrorAction SilentlyContinue) }
function Start-Hub {
 $script:hubApp = Start-Process -FilePath (Join-Path $AppDirectory 'AI Hub.exe') -WindowStyle Hidden -PassThru
 Wait-For { $script:hubApp.Refresh(); $script:hubApp.MainWindowHandle -ne 0 } 'Status test window did not open'
 Start-Sleep -Milliseconds 1000
 $script:hubWindow = [System.Windows.Automation.AutomationElement]::FromHandle($script:hubApp.MainWindowHandle)
 $null = Control 'Composer'
}
function Close-Hub {
 Invoke-Control 'CloseButton'
 if (-not $script:hubApp.WaitForExit(10000) -or $script:hubApp.ExitCode -ne 0) { throw 'Status window did not close cleanly' }
 $script:hubApp.Dispose(); $script:hubApp = $null
}
function Capture([string]$name) {
 $rect = $script:hubWindow.Current.BoundingRectangle
 $bitmap = [System.Drawing.Bitmap]::new([int]$rect.Width,[int]$rect.Height)
 $graphics = [System.Drawing.Graphics]::FromImage($bitmap); $dc = $graphics.GetHdc()
 try { if (-not [StatusWindow]::PrintWindow($script:hubApp.MainWindowHandle,$dc,2)) { throw 'Capture failed' } }
 finally { $graphics.ReleaseHdc($dc); $graphics.Dispose() }
 try { $bitmap.Save((Join-Path $hubEvidence $name),[System.Drawing.Imaging.ImageFormat]::Png) } finally { $bitmap.Dispose() }
}
try {
 Start-Hub
 if ((Current-Room).CodexSession -ne 'ordinary-codex') { throw 'Fixture session signature mismatch' }
 Invoke-Control 'ProjectStatusButton'
 Wait-For { @(Report-Files).Count -eq 1 -and @((Current-Room).Messages | Where-Object { $_.Text -like '*Project status saved*' }).Count -eq 1 } 'Cold project status did not save'
 if ((Control 'Composer').GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value -ne 'Keep this draft') { throw 'Status action overwrote draft' }
 if ((Current-Room).CodexSession -ne 'ordinary-codex' -or (Current-Room).ClaudeSession -ne 'ordinary-claude') { throw 'Status replaced ordinary provider sessions' }
 $messages = @((Current-Room).Messages | Where-Object { $_.Speaker -in @('Codex','Claude') })
 if ($messages.Count -ne 2 -or $messages[0].Speaker -ne 'Codex' -or $messages[1].Speaker -ne 'Claude') { throw 'Inspection/review order incorrect' }
 $first = Get-Content -Raw -LiteralPath @(Report-Files)[0].FullName | ConvertFrom-Json
 Invoke-Control 'ProjectStatusButton'
 Wait-For { @((Current-Room).Messages | Where-Object { $_.Text -like '*Reused project status*' }).Count -eq 1 } 'Warm report not reused'
 if (@((Current-Room).Messages | Where-Object { $_.Speaker -in @('Codex','Claude') }).Count -ne 2) { throw 'Warm status invoked providers' }
 'changed' | Set-Content -Encoding UTF8 -LiteralPath (Join-Path $hubProject 'README.md')
 Invoke-Control 'ProjectStatusButton'
 Wait-For { @((Current-Room).Messages | Where-Object { $_.Text -like '*Project status saved*' }).Count -eq 2 } 'Changed project did not refresh'
 $second = Get-Content -Raw -LiteralPath @(Report-Files)[0].FullName | ConvertFrom-Json
 if ($first.RunId -eq $second.RunId -or $first.Fingerprint -eq $second.Fingerprint) { throw 'Refresh retained old report identity/fingerprint' }
 Invoke-Control 'StatusActionsButton'
 Start-Sleep -Milliseconds 250
 Invoke-Control 'RefreshStatusMenuItem' ([System.Windows.Automation.AutomationElement]::RootElement)
 Wait-For { @((Current-Room).Messages | Where-Object { $_.Text -like '*Project status saved*' }).Count -eq 3 } 'Explicit refresh did not inspect again'
 $script:hubWindow.GetCurrentPattern([System.Windows.Automation.TransformPattern]::Pattern).Resize(1080 * 1.5,700 * 1.5)
 Start-Sleep -Milliseconds 250
 foreach ($id in @('ProjectStatusButton','StatusActionsButton','RoomList','DeleteConversationButton','ArchiveButton','Composer')) {
  $control = Control $id
  if ($control.Current.IsOffscreen -or $control.Current.BoundingRectangle.Height -le 0) { throw "Compact layout hides $id" }
 }
 Capture 'status-compact.png'
 Close-Hub
 Start-Hub
 Invoke-Control 'ProjectStatusButton'
 Wait-For { @((Current-Room).Messages | Where-Object { $_.Text -like '*Reused project status*' }).Count -eq 2 } 'Restart did not reuse persistent report'
 Invoke-Control 'StopButton'
 (Control 'Composer').GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('')
 Invoke-Control 'ContinueButton'
 Wait-For { @((Current-Room).Messages | Where-Object { $_.Text -like '*Reused project status*' }).Count -eq 3 } 'Continue bypassed shared status workflow'
 (Control 'Composer').GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('Keep this draft')
 Invoke-Control 'StatusActionsButton'
 Start-Sleep -Milliseconds 200
 Invoke-Control 'ForgetStatusMenuItem' ([System.Windows.Automation.AutomationElement]::RootElement)
 Wait-For { @(Report-Files).Count -eq 0 -and (Control 'ProjectStatusButton').Current.IsEnabled } 'Forget did not clear saved report'
 if ((Control 'Composer').GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value -ne 'Keep this draft') { throw 'Forget overwrote draft' }
 Invoke-Control 'ProjectStatusButton'
 Wait-For { @((Current-Room).Messages | Where-Object { $_.Text -like '*Project status saved*' }).Count -eq 4 } 'Status did not rebuild after Forget'
 Invoke-Control 'SettingsButton'
 Start-Sleep -Milliseconds 200
 $inspector = Control 'StatusInspectorInput'
 $inspector.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
 $claudeName = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,'Claude Code')
 $inspector.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$claudeName).GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
 $inspector.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Collapse()
 Invoke-Control 'SaveSettingsButton'
 Invoke-Control 'ProjectStatusButton'
 Wait-For { @((Current-Room).Messages | Where-Object { $_.Text -like '*Project status saved*' }).Count -eq 5 } 'Inspector preference did not invalidate status'
 $final = Get-Content -Raw -LiteralPath @(Report-Files)[0].FullName | ConvertFrom-Json
 if ($final.Inspector -ne 1 -or $final.Reviewer -ne 0) { throw 'Preferred inspector did not own inspection' }
 Invoke-Control 'ArchiveButton'
 Wait-For { @(Rooms | Where-Object { $_.Id -eq 'status-room' -and $_.IsArchived }).Count -eq 1 } 'Status conversation did not archive'
 if (@(Report-Files).Count -ne 1) { throw 'Archive discarded shared report' }
 $filter = Control 'RoomFilter'; $filter.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
 $name = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,'Archived conversations')
 $filter.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$name).GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
 $filter.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Collapse()
 Start-Sleep -Milliseconds 200
 $items = (Control 'RoomList').FindAll([System.Windows.Automation.TreeScope]::Children,[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::ListItem))
 $items[0].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
 Wait-For { (Control 'RoomTitle').Current.Name -eq 'Status test' } 'Archived status room not selected'
 if ((Control 'ProjectStatusButton').Current.IsEnabled -or (Control 'StatusActionsButton').Current.IsEnabled) { throw 'Archived room starts status work' }
 Invoke-Control 'DeleteConversationButton'
 Start-Sleep -Milliseconds 300
 Invoke-Control 'ConfirmDeleteConversationButton'
 Wait-For { @(Rooms | Where-Object { $_.Id -eq 'status-room' }).Count -eq 0 } 'Status room was not deleted'
 if (@(Report-Files).Count -ne 0 -or -not (Test-Path -LiteralPath (Join-Path $hubProject 'README.md'))) { throw 'Delete retained source report or removed project source' }
 Close-Hub
 Write-Output ('PASS status UI: cold, warm, stale, refresh, forget, continuation, inspector preference, restart, draft/session isolation, archive retention, deletion; ' + $hubEvidence)
}
finally {
 if ($hubApp -and -not $hubApp.HasExited) { $hubApp.Kill(); $hubApp.WaitForExit() }
 $env:AIHUB_DATA_DIR = $hubPreviousData; Remove-Item Env:AIHUB_UI_TEST -ErrorAction SilentlyContinue
}
