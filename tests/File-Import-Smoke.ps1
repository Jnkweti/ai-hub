param([Parameter(Mandatory=$true)][string]$AppDirectory)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$hubRoot=Split-Path $PSScriptRoot -Parent
$hubFixture=Join-Path $hubRoot ('artifacts\file-import-ui-' + [Guid]::NewGuid().ToString('N'))
$hubData=Join-Path $hubFixture 'data'
$hubProject=Join-Path $hubFixture 'project'
New-Item -ItemType Directory -Path $hubData,$hubProject | Out-Null
$hubSource=Join-Path $hubFixture 'meeting notes.txt'
[IO.File]::WriteAllText($hubSource,'Transcript import fixture: exact bytes retained.')
[IO.File]::WriteAllText((Join-Path $hubProject 'meeting notes.txt'),'Existing file must stay.')
@{Workspace=$hubProject;LastRoomId='import-room';AllowEdits=$false;AutoExchange=$false;ReduceMotion=$true} | ConvertTo-Json | Set-Content -Encoding UTF8 (Join-Path $hubData 'settings.json')
ConvertTo-Json -Depth 8 -InputObject @(@{Id='import-room';Title='File import verification';Workspace=$hubProject;Target='Both';Draft='Please analyze this transcript.';Messages=@()}) | Set-Content -Encoding UTF8 (Join-Path $hubData 'rooms.json')
$hubPrevious=$env:AIHUB_DATA_DIR
$hubProcess=$null
function Window([string]$name) {
 $condition=[System.Windows.Automation.AndCondition]::new([System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ProcessIdProperty,$hubProcess.Id),[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,$name))
 $deadline=(Get-Date).AddSeconds(20)
 do { $found=[System.Windows.Automation.AutomationElement]::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$condition); if (-not $found) { Start-Sleep -Milliseconds 100 } } until($found -or (Get-Date) -gt $deadline)
 if (-not $found) { throw "Missing window $name" }; return $found
}
function Control($root,[string]$id) {
 $condition=[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty,$id)
 $found=$root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$condition)
 if (-not $found) { throw "Missing control $id" }; return $found
}
function Invoke($root,[string]$id) { (Control $root $id).GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
try {
 $env:AIHUB_DATA_DIR=$hubData
 $hubProcess=Start-Process -FilePath (Join-Path $AppDirectory 'AI Hub.exe') -WindowStyle Hidden -PassThru
 $main=Window 'AI Hub'
 Invoke $main 'ImportFilesButton'
 $dialog=Window 'Import files into the current project folder'
 $filename=Control $dialog '1148'
 $filename.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($hubSource)
 Invoke $dialog '1'
 $deadline=(Get-Date).AddSeconds(15)
 do { Start-Sleep -Milliseconds 150; $draft=(Control $main 'Composer').GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value } until($draft.Contains('meeting notes (1).txt') -or (Get-Date) -gt $deadline)
 if (-not $draft.Contains('meeting notes (1).txt') -or -not $draft.Contains('Please analyze this transcript.')) { throw 'Import did not preserve draft and add file reference' }
 if ((Get-FileHash -LiteralPath $hubSource).Hash -ne (Get-FileHash -LiteralPath (Join-Path $hubProject 'meeting notes (1).txt')).Hash) { throw 'Imported bytes changed' }
 if ([IO.File]::ReadAllText((Join-Path $hubProject 'meeting notes.txt')) -ne 'Existing file must stay.') { throw 'Existing file overwritten' }
 Invoke $main 'CloseButton'
 if (-not $hubProcess.WaitForExit(15000)) { throw 'Import app did not close' }
 $saved=Get-Content -Raw (Join-Path $hubData 'rooms.json') | ConvertFrom-Json
 if ($saved[0].Messages.Count -ne 0 -or -not $saved[0].Draft.Contains('meeting notes (1).txt')) { throw 'Import dispatched work or lost draft' }
 $hubProcess=Start-Process -FilePath (Join-Path $AppDirectory 'AI Hub.exe') -WindowStyle Hidden -PassThru
 $main=Window 'AI Hub'
 $restored=(Control $main 'Composer').GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value
 if ($restored -ne $draft) { throw 'Restart lost imported draft' }
 Invoke $main 'CloseButton'
 if (-not $hubProcess.WaitForExit(15000)) { throw 'Restart did not close' }
 Write-Output "PASS import picker copies exact bytes, preserves collision and draft, survives restart, and starts no model work: $hubFixture"
}
finally {
 if ($hubProcess -and -not $hubProcess.HasExited) { $hubProcess.CloseMainWindow() | Out-Null; if (-not $hubProcess.WaitForExit(10000)) { $hubProcess.Kill(); $hubProcess.WaitForExit() } }
 $env:AIHUB_DATA_DIR=$hubPrevious
}
