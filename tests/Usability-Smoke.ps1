param([string]$AppDirectory = '')
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$hubRoot = Split-Path $PSScriptRoot -Parent
if (-not $AppDirectory) { $AppDirectory = Join-Path $hubRoot 'app' }
$hubData = Join-Path $hubRoot ('artifacts\usability-smoke-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $hubData -Force | Out-Null
$hubRoomSeed = @{Id='draft-room';Title='Draft persistence';Workspace=$hubRoot;LastTask='Review the project';Target='Claude';Messages=@()}
ConvertTo-Json -InputObject @($hubRoomSeed) -Depth 6 | Set-Content -Encoding UTF8 -LiteralPath (Join-Path $hubData 'rooms.json')
$env:AIHUB_DATA_DIR = $hubData; $env:AIHUB_UI_TEST = '1'
function Start-Hub {
    $script:hubApp = Start-Process -FilePath (Join-Path $AppDirectory 'AI Hub.exe') -WindowStyle Hidden -PassThru
    $deadline = (Get-Date).AddSeconds(15)
    do { Start-Sleep -Milliseconds 100; $hubApp.Refresh() } until ($hubApp.MainWindowHandle -ne 0 -or $hubApp.HasExited -or (Get-Date) -gt $deadline)
    if ($hubApp.MainWindowHandle -eq 0) { throw 'No app window' }
    # Let WPF attach its automation provider before FromHandle caches the HWND.
    Start-Sleep -Milliseconds 1000
    $script:hubWindow = [System.Windows.Automation.AutomationElement]::FromHandle($hubApp.MainWindowHandle)
    Start-Sleep -Milliseconds 250
}
function Control([string]$id, $root = $hubWindow) {
    $condition = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty,$id)
    $controlDeadline = (Get-Date).AddSeconds(5)
    do {
        $control = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$condition)
        if ($null -eq $control) { Start-Sleep -Milliseconds 75 }
    } until ($null -ne $control -or (Get-Date) -gt $controlDeadline)
    if ($null -eq $control) { throw "Control not found: $id" }; return $control
}
function Invoke-Control([string]$id, $root = $hubWindow) { (Control $id $root).GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
function ComposerValue { return (Control 'Composer').GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern) }
function Close-Hub {
    Invoke-Control 'CloseButton'
    if (-not $hubApp.WaitForExit(10000) -or $hubApp.ExitCode -ne 0) { throw 'App failed to close' }
    $hubApp.Dispose()
    $script:hubApp = $null
}
Start-Hub
try {
    if ((Control 'SendButton').Current.IsEnabled) { throw 'Empty composer has an enabled Send action' }
    $hubDraft = "A draft that must survive.`r`nIt has a second line."
    (ComposerValue).SetValue($hubDraft)
    if (-not (Control 'SendButton').Current.IsEnabled) { throw 'Typing did not enable Send' }
    Invoke-Control 'NewRoomButton'; Start-Sleep -Milliseconds 350
    if ((ComposerValue).Current.Value -ne '') { throw 'New conversation inherited another room draft' }
    $hubItemCondition = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::ListItem)
    $hubItems = (Control 'RoomList').FindAll([System.Windows.Automation.TreeScope]::Children,$hubItemCondition)
    $hubItems[1].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Start-Sleep -Milliseconds 350
    if ((ComposerValue).Current.Value -ne $hubDraft) { throw 'Conversation switching lost the draft' }
    $hubSelectedTarget = (Control 'Target').GetCurrentPattern([System.Windows.Automation.SelectionPattern]::Pattern).Current.GetSelection()[0].Current.Name
    if ($hubSelectedTarget -ne 'Claude only') { throw 'Conversation switching lost the recipient' }
    Write-Output 'PASS per-conversation drafts, recipients, and empty-send feedback'
    Invoke-Control 'StopButton'; Start-Sleep -Milliseconds 250
    if ((Control 'ContinueButton').Current.IsOffscreen) { throw 'Stopped task has no visible continuation action' }
    Invoke-Control 'ContinueButton'; Start-Sleep -Milliseconds 200
    if ((ComposerValue).Current.Value -ne $hubDraft) { throw 'Continue overwrote the existing draft' }
    Write-Output 'PASS visible pause action preserves unsent text'
    Close-Hub; Start-Hub
    if ((ComposerValue).Current.Value -ne $hubDraft) { throw 'App restart lost the last room or draft' }
    Write-Output 'PASS restart restores the selected room and draft'
    Invoke-Control 'SettingsButton'; Start-Sleep -Milliseconds 350
    $hubWindowCondition = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::Window)
    $hubSettings = $hubWindow.FindAll([System.Windows.Automation.TreeScope]::Descendants,$hubWindowCondition) | Where-Object { $_.Current.Name -like '*Settings*' } | Select-Object -First 1
    if ($null -eq $hubSettings) { throw 'Settings window missing' }
    $hubSettings.GetCurrentPattern([System.Windows.Automation.TransformPattern]::Pattern).Resize(600,480)
    if ((Control 'SaveSettingsButton' $hubSettings).Current.IsOffscreen) { throw 'Settings save action disappears in a small window' }
    $hubRounds = (Control 'MaxRoundsInput' $hubSettings).GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
    $hubRounds.SetValue('0'); Invoke-Control 'SaveSettingsButton' $hubSettings; Start-Sleep -Milliseconds 200
    if ($hubRounds.Current.Value -ne '0') { throw 'Invalid round limit was not left for correction' }
    $hubRounds.SetValue('3'); Invoke-Control 'SaveSettingsButton' $hubSettings; Start-Sleep -Milliseconds 250
    $hubSaved = Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $hubData 'settings.json') | ConvertFrom-Json
    if ($hubSaved.MaxAutoRounds -ne 3) { throw 'Round-limit setting was not saved' }
    Write-Output 'PASS compact settings and round-limit validation'
    Close-Hub
    Write-Output "Isolated usability evidence: $hubData"
}
finally {
    if ($null -ne $hubApp -and -not $hubApp.HasExited) { $hubApp.CloseMainWindow() | Out-Null; $hubApp.WaitForExit(5000) | Out-Null }
}
