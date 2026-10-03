param([string]$AppDirectory = '')
# Offline checks through the actual redesigned window. Test data stays isolated.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$hubRoot = Split-Path $PSScriptRoot -Parent
if (-not $AppDirectory) { $AppDirectory = Join-Path $hubRoot 'app' }
$hubData = Join-Path $hubRoot ('artifacts\appearance-smoke-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $hubData -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'fixtures\visual-room.json') -Destination (Join-Path $hubData 'rooms.json')
$env:AIHUB_DATA_DIR = $hubData; $env:AIHUB_UI_TEST = '1'
$hubApp = Start-Process -FilePath (Join-Path $AppDirectory 'AI Hub.exe') -WindowStyle Hidden -PassThru
try {
    $hubDeadline = (Get-Date).AddSeconds(15)
    do { Start-Sleep -Milliseconds 150; $hubApp.Refresh() } until ($hubApp.MainWindowHandle -ne 0 -or $hubApp.HasExited -or (Get-Date) -gt $hubDeadline)
    if ($hubApp.MainWindowHandle -eq 0) { throw 'AI Hub window was not available.' }
    $hubWindow = [System.Windows.Automation.AutomationElement]::FromHandle($hubApp.MainWindowHandle)
    function Find-Control([string]$id, $root = $hubWindow) {
        $condition = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id)
        $controlDeadline = (Get-Date).AddSeconds(5)
        do {
            $element = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
            if ($null -eq $element) { Start-Sleep -Milliseconds 75 }
        } until ($null -ne $element -or (Get-Date) -gt $controlDeadline)
        if ($null -eq $element) { throw "Control not found: $id" }
        return $element
    }
    function Invoke-Control([string]$id, $root = $hubWindow) { (Find-Control $id $root).GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
    Start-Sleep -Milliseconds 700
    $hubOriginalChatWidth = (Find-Control 'ChatScroll').Current.BoundingRectangle.Width
    Invoke-Control 'ActivityToggle'
    Start-Sleep -Milliseconds 450
    if ((Find-Control 'ChatScroll').Current.BoundingRectangle.Width -lt ($hubOriginalChatWidth + 200)) { throw 'Activity rail did not collapse.' }
    Invoke-Control 'ActivityToggle'
    Start-Sleep -Milliseconds 450
    if ([Math]::Abs((Find-Control 'ChatScroll').Current.BoundingRectangle.Width - $hubOriginalChatWidth) -gt 3) { throw 'Activity rail did not return to its original width.' }
    Write-Output 'PASS activity collapse and expand'
    $hubWindowPattern = $hubWindow.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern)
    Invoke-Control 'MaximizeButton'
    Start-Sleep -Milliseconds 250
    if ($hubWindowPattern.Current.WindowVisualState -ne 'Maximized') { throw 'Custom maximize control failed.' }
    Invoke-Control 'MaximizeButton'
    Start-Sleep -Milliseconds 250
    if ($hubWindowPattern.Current.WindowVisualState -ne 'Normal') { throw 'Custom restore control failed.' }
    Write-Output 'PASS custom maximize and restore'
    $hubTransform = $hubWindow.GetCurrentPattern([System.Windows.Automation.TransformPattern]::Pattern)
    $hubTransform.Resize(1080, 700)
    Start-Sleep -Milliseconds 300
    if ((Find-Control 'SendButton').Current.IsOffscreen -or (Find-Control 'StopButton').Current.IsOffscreen) { throw 'Primary actions disappeared at the minimum window size.' }
    $hubTransform.Resize(1500, 960)
    Write-Output 'PASS compact layout keeps Send and Stop available'
    Invoke-Control 'SettingsButton'
    $hubDialogDeadline = (Get-Date).AddSeconds(5)
    do {
        Start-Sleep -Milliseconds 200
        $hubWindowCondition = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Window)
        $hubSettingsWindow = $hubWindow.FindAll([System.Windows.Automation.TreeScope]::Descendants, $hubWindowCondition) | Where-Object { $_.Current.Name -like '*Settings*' } | Select-Object -First 1
    } until ($null -ne $hubSettingsWindow -or (Get-Date) -gt $hubDialogDeadline)
    if ($null -eq $hubSettingsWindow) { throw 'Settings window was not found.' }
    (Find-Control 'ReduceMotionToggle' $hubSettingsWindow).GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()
    Invoke-Control 'SaveSettingsButton' $hubSettingsWindow
    Start-Sleep -Milliseconds 350
    $hubConfig = Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $hubData 'settings.json') | ConvertFrom-Json
    if (-not $hubConfig.ReduceMotion) { throw 'Reduced motion was not persisted.' }
    Write-Output 'PASS reduced motion setting'
    (Find-Control 'Composer').GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('Draft text stays in the command box.')
    if ((Find-Control 'Composer').GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value -ne 'Draft text stays in the command box.') { throw 'Composer did not accept text.' }
    Invoke-Control 'CloseButton'
    if (-not $hubApp.WaitForExit(15000) -or $hubApp.ExitCode -ne 0) { throw 'Custom close control failed.' }
    Write-Output 'PASS composer and custom close'
    Write-Output "Isolated appearance data: $hubData"
}
finally {
    if (-not $hubApp.HasExited) { $hubApp.CloseMainWindow() | Out-Null; $hubApp.WaitForExit(15000) | Out-Null }
    $hubApp.Dispose()
}
