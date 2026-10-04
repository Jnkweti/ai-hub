param([string]$AppDirectory = '')
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$hubRoot = Split-Path $PSScriptRoot -Parent
if (-not $AppDirectory) { $AppDirectory = Join-Path $hubRoot 'app' }
$hubData = Join-Path $hubRoot ('artifacts\audit-smoke-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $hubData | Out-Null
$hubFixture = Join-Path $PSScriptRoot 'AIHub.Tests\bin\Release\net10.0\AIHub.Tests.exe'
$hubOriginalText = ('**long text** ' * 9000) + 'FULL MESSAGE END'
$hubRooms = @($null, @{Id='audit';Title='Recovered audit';Workspace=$hubData;Target='Codex';CodexContext=$null;ClaudeContext=@{MessageIds=$null};Messages=@($null,@{Id='large';Speaker='Claude';Text=$hubOriginalText;Complete=$true})}, @{Id='broken';Title=$null;Workspace=$null;Messages=$null})
ConvertTo-Json -Depth 10 -InputObject $hubRooms | Set-Content -Encoding UTF8 -LiteralPath (Join-Path $hubData 'rooms.json')
$hubOriginalRooms = [IO.File]::ReadAllText((Join-Path $hubData 'rooms.json'))
@{Workspace=$hubData;LastRoomId='audit';CodexPath=$hubFixture;ClaudePath=$hubFixture;ClaudeModel=$null;MaxAutoRounds=-3;AllowEdits=$false;AutoExchange=$false;ReduceMotion=$true} | ConvertTo-Json | Set-Content -Encoding UTF8 -LiteralPath (Join-Path $hubData 'settings.json')
$hubPreviousData = $env:AIHUB_DATA_DIR
$env:AIHUB_DATA_DIR = $hubData; $env:AIHUB_UI_TEST = '1'
$hubApp = $null
$hubSecond = $null
function Wait-For([scriptblock]$condition, [string]$failure) {
 $deadline = (Get-Date).AddSeconds(20)
 do { if (& $condition) { return }; Start-Sleep -Milliseconds 100 } until ((Get-Date) -gt $deadline)
 throw $failure
}
function Control([string]$id) {
 $condition = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty,$id)
 $element = $script:hubWindow.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$condition)
 if (-not $element) { throw "Missing control: $id" }; return $element
}
function Invoke-Control([string]$id) { (Control $id).GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
function Start-Hub {
 $script:hubApp = Start-Process -FilePath (Join-Path $AppDirectory 'AI Hub.exe') -WindowStyle Hidden -PassThru
 Wait-For { $script:hubApp.Refresh(); $script:hubApp.MainWindowHandle -ne 0 } 'No audit window; damaged data or rendering prevented startup'
 Start-Sleep -Milliseconds 1000
 $script:hubWindow = [System.Windows.Automation.AutomationElement]::FromHandle($script:hubApp.MainWindowHandle)
 $null = Control 'Composer'
}
function Close-Hub {
 Invoke-Control 'CloseButton'
 if (-not $script:hubApp.WaitForExit(10000) -or $script:hubApp.ExitCode -ne 0) { throw 'Audit window did not close cleanly' }
 $script:hubApp.Dispose(); $script:hubApp = $null
}
try {
 Start-Hub
 if (@(Get-ChildItem -LiteralPath $hubData -Filter 'rooms.json.unreadable-*').Count -ne 1) { throw 'Recovery did not back up the original rooms' }
 $hubBackup = Get-ChildItem -LiteralPath $hubData -Filter 'rooms.json.unreadable-*' | Select-Object -First 1
 if ([IO.File]::ReadAllText($hubBackup.FullName) -cne $hubOriginalRooms) { throw 'Recovery changed the backup' }
 $documents = (Control 'MessageList').FindAll([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::Document))
 $preview = @($documents | ForEach-Object { $_.GetCurrentPattern([System.Windows.Automation.TextPattern]::Pattern).DocumentRange.GetText(-1) }) -join "`n"
 if ($preview -notlike '*Long message preview limited*' -or $preview -like '*FULL MESSAGE END*') { throw 'Long message preview was not bounded visibly' }
 $hubSecond = Start-Process -FilePath (Join-Path $AppDirectory 'AI Hub.exe') -WindowStyle Hidden -PassThru
 if (-not $hubSecond.WaitForExit(10000) -or $hubSecond.ExitCode -ne 0) { throw 'A second instance did not reuse the original application' }
 $hubApp.Refresh(); if ($hubApp.HasExited) { throw 'Original instance was replaced' }
 $hubLog = Join-Path $hubData 'activity-audit.jsonl'
 [IO.File]::WriteAllText($hubLog,'read-only sentinel')
 [IO.File]::SetAttributes($hubLog,[IO.FileAttributes]::ReadOnly)
 (Control 'Composer').GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('activity-fixture')
 Invoke-Control 'SendButton'
 Wait-For { (Control 'CodexStatus').Current.Name -eq 'Ready' } 'Read-only activity log interrupted the agent or crashed the UI'
 if ([IO.File]::ReadAllText($hubLog) -cne 'read-only sentinel') { throw 'Read-only log was overwritten' }
 [IO.File]::SetAttributes($hubLog,[IO.FileAttributes]::Normal)
 Close-Hub
 $saved = Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $hubData 'rooms.json') | ConvertFrom-Json
 $auditTranscript = @(Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $hubData 'room-audit.json') | ConvertFrom-Json) # split out of the legacy rooms.json on first load (0.23.0)
 if ($auditTranscript[0].Text -cne $hubOriginalText) { throw 'Preview truncation lost the full saved message' }
 if (@($saved).Count -ne 2) { throw 'Damaged room repair discarded a recoverable conversation' }
 Start-Hub
 if ((Control 'RoomTitle').Current.Name -ne 'Recovered audit') { throw 'Reopening recovered data lost selection' }
 Close-Hub
 Write-Output 'PASS native recovery preserves original backup and valid conversations'
 Write-Output 'PASS bounded Markdown preview retains the full saved message'
 Write-Output 'PASS second launch reuses the running instance; restart releases ownership'
 Write-Output 'PASS read-only activity log is handled without a crash'
 Write-Output "Audit fixture: $hubData"
}
finally {
 if ($hubLog -and (Test-Path -LiteralPath $hubLog)) { [IO.File]::SetAttributes($hubLog,[IO.FileAttributes]::Normal) }
 foreach ($process in @($hubApp,$hubSecond)) { if ($null -ne $process) { if (-not $process.HasExited) { $process.Kill() }; $process.Dispose() } }
 $env:AIHUB_DATA_DIR = $hubPreviousData; Remove-Item Env:AIHUB_UI_TEST -ErrorAction SilentlyContinue
}
