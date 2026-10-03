param([string]$AppDirectory = '')
# Exercises the actual desktop controls and real CLI connections in an isolated room.
# This makes short model calls; run deliberately, separately from the offline suite.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$hubRoot = Split-Path $PSScriptRoot -Parent
if (-not $AppDirectory) { $AppDirectory = Join-Path $hubRoot 'app' }
$hubData = Join-Path $hubRoot ('artifacts\desktop-smoke-' + [Guid]::NewGuid().ToString('N'))
$env:AIHUB_DATA_DIR = $hubData; $env:AIHUB_UI_TEST = '1'
$hubApp = Start-Process -FilePath (Join-Path $AppDirectory 'AI Hub.exe') -WindowStyle Hidden -PassThru
try {
    $hubDeadline = (Get-Date).AddSeconds(15)
    do {
        Start-Sleep -Milliseconds 150
        $hubApp.Refresh()
        if ($hubApp.HasExited) { throw 'Desktop closed before the smoke test started.' }
    } until ($hubApp.MainWindowHandle -ne 0 -or (Get-Date) -gt $hubDeadline)
    $hubWindow = [System.Windows.Automation.AutomationElement]::FromHandle($hubApp.MainWindowHandle)
    function Find-Control([string]$id) {
        $condition = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id)
        $controlDeadline = (Get-Date).AddSeconds(5)
        do {
            $element = $hubWindow.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
            if ($null -eq $element) { Start-Sleep -Milliseconds 75 }
        } until ($null -ne $element -or (Get-Date) -gt $controlDeadline)
        if ($null -eq $element) { throw "Desktop control not found: $id" }
        return $element
    }
    $composer = Find-Control 'Composer'
    $value = $composer.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
    $value.SetValue('Perform a structured communication check with your colleague for 20 rounds, subject to the Hub round limit. On each turn read get_task_context, then submit one handoff to the other selected agent with summary AI Hub connected: YOUR_AGENT_NAME step N, requested_action to continue this communication check, scope files [] and focus [], evidence_refs [], blockers [], and reply_to the incoming message ID when one exists. Use schema_version 1.0 and a unique idempotency_key. After acceptance reply briefly with AI Hub connected: YOUR_AGENT_NAME step N. Use only ai_hub tools; do not read or modify files, run commands, delegate, or use external tools. Stop when interrupted by the user.')
    $send = Find-Control 'SendButton'
    $send.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    $hubDeadline = (Get-Date).AddSeconds(240)
    $hubReady = $false
    do {
        Start-Sleep -Milliseconds 300
        $hubRoomFile = Join-Path $hubData 'rooms.json'
        if (Test-Path -LiteralPath $hubRoomFile) {
            $hubRooms = Get-Content -Raw -Encoding UTF8 -LiteralPath $hubRoomFile | ConvertFrom-Json
            $hubRoom = @($hubRooms)[0]
            $hubCodexCount = @($hubRoom.Messages | Where-Object { $_.Speaker -eq 'Codex' -and $_.Complete -and $_.Text -match 'AI Hub connected' }).Count
            $hubClaudeCount = @($hubRoom.Messages | Where-Object { $_.Speaker -eq 'Claude' -and $_.Complete -and $_.Text -match 'AI Hub connected' }).Count
            $hubReady = $hubCodexCount -ge 2 -and $hubClaudeCount -ge 2
        }
    } until ($hubReady -or $hubApp.HasExited -or (Get-Date) -gt $hubDeadline)
    if (-not $hubReady) { throw "Both agent-to-agent directions were not observed. Inspect $hubData" }
    (Find-Control 'StopButton').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    $hubDeadline = (Get-Date).AddSeconds(15)
    do {
        Start-Sleep -Milliseconds 150
        $hubStopped = (Find-Control 'CodexStatus').Current.Name -match 'Stopped' -and (Find-Control 'ClaudeStatus').Current.Name -match 'Stopped'
    } until ($hubStopped -or (Get-Date) -gt $hubDeadline)
    if (-not $hubStopped) { throw 'Stop did not update both agent controls.' }
    $hubActivityPath = Join-Path $hubData ('activity-' + $hubRoom.Id + '.jsonl')
    $hubActivity = Get-Content -Encoding UTF8 -LiteralPath $hubActivityPath | ForEach-Object { $_ | ConvertFrom-Json }
    if (-not ($hubActivity | Where-Object { $_.from -eq 'Claude' -and $_.to -eq 'Codex' })) { throw 'Missing Claude to Codex handoff.' }
    if (-not ($hubActivity | Where-Object { $_.from -eq 'Codex' -and $_.to -eq 'Claude' })) { throw 'Missing Codex to Claude handoff.' }
    $hubWindow.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close()
    if (-not $hubApp.WaitForExit(15000) -or $hubApp.ExitCode -ne 0) { throw 'Desktop did not close cleanly.' }
    Write-Output 'PASS real desktop send, both replies, automatic handoffs in both directions, Stop, and close'
    Write-Output "Isolated evidence: $hubData"
}
finally {
    if (-not $hubApp.HasExited) {
        $hubApp.CloseMainWindow() | Out-Null
        $hubApp.WaitForExit(15000) | Out-Null
    }
    $hubApp.Dispose()
}
