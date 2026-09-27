param([Parameter(Mandatory=$true)][string]$AppDirectory)
$ErrorActionPreference='Stop'
$hubRoot=Split-Path $PSScriptRoot -Parent
$hubSource=Join-Path $env:LOCALAPPDATA 'AIHub'
$hubClone=Join-Path $hubRoot ('artifacts\context-upgrade-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $hubClone | Out-Null
$hubHashes=@{}
foreach($hubFile in Get-ChildItem -LiteralPath $hubSource -Filter '*.json' -File) {
 $hubHashes[$hubFile.FullName]=(Get-FileHash -LiteralPath $hubFile.FullName).Hash
 Copy-Item -LiteralPath $hubFile.FullName -Destination $hubClone
}
$hubRoomsBefore=Get-Content -Raw -LiteralPath (Join-Path $hubClone 'rooms.json') | ConvertFrom-Json
$hubTaskBefore=Get-Content -Raw -LiteralPath (Join-Path $hubClone 'tasks.json') | ConvertFrom-Json
$hubPrevious=$env:AIHUB_DATA_DIR
$hubProcess=$null
try {
 $env:AIHUB_DATA_DIR=$hubClone
 $hubProcess=Start-Process -FilePath (Join-Path $AppDirectory 'AI Hub.exe') -WindowStyle Hidden -PassThru
 $hubDeadline=(Get-Date).AddSeconds(20)
 do { Start-Sleep -Milliseconds 200; $hubProcess.Refresh() } until($hubProcess.MainWindowHandle -ne 0 -or $hubProcess.HasExited -or (Get-Date) -gt $hubDeadline)
 if ($hubProcess.HasExited -or $hubProcess.MainWindowHandle -eq 0) { throw 'Upgraded profile did not open' }
 Start-Sleep -Seconds 2
 if (-not $hubProcess.CloseMainWindow() -or -not $hubProcess.WaitForExit(15000) -or $hubProcess.ExitCode -ne 0) { throw 'Upgraded profile did not close cleanly' }
 $hubRoomsAfter=Get-Content -Raw -LiteralPath (Join-Path $hubClone 'rooms.json') | ConvertFrom-Json
 $hubTasksAfter=Get-Content -Raw -LiteralPath (Join-Path $hubClone 'tasks.json') | ConvertFrom-Json
 if (@($hubRoomsBefore).Count -ne @($hubRoomsAfter).Count -or @($hubTaskBefore).Count -ne @($hubTasksAfter).Count) { throw 'Upgrade changed room or task counts' }
 foreach($hubRoom in $hubRoomsBefore) {
  $hubRestored=@($hubRoomsAfter | Where-Object Id -eq $hubRoom.Id)[0]
  if ($null -eq $hubRestored -or $hubRestored.Draft -ne $hubRoom.Draft -or @($hubRestored.Messages).Count -ne @($hubRoom.Messages).Count) { throw 'Upgrade lost transcript or draft data' }
 }
 if (@($hubTasksAfter | Where-Object State -eq 1).Count -gt 0) { throw 'Upgrade started model work' }
 foreach($hubPath in $hubHashes.Keys) { if ((Get-FileHash -LiteralPath $hubPath).Hash -ne $hubHashes[$hubPath]) { throw 'Production data changed during isolated upgrade check' } }
 [pscustomobject]@{Profile=$hubClone;Rooms=@($hubRoomsAfter).Count;Tasks=@($hubTasksAfter).Count;ProductionData='unchanged';StartedWorkers=$false} | ConvertTo-Json | Set-Content -Encoding UTF8 -LiteralPath (Join-Path $hubClone 'upgrade-result.json')
 Write-Output "PASS copied production profile opens and closes with transcripts/drafts/tasks preserved, no model work, and original data unchanged: $hubClone"
}
finally {
 if ($hubProcess -and -not $hubProcess.HasExited) { $hubProcess.CloseMainWindow() | Out-Null; if (-not $hubProcess.WaitForExit(15000)) { throw 'Isolated upgrade window still running' } }
 $env:AIHUB_DATA_DIR=$hubPrevious
}
