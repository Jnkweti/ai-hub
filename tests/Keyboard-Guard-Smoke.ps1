param([string]$AppDirectory = '', [switch]$Live)
# Actual Windows key input in an isolated room; fixtures unless -Live is selected.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Windows.Forms
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class HubKeyboardWindow {
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr handle);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr handle, int command);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
}
'@
$hubRoot = Split-Path $PSScriptRoot -Parent
if (-not $AppDirectory) { $AppDirectory = Join-Path $hubRoot 'app' }
$hubData = Join-Path $hubRoot ('artifacts\keyboard-smoke-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $hubData -Force | Out-Null
$hubFixture = Join-Path $PSScriptRoot 'AIHub.Tests\bin\Release\net10.0\AIHub.Tests.exe'
if (-not (Test-Path -LiteralPath $hubFixture)) { throw 'Build the AIHub.Tests project first.' }
$hubProviderPath = if ($Live) { '' } else { $hubFixture }
@{ Workspace=$hubRoot; CodexPath=$hubProviderPath; ClaudePath=$hubProviderPath; AutoExchange=$true; AllowEdits=$false; MaxAutoRounds=6; ReduceMotion=$true } | ConvertTo-Json | Set-Content -Encoding UTF8 -LiteralPath (Join-Path $hubData 'settings.json')
$env:AIHUB_DATA_DIR = $hubData; $env:AIHUB_UI_TEST = '1'
$hubApp = Start-Process -FilePath (Join-Path $AppDirectory 'AI Hub.exe') -WindowStyle Hidden -PassThru
try {
    $hubDeadline = (Get-Date).AddSeconds(15)
    do { Start-Sleep -Milliseconds 100; $hubApp.Refresh() } until ($hubApp.MainWindowHandle -ne 0 -or $hubApp.HasExited -or (Get-Date) -gt $hubDeadline)
    if ($hubApp.MainWindowHandle -eq 0) { throw 'Window not found.' }
    $hubWindow = [System.Windows.Automation.AutomationElement]::FromHandle($hubApp.MainWindowHandle)
    function Find-Control([string]$id) {
        $condition = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty,$id)
        $controlDeadline = (Get-Date).AddSeconds(5)
        do {
            $result = $hubWindow.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$condition)
            if ($null -eq $result) { Start-Sleep -Milliseconds 75 }
        } until ($null -ne $result -or (Get-Date) -gt $controlDeadline)
        if ($null -eq $result) { throw "Missing control: $id" }; return $result
    }
    $composer = Find-Control 'Composer'
    $value = $composer.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
    [HubKeyboardWindow]::ShowWindow($hubApp.MainWindowHandle,9) | Out-Null
    [HubKeyboardWindow]::SetForegroundWindow($hubApp.MainWindowHandle) | Out-Null
    $composer.SetFocus(); Start-Sleep -Milliseconds 150
    if ([HubKeyboardWindow]::GetForegroundWindow() -ne $hubApp.MainWindowHandle) { throw 'Cannot target keyboard input safely: test window is not foreground.' }
    $value.SetValue('hello')
    [System.Windows.Forms.SendKeys]::SendWait('^{END}+{ENTER}')
    Start-Sleep -Milliseconds 200
    if ($value.Current.Value -notmatch "hello\r?\n") { throw 'Shift+Enter did not insert a newline.' }
    if ($value.Current.Value -eq '') { throw 'Shift+Enter submitted the message.' }
    Write-Output 'PASS Shift+Enter inserts a newline'
    [System.Windows.Forms.SendKeys]::SendWait('{ENTER}')
    $hubDeadline = (Get-Date).AddSeconds($(if ($Live) { 60 } else { 12 }))
    do {
        Start-Sleep -Milliseconds 150
        $hubPaused = (Find-Control 'StateLabel').Current.Name -like 'Paused*'
    } until ($hubPaused -or $hubApp.HasExited -or (Get-Date) -gt $hubDeadline)
    if (-not $hubPaused) { throw 'Enter did not submit and reach the greeting guard.' }
    if ($value.Current.Value -ne '') { throw 'Composer did not clear after Enter.' }
    $hubRooms = Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $hubData 'rooms.json') | ConvertFrom-Json
    $hubMessages = $hubRooms[0].Messages
    if (@($hubMessages | Where-Object Speaker -eq 'You').Count -ne 1) { throw 'Enter submitted zero or multiple messages.' }
    if (@($hubMessages | Where-Object Speaker -eq 'AI Hub').Count -ne 1) { throw 'No visible explanation of the automatic pause.' }
    $hubLogPath = Join-Path $hubData ('activity-' + $hubRooms[0].Id + '.jsonl')
    $hubEvents = Get-Content -Encoding UTF8 -LiteralPath $hubLogPath | ForEach-Object { $_ | ConvertFrom-Json }
    if (@($hubEvents | Where-Object { $_.from -in 'Claude','Codex' }).Count -ne 0) { throw 'Greeting started agent-to-agent handoffs.' }
    Write-Output 'PASS Enter sends exactly once and the greeting guard prevents handoffs'
    (Find-Control 'CloseButton').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    if (-not $hubApp.WaitForExit(15000) -or $hubApp.ExitCode -ne 0) { throw 'Keyboard test did not close cleanly.' }
    Write-Output "Isolated keyboard evidence: $hubData"
}
finally {
    if (-not $hubApp.HasExited) { $hubApp.CloseMainWindow() | Out-Null; $hubApp.WaitForExit(10000) | Out-Null }
    $hubApp.Dispose()
}
