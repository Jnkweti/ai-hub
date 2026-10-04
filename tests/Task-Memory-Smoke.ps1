param([string]$AppDirectory = '', [string]$FixturePath = '')
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
# Quiet check (0.35.1): drives the app through UI Automation only; it never restores or activates the window, and reads
# transcripts from the per-room files that have held them since 0.23.0.
$hubRoot = Split-Path $PSScriptRoot -Parent
if (-not $AppDirectory) { $AppDirectory = Join-Path $hubRoot 'artifacts\tasks-candidate' }
if (-not $FixturePath) { $FixturePath = Join-Path $hubRoot 'artifacts\tasks-test-build\bin\AIHub.Tests\release\AIHub.Tests.exe' }
$hubData = Join-Path $hubRoot ('artifacts\task-smoke-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $hubData | Out-Null
@{Workspace=$hubData;LastRoomId='background';CodexPath=$FixturePath;ClaudePath=$FixturePath;AllowEdits=$false;AutoExchange=$false;ReduceMotion=$true} | ConvertTo-Json | Set-Content -Encoding UTF8 -LiteralPath (Join-Path $hubData 'settings.json')
$hubLegacyOptions = [ordered]@{CodexModel='';ClaudeModel='';AllowEdits=$false;Workspace=$hubData} | ConvertTo-Json -Compress
ConvertTo-Json -Depth 8 -InputObject @(@{Id='background';Title='Background task';Workspace=$hubData;Target='Codex';CodexSession='legacy-codex';ClaudeSession='legacy-claude';SessionOptions=$hubLegacyOptions;Messages=@()}) | Set-Content -Encoding UTF8 -LiteralPath (Join-Path $hubData 'rooms.json')
$hubPreviousData = $env:AIHUB_DATA_DIR
$env:AIHUB_DATA_DIR = $hubData; $env:AIHUB_UI_TEST = '1'
$hubApp = $null
function Wait-For([scriptblock]$condition,[string]$failure) {
 $deadline = (Get-Date).AddSeconds(25)
 do { if (& $condition) { return }; Start-Sleep -Milliseconds 100 } until ((Get-Date) -gt $deadline)
 throw $failure
}
function Control([string]$id,$root=$script:hubWindow) {
 if ($null -eq $root) { $root = $script:hubWindow }
 $condition = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty,$id)
 $deadline = (Get-Date).AddSeconds(10)
 do { $element = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$condition); if (-not $element) { Start-Sleep -Milliseconds 75 } } until ($element -or (Get-Date) -gt $deadline)
 if (-not $element) { throw "Missing control $id in window $($root.Current.Name), PID $($root.Current.ProcessId), class $($root.Current.ClassName)" }; return $element
}
function Invoke-Control([string]$id,$root=$script:hubWindow) { (Control $id $root).GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
function Value([string]$id,[string]$text,$root=$script:hubWindow) { (Control $id $root).GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($text) }
function Send([string]$text) { Value 'Composer' $text; Invoke-Control 'SendButton' }
function Tasks { $records = Get-Content -Raw -LiteralPath (Join-Path $hubData 'tasks.json') | ConvertFrom-Json; foreach ($record in $records) { Write-Output $record } }
function Rooms {
 # rooms.json is a header index since 0.23.0; each room's messages live in room-<id>.json.
 $records = Get-Content -Raw -LiteralPath (Join-Path $hubData 'rooms.json') | ConvertFrom-Json
 foreach ($record in $records) {
  $transcript = Join-Path $hubData ('room-' + $record.Id + '.json')
  $messages = if (Test-Path -LiteralPath $transcript) { @(Get-Content -Raw -LiteralPath $transcript | ConvertFrom-Json) } else { @() }
  $record | Add-Member -NotePropertyName Messages -NotePropertyValue $messages -Force
  Write-Output $record
 }
}
function Dialog([string]$title) {
 $named = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,$title)
 $typed = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::Window)
 $owned = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ProcessIdProperty,$script:hubApp.Id)
 $condition = [System.Windows.Automation.AndCondition]::new([System.Windows.Automation.Condition[]]@($named,$typed,$owned))
 $deadline = (Get-Date).AddSeconds(20)
 do { $element = [System.Windows.Automation.AutomationElement]::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$condition); if (-not $element) { Start-Sleep -Milliseconds 75 } } until ($element -or (Get-Date) -gt $deadline)
 if (-not $element) { throw "Missing dialog $title" }; return $element
}
function Start-Hub {
 $script:hubApp = Start-Process -FilePath (Join-Path $AppDirectory 'AI Hub.exe') -WindowStyle Hidden -PassThru
 $processCondition = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ProcessIdProperty,$script:hubApp.Id)
 $nameCondition = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,'AI Hub')
 $windowCondition = [System.Windows.Automation.AndCondition]::new($processCondition,$nameCondition)
 Wait-For { $script:hubWindow = [System.Windows.Automation.AutomationElement]::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Children,$windowCondition); $null -ne $script:hubWindow } 'Task window did not open'
 Start-Sleep -Milliseconds 500
 $hubHandle = [IntPtr]$script:hubWindow.Current.NativeWindowHandle
 Wait-For { $script:hubWindow = [System.Windows.Automation.AutomationElement]::FromHandle($hubHandle); $script:hubWindow.Current.ClassName -eq 'Window' } 'WPF automation provider did not attach'
 $null = Control 'Composer'
}
function Close-Hub {
 Invoke-Control 'CloseButton'
 if (-not $script:hubApp.WaitForExit(15000) -or $script:hubApp.ExitCode -ne 0) { throw 'Task window failed to close cleanly' }
 $script:hubApp.Dispose(); $script:hubApp = $null
}
try {
 Start-Hub
 Start-Sleep -Seconds 4
 $legacy = Rooms | Where-Object Id -eq 'background'
 if ($legacy.CodexSession -ne 'legacy-codex' -or $legacy.ClaudeSession -ne 'legacy-claude') { throw 'Opening a legacy conversation reset native sessions before any task was sent' }
 Write-Output 'PASS opening a legacy room preserves native sessions until an explicit task boundary'
 Send 'task-fixture-hold OLD-TASK-MARKER'
 Wait-For { (Tasks | Where-Object RoomId -eq 'background').State -eq 1 } 'Background task was not claimed'
 $progressGeneration = (Tasks | Where-Object RoomId -eq 'background').Generation
 Send 'How is it going?'
 Wait-For { @((Rooms | Where-Object Id -eq 'background').Messages | Where-Object Route -eq 'Task progress').Count -eq 2 } 'Progress answer did not appear'
 $progressTask = Tasks | Where-Object RoomId -eq 'background'
 if ($progressTask.Generation -ne $progressGeneration -or $progressTask.State -ne 1) { throw 'Progress poll interrupted the worker' }
 Write-Output 'PASS a progress question leaves the active worker and generation unchanged'
 Invoke-Control 'NewRoomButton'
 Wait-For { (Control 'RoomTitle').Current.Name -eq 'New conversation' } 'Second conversation did not open'
 Send 'task-fixture-quick'
 Wait-For { (Tasks | Where-Object Objective -eq 'task-fixture-quick').State -eq 0 } 'Independent second task did not finish'
 if (@((Rooms | Where-Object Id -ne 'background').Messages | Where-Object Text -match 'Historical marker included: True').Count -gt 0) { throw 'Background task context leaked to another task' }
 Wait-For { (Tasks | Where-Object RoomId -eq 'background').State -eq 0 } 'Switching rooms stopped background work'
 Wait-For { @((Rooms | Where-Object Id -eq 'background').Messages | Where-Object { $_.Speaker -eq 'Codex' -and $_.Text -like 'Task fixture result*' }).Count -eq 1 } 'Background result was lost or duplicated'
 Write-Output 'PASS independent read tasks continue across conversations and persist into their originating rooms'
 $ledgers = @(Get-ChildItem -LiteralPath $hubData -Filter 'collaboration-*.json' | ForEach-Object { Get-Content -Raw -LiteralPath $_.FullName | ConvertFrom-Json })
 if ($ledgers.Count -ne 2 -or @($ledgers | Where-Object { $_.Entries.Count -lt 1 -or -not $_.Entries[0].SenderSucceeded }).Count -gt 0) { throw 'Desktop did not commit structured task messages' }
 if (@((Rooms | Where-Object Id -eq 'background').Messages | Where-Object { $null -ne $_.Collaboration }).Count -ne 0) { throw 'Routine completion receipts cluttered the chat' }

 Invoke-Control 'TasksButton'
 $dialog = Dialog 'Project tasks and notes'
 if (-not $dialog) { throw 'Task inspector did not open' }
 Invoke-Control 'TaskEvidenceButton' $dialog
 $evidenceDialog = Dialog 'Task evidence and review freshness'
 $evidenceDialog.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close()
 Invoke-Control 'SharedContextButton' $dialog
 $sharedDialog = Dialog 'Shared task context'
 $sharedDialog.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close()
 Value 'TaskNoteInput' 'Saved task keyboard note' $dialog
 Invoke-Control 'AddTaskNoteButton' $dialog
 Wait-For { @((Tasks | Where-Object Objective -eq 'task-fixture-quick').Notes).Count -eq 1 } 'Note was not saved'
 Invoke-Control 'CloseTasksButton' $dialog
 Send 'Continue this task'
 Wait-For { @((Tasks | Where-Object Objective -eq 'task-fixture-quick').LatestReplies.PSObject.Properties | Where-Object Value -match 'Note included: True').Count -gt 0 } 'Saved task note was not supplied to the worker'
 Write-Output 'PASS task inspector saves notes and supplies them on the next turn'

 Invoke-Control 'TasksButton'
 $dialog = Dialog 'Project tasks and notes'
 Invoke-Control 'NewTaskButton' $dialog
 $new = Dialog 'New task'
 Value 'NewTaskObjective' 'task-fixture-isolated' $new
 Invoke-Control 'StartNewTaskButton' $new
 Wait-For { (Tasks | Where-Object Objective -eq 'task-fixture-isolated').State -eq 0 } 'Explicit new task did not finish'
 $isolated = Tasks | Where-Object Objective -eq 'task-fixture-isolated'
 if (@($isolated.LatestReplies.PSObject.Properties | Where-Object { $_.Value -match 'Note included: True|Historical marker included: True' }).Count -ne 0) { throw 'New task inherited unrelated context' }
 Close-Hub; Start-Hub
 if (@(Tasks).Count -ne 3 -or @((Tasks | Where-Object Objective -eq 'task-fixture-quick').Notes).Count -ne 1) { throw 'Restart lost task records or notes' }
 if (@(Tasks | Where-Object State -eq 1).Count -ne 0) { throw 'Restart relaunched work' }
 Write-Output 'PASS explicit task separation and restart preserve memory without starting workers'

 Send 'task-fixture-hold'
 Wait-For { @((Tasks | Where-Object State -eq 1)).Count -eq 1 } 'Stop fixture did not start'
 Invoke-Control 'NewRoomButton'
 Invoke-Control 'StopButton'
 Wait-For { @((Tasks | Where-Object State -eq 1)).Count -eq 0 } 'Stop all left an offscreen worker running'
 Send 'hello'
 Wait-For { $greeting = Tasks | Where-Object Objective -eq 'hello'; $null -ne $greeting -and $greeting.State -eq 0 -and @($greeting.LatestReplies.PSObject.Properties).Count -eq 1 } 'A greeting did not use the single-contribution fast path'
 Write-Output 'PASS a fresh-room greeting creates valid context with one contribution'
 Close-Hub
 Write-Output "PASS Stop all cancels background work; evidence: $hubData"
}
catch { Write-Output $_.ScriptStackTrace; throw }
finally {
 if ($hubApp -and -not $hubApp.HasExited) { $hubApp.CloseMainWindow() | Out-Null; if (-not $hubApp.WaitForExit(15000)) { $hubApp.Kill(); $hubApp.WaitForExit() } }
 $env:AIHUB_DATA_DIR = $hubPreviousData; Remove-Item Env:AIHUB_UI_TEST -ErrorAction SilentlyContinue
}
