param([string]$AppDirectory = '', [string]$FixturePath = '')
# Offline native UI checks with both provider protocols. Never opens a real agent session.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class InputWindow {
 [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
 [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);
 [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
 [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr dc, uint f);
 [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr h);
}
'@
$hubRoot = Split-Path $PSScriptRoot -Parent
if (-not $AppDirectory) { $AppDirectory = Join-Path $hubRoot 'app' }
$hubData = Join-Path $hubRoot ('artifacts\input-smoke-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $hubData -Force | Out-Null
$hubFixture = Join-Path $PSScriptRoot 'AIHub.Tests\bin\Release\net10.0\AIHub.Tests.exe'
if ($FixturePath) { $hubFixture = $FixturePath }
@{Workspace=$hubData;LastRoomId='input';CodexPath=$hubFixture;ClaudePath=$hubFixture;AllowEdits=$false;AutoExchange=$true;ReduceMotion=$true} | ConvertTo-Json | Set-Content -Encoding UTF8 -LiteralPath (Join-Path $hubData 'settings.json')
ConvertTo-Json -Depth 8 -InputObject @(@{Id='input';Title='Input check';Workspace=$hubData;Messages=@()}) | Set-Content -Encoding UTF8 -LiteralPath (Join-Path $hubData 'rooms.json')
$hubPreviousData = $env:AIHUB_DATA_DIR
$env:AIHUB_DATA_DIR = $hubData
$hubApp = $null
function Wait-For([scriptblock]$condition,[string]$failure) {
 $deadline = (Get-Date).AddSeconds(15)
 do { if (& $condition) { return }; Start-Sleep -Milliseconds 100 } until ((Get-Date) -gt $deadline)
 throw $failure
}
function Id-Condition([string]$id) { return [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty,$id) }
function Control([string]$id,$root=$script:hubWindow) {
 $deadline = (Get-Date).AddSeconds(5)
 do { $element = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,(Id-Condition $id)); if (-not $element) { Start-Sleep -Milliseconds 75 } } until ($element -or (Get-Date) -gt $deadline)
 if (-not $element) { throw "Missing control: $id" }; return $element
}
function Invoke-Control([string]$id,$root=$script:hubWindow) { (Control $id $root).GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
function Value { return (Control 'Composer').GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern) }
function Card([string]$agent) {
 return $script:hubWindow.FindAll([System.Windows.Automation.TreeScope]::Descendants,(Id-Condition 'AgentRequestCard')) | Where-Object { $_.Current.Name -eq $agent -and (Control 'RequestStatus' $_).Current.Name -in @('Waiting for you','Answering here') } | Select-Object -Last 1
}
function Choose($card,[string]$label) {
 $named = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,$label)
 $typed = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::CheckBox)
 $choice = $card.FindFirst([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.AndCondition]::new($named,$typed))
 if (-not $choice) { throw "Missing choice $label" }
 $choice.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()
 return $choice
}
function Keys($control,[string]$keys) {
 [InputWindow]::SetForegroundWindow([IntPtr]$script:hubWindow.Current.NativeWindowHandle) | Out-Null
 $control.SetFocus(); Start-Sleep -Milliseconds 100
 if ([InputWindow]::GetForegroundWindow() -ne [IntPtr]$script:hubWindow.Current.NativeWindowHandle) { throw 'Fixture app is not foreground; refusing to send keys.' }
 [System.Windows.Forms.SendKeys]::SendWait($keys)
}
function Target([string]$name) {
 $target = Control 'Target'
 $target.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
 $condition = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,$name)
 $target.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$condition).GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
 $target.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Collapse()
}
function Send([string]$text) { (Value).SetValue($text); Invoke-Control 'SendButton' }
function Agent-Ready([string]$agent) {
 $tasks = @(Get-Content -Raw -LiteralPath (Join-Path $hubData 'tasks.json') | ConvertFrom-Json)
 return (Control ($agent + 'Status')).Current.Name -in @('Ready','Standby') -and @($tasks | Where-Object State -eq 1).Count -eq 0
}
function Both-Ready { return (Agent-Ready 'Codex') -and (Agent-Ready 'Claude') }
function Read-Room { return @(Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $hubData 'rooms.json') | ConvertFrom-Json) | Where-Object { $_.Id -eq 'input' } }
function No-Dialogs {
 $type = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::Window)
 if ($script:hubWindow.FindAll([System.Windows.Automation.TreeScope]::Descendants,$type).Count -ne 0) { throw 'Agent input opened a popup window' }
}
function Start-Hub {
 $script:hubApp = Start-Process -FilePath (Join-Path $AppDirectory 'AI Hub.exe') -WindowStyle Hidden -PassThru
 $processCondition = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ProcessIdProperty,$script:hubApp.Id)
 $nameCondition = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,'AI Hub')
 $windowCondition = [System.Windows.Automation.AndCondition]::new($processCondition,$nameCondition)
 Wait-For { $script:hubWindow = [System.Windows.Automation.AutomationElement]::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Children,$windowCondition); $null -ne $script:hubWindow } 'No inline input test window'
 Start-Sleep -Milliseconds 1000
 [InputWindow]::ShowWindow([IntPtr]$script:hubWindow.Current.NativeWindowHandle,9) | Out-Null
 $null = Control 'Composer'
}
function Close-Hub {
 Invoke-Control 'CloseButton'
 if (-not $script:hubApp.WaitForExit(10000) -or $script:hubApp.ExitCode -ne 0) { throw 'Input window did not close cleanly' }
 $script:hubApp.Dispose(); $script:hubApp = $null
}
function Capture([string]$name) {
 $rect = $script:hubWindow.Current.BoundingRectangle
 $bitmap = [System.Drawing.Bitmap]::new([int]$rect.Width,[int]$rect.Height)
 $graphics = [System.Drawing.Graphics]::FromImage($bitmap); $dc = $graphics.GetHdc()
 try { [InputWindow]::PrintWindow([IntPtr]$script:hubWindow.Current.NativeWindowHandle,$dc,2) | Out-Null }
 finally { $graphics.ReleaseHdc($dc); $graphics.Dispose() }
 try { $bitmap.Save((Join-Path $hubData $name),[System.Drawing.Imaging.ImageFormat]::Png) } finally { $bitmap.Dispose() }
}
try {
 Start-Hub
 Send 'input-fixture-delayed'
 (Value).SetValue('My unsent next task')
 Wait-For { $null -ne (Card 'Codex') } 'First speaker question was not presented'
 if ($null -ne (Card 'Claude Code') -or (Control 'ClaudeStatus').Current.Name -ne 'Listening') { throw 'The peer started before the first speaker finished' }
 No-Dialogs
 if ((Control 'SendButton').Current.IsEnabled -or (Value).Current.Value -ne '' -or (Control 'Target').Current.IsEnabled) { throw ('Answer routing or empty-answer guard failed: Send=' + (Control 'SendButton').Current.IsEnabled + '; Value=' + (Value).Current.Value + '; Target=' + (Control 'Target').Current.IsEnabled) }
 $codexCard = Card 'Codex'
 $choice = Choose $codexCard 'Comfortable'
 if ((Control 'AnswerLabel').Current.Name -notlike 'Answering Codex*') { throw 'Choice did not target Codex' }
 Capture 'choices.png'
 Keys $choice '{ENTER}'
 Wait-For { $null -ne (Card 'Claude Code') } 'Enter on the selected choice did not resume Codex and hand off to Claude'
 $claudeCard = Card 'Claude Code'
 if ((Control 'ClaudeStatus').Current.Name -ne 'Needs your input') { throw 'Answer was sent to the other agent' }
 $null = Choose $claudeCard 'Compact'
 Keys (Control 'Composer') '{ENTER}'
 Wait-For { Both-Ready } 'Enter in composer did not send Claude the selected choice'
 if ((Value).Current.Value -ne 'My unsent next task') { throw 'An ordinary draft was lost or used as an answer' }
 Wait-For { @((Read-Room).Messages | Where-Object { $_.Speaker -eq 'You' -and $_.Text -eq 'Comfortable' -and $_.Route -eq 'Codex' }).Count -eq 1 -and @((Read-Room).Messages | Where-Object { $_.Speaker -eq 'You' -and $_.Text -eq 'Compact' -and $_.Route -eq 'Claude' }).Count -eq 1 } 'Answers were not persisted with their recipients'
 Write-Output 'PASS sequential inline choices, listening peer, Enter in card/composer, correct recipients, and preserved ordinary draft'

 Target 'Claude only'; Send 'input-fixture-multi'
 Wait-For { $null -ne (Card 'Claude Code') } 'Claude multi-select card missing'
 $card = Card 'Claude Code'
 $null = Choose $card 'Compact'; $null = Choose $card 'Comfortable'
 Invoke-Control 'RequestSend' $card
 Wait-For { Agent-Ready 'Claude' } 'Multi-select did not finish'
 Wait-For { @((Read-Room).Messages | Where-Object { $_.Speaker -eq 'You' -and $_.Text -eq 'Compact, Comfortable' }).Count -eq 1 } 'Both selected labels were not sent'
 Write-Output 'PASS multiple selections reach Claude together'

 Target 'Both agents'; Send 'Codex, input-fixture-open'
 Wait-For { $null -ne (Card 'Codex') } 'First open question missing'
 Invoke-Control 'RequestWrite' (Card 'Codex'); (Value).SetValue('First line')
 if ((Value).Current.Value -ne 'First line') { throw 'Switching questions lost an unfinished answer' }
 Keys (Control 'Composer') '^{END}'
 Keys (Control 'Composer') '+{ENTER}'
 if ((Value).Current.Value -notmatch '[\r\n]' -or (Control 'CodexStatus').Current.Name -ne 'Needs your input') { throw 'Shift+Enter submitted instead of adding a line' }
 Keys (Control 'Composer') 'Second line'
 Keys (Control 'Composer') '{ENTER}'
 Wait-For { $null -ne (Card 'Claude Code') } 'Open-ended Enter did not resume Codex and pass the turn'
 (Value).SetValue('Claude answer')
 Keys (Control 'Composer') '{ENTER}'
 Wait-For { Both-Ready } 'Open-ended answer did not resume Claude'
 Wait-For { @((Read-Room).Messages | Where-Object { $_.Speaker -eq 'You' -and $_.Route -eq 'Codex' -and $_.Text -match 'First line\s+Second line' }).Count -eq 1 } 'Multiline text was not preserved'
 Write-Output 'PASS free-text replies, Shift+Enter newline, Enter submission and sequential follow-up'

 Target 'Codex only'; Send 'input-fixture-secret'
 Wait-For { $null -ne (Card 'Codex') } 'Secret question missing'
 if (-not (Control 'SecretAnswer').Current.IsPassword) { throw 'Private input is not masked' }
 Keys (Control 'SecretAnswer') 'private-fixture-value'
 Keys (Control 'SecretAnswer') '{ENTER}'
 Wait-For { Agent-Ready 'Codex' } 'Private answer did not resume agent'
 Start-Sleep -Seconds 4
 if ((Get-Content -Raw -LiteralPath (Join-Path $hubData 'rooms.json')).Contains('private-fixture-value')) { throw 'Private answer leaked into saved history' }
 Write-Output 'PASS private answers are masked and omitted from saved history'

 Target 'Both agents'; Send 'Codex, input-fixture-delayed-open'
 (Value).SetValue('Draft to retain after Stop')
 Wait-For { $null -ne (Card 'Codex') } 'Stop question missing'
 $scale = [InputWindow]::GetDpiForWindow([IntPtr]$hubWindow.Current.NativeWindowHandle) / 96.0
 $hubWindow.GetCurrentPattern([System.Windows.Automation.TransformPattern]::Pattern).Resize((1080*$scale),(700*$scale))
 Start-Sleep -Milliseconds 300
 foreach ($id in @('Composer','SendButton','StopButton','AnswerLabel','ShowQuestionButton')) {
  $element = Control $id
  if ($element.Current.IsOffscreen -or -not $hubWindow.Current.BoundingRectangle.Contains($element.Current.BoundingRectangle)) { throw "Compact pending input clips $id" }
 }
 Capture 'compact-open.png'
 Invoke-Control 'StopButton'
 Wait-For { $null -eq (Card 'Codex') -and $null -eq (Card 'Claude Code') } 'Stop left questions interactive'
 if ((Value).Current.Value -ne 'Draft to retain after Stop') { throw 'Stop lost the ordinary draft' }
 Close-Hub; Start-Hub
 if ($null -ne (Card 'Codex') -or $null -ne (Card 'Claude Code')) { throw 'Restart resurrected a question' }
 if (@((Read-Room).Messages | Where-Object { $_.Input -and $_.Input.Status -eq 3 }).Count -ne 1) { throw 'Cancelled request was not saved or a queued peer started' }
 No-Dialogs
 Write-Output 'PASS compact layout, Stop cancellation, transcript history and restart'

 Send 'Codex, input-fixture-open'
 Wait-For { $null -ne (Card 'Codex') } 'Room-switch question missing'
 Invoke-Control 'NewRoomButton'
 Wait-For { (Control 'RoomTitle').Current.Name -eq 'New conversation' } 'New conversation did not open'
 if ($null -ne (Card 'Codex') -or $null -ne (Card 'Claude Code') -or (Value).Current.Value -ne '') { throw 'Questions leaked into another room' }
 if (@((Read-Room).Messages | Where-Object { $_.Input -and $_.Input.Status -eq 0 }).Count -ne 1) { throw 'Departed room lost its pending question' }
 $roomItems = (Control 'RoomList').FindAll([System.Windows.Automation.TreeScope]::Children,[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::ListItem))
 $oldRoomName = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,'Input check')
 $oldRoomItem = $roomItems | Where-Object { $null -ne $_.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$oldRoomName) } | Select-Object -First 1
 if (-not $oldRoomItem) { throw 'Original conversation missing' }
 $oldRoomItem.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
 Wait-For { $null -ne (Card 'Codex') } 'Returning did not restore the live question'
 $returnCard = Card 'Codex'
 Invoke-Control 'RequestWrite' $returnCard
 Keys (Control 'Composer') 'Return-room answer'
 Keys (Control 'Composer') '{ENTER}'
 Wait-For { $null -eq (Card 'Codex') } 'Returning question did not resume its provider'
 Close-Hub
 Write-Output ('PASS room switching preserves questions and routes returning answers; evidence: ' + $hubData)
}
catch { Write-Output $_.ScriptStackTrace; throw }
finally {
 if ($hubApp -and -not $hubApp.HasExited) { $hubApp.CloseMainWindow() | Out-Null; if (-not $hubApp.WaitForExit(10000)) { $hubApp.Kill(); $hubApp.WaitForExit() } }
 $env:AIHUB_DATA_DIR = $hubPreviousData
}
