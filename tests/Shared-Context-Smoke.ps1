param([string]$AppDirectory, [string]$NativeResultDirectory)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$hubRoot = Split-Path $PSScriptRoot -Parent
if (-not $NativeResultDirectory -or -not (Test-Path -LiteralPath (Join-Path $NativeResultDirectory 'data'))) {
 throw "This check replays a real shared-context run: pass -NativeResultDirectory <dir> produced by 'AIHub.Tests.exe --shared-context-live <dir>' (its data folder is copied into an isolated profile)."
}
$hubData = Join-Path $hubRoot ('artifacts\shared-context-ui-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $hubData | Out-Null
Get-ChildItem -LiteralPath (Join-Path $NativeResultDirectory 'data') -Filter '*.json' | Copy-Item -Destination $hubData
$hubTasks = Get-Content -Raw -LiteralPath (Join-Path $hubData 'tasks.json') | ConvertFrom-Json
$hubTask = $hubTasks[0]
@{Workspace=$hubTask.Workspace;LastRoomId=$hubTask.RoomId;AllowEdits=$false;AutoExchange=$false;ReduceMotion=$true} | ConvertTo-Json | Set-Content -Encoding UTF8 -LiteralPath (Join-Path $hubData 'settings.json')
ConvertTo-Json -Depth 8 -InputObject @(@{Id=$hubTask.RoomId;Title='Shared context verification';Workspace=$hubTask.Workspace;Target='Both';ActiveTaskId=$hubTask.Id;Messages=@()}) | Set-Content -Encoding UTF8 -LiteralPath (Join-Path $hubData 'rooms.json')
$hubPrevious = $env:AIHUB_DATA_DIR
$env:AIHUB_DATA_DIR = $hubData; $env:AIHUB_UI_TEST = '1'
$hubProcess = $null
function Find-Control($root,[string]$id) {
 $condition = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty,$id)
 $deadline=(Get-Date).AddSeconds(20)
 do { $found=$root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$condition); if (-not $found) { Start-Sleep -Milliseconds 100 } } until ($found -or (Get-Date) -gt $deadline)
 if (-not $found) { throw "Missing control: $id" }; return $found
}
function Invoke-Control($root,[string]$id) { (Find-Control $root $id).GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
function Window([string]$name) {
 $condition=[System.Windows.Automation.AndCondition]::new(
  [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ProcessIdProperty,$hubProcess.Id),
  [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,$name))
 $deadline=(Get-Date).AddSeconds(20)
 do { $found=[System.Windows.Automation.AutomationElement]::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$condition); if (-not $found) { Start-Sleep -Milliseconds 100 } } until ($found -or (Get-Date) -gt $deadline)
 if (-not $found) { throw "Missing window: $name" }; return $found
}
try {
 $hubProcess=Start-Process -FilePath (Join-Path $AppDirectory 'AI Hub.exe') -WindowStyle Hidden -PassThru
 $main=Window 'AI Hub'
 Invoke-Control $main 'TasksButton'
 $tasks=Window 'Project tasks and notes'
 Invoke-Control $tasks 'SharedContextButton'
 $shared=Window 'Shared task context'
 $textBox=Find-Control $shared 'SharedContextReport'
 $text=$textBox.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value
 foreach ($expected in @('Codex','Claude','submit-note','save-note','Current files','frontend/view.txt','backend/store.txt','Recent exact host inputs','Common hash:')) {
  if (-not $text.Contains($expected)) { throw "Shared context window missing $expected" }
 }
 $text | Set-Content -Encoding UTF8 -LiteralPath (Join-Path $hubData 'window-text.txt')
 $inputBox=Find-Control $shared 'SharedInstructionInput'
 $inputBox.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('UI pin original')
 Invoke-Control $shared 'PinInstructionButton'
 Start-Sleep -Milliseconds 800
 $inputBox.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('UI pin replacement')
 Invoke-Control $shared 'ReplaceInstructionButton'
 Start-Sleep -Milliseconds 800
 $updated=$textBox.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value
 if (-not $updated.Contains('UI pin replacement') -or -not $updated.Contains('Superseded instructions (historical):')) { throw 'Pin or replacement did not update the shared window' }
 $ledger=Get-Content -Raw -LiteralPath (Join-Path $hubData ('collaboration-' + $hubTask.Id + '.json')) | ConvertFrom-Json
 $old=@($ledger.ContextRecords | Where-Object Text -eq 'UI pin original')[0]
 $replacement=@($ledger.ContextRecords | Where-Object Text -eq 'UI pin replacement')[0]
 if (-not $old -or $replacement.Supersedes -ne $old.Id) { throw 'Supersession was not persisted' }
 $shared.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close()
 Invoke-Control $tasks 'CloseTasksButton'
 Invoke-Control $main 'CloseButton'
 if (-not $hubProcess.WaitForExit(15000) -or $hubProcess.ExitCode -ne 0) { throw 'App did not close cleanly' }
 $hubProcess=Start-Process -FilePath (Join-Path $AppDirectory 'AI Hub.exe') -WindowStyle Hidden -PassThru
 $main=Window 'AI Hub'
 Invoke-Control $main 'TasksButton'
 $tasks=Window 'Project tasks and notes'
 Invoke-Control $tasks 'SharedContextButton'
 $shared=Window 'Shared task context'
 $restored=(Find-Control $shared 'SharedContextReport').GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value
 if (-not $restored.Contains('UI pin replacement') -or -not $restored.Contains('UI pin original')) { throw 'Restart lost instruction history' }
 $shared.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close()
 Invoke-Control $tasks 'CloseTasksButton'
 Invoke-Control $main 'CloseButton'
 if (-not $hubProcess.WaitForExit(15000) -or $hubProcess.ExitCode -ne 0) { throw 'Restarted app did not close cleanly' }
 Write-Output "PASS shared context displays native findings, freshness, exact inputs, and durable pin/supersession; profile: $hubData"
}
finally {
 if ($hubProcess -and -not $hubProcess.HasExited) { $hubProcess.CloseMainWindow() | Out-Null; if (-not $hubProcess.WaitForExit(15000)) { $hubProcess.Kill(); $hubProcess.WaitForExit() } }
 $env:AIHUB_DATA_DIR=$hubPrevious; Remove-Item Env:AIHUB_UI_TEST -ErrorAction SilentlyContinue
}
