param([string]$AppDirectory = '')
# Offline native UI check for explicit feedback (0.29.0): record feedback on a seeded agent message through the dialog,
# see the badge, list it in Your feedback, and find the record on disk with its links. Everything lives in an isolated profile.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$hubRoot = Split-Path $PSScriptRoot -Parent
if (-not $AppDirectory) { $AppDirectory = Join-Path $hubRoot 'app' }
$hubData = Join-Path $hubRoot ('artifacts\feedback-smoke-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $hubData | Out-Null
$roomId = [Guid]::NewGuid().ToString('N'); $messageId = [Guid]::NewGuid().ToString('N'); $dispatchId = [Guid]::NewGuid().ToString('N')
$workspace = Join-Path $hubData 'project'; New-Item -ItemType Directory -Path $workspace | Out-Null
@{Workspace=$workspace;AllowEdits=$false;AutoExchange=$true;ReduceMotion=$true;LastRoomId=$roomId} | ConvertTo-Json | Set-Content -Encoding UTF8 -LiteralPath (Join-Path $hubData 'settings.json')
# The rooms index holds headers; each transcript is its own file (0.23.0). One completed Codex reply from a structured dispatch.
ConvertTo-Json -Depth 4 -InputObject @(@{Id=$roomId;Title='Feedback smoke';Workspace=$workspace;IsArchived=$false;IsAuditReview=$false;Target='Both';Draft='';PauseReason='';LastTask='';ActiveTaskId='';SessionOptions='';Messages=@()}) | Set-Content -Encoding UTF8 -LiteralPath (Join-Path $hubData 'rooms.json')
ConvertTo-Json -Depth 4 -InputObject @(
 @{TaskId='';Id=[Guid]::NewGuid().ToString('N');Speaker='You';Text='Where is the bug?';Route='Both';Time='2026-10-03T12:00:00-04:00';Complete=$true},
 @{TaskId='';Id=$messageId;Speaker='Codex';Text='The sort key uses the raw date string.';Route='Shared room';Time='2026-10-03T12:01:00-04:00';Complete=$true;DispatchId=$dispatchId}
) | Set-Content -Encoding UTF8 -LiteralPath (Join-Path $hubData ('room-' + $roomId + '.json'))
$hubPrevious = $env:AIHUB_DATA_DIR
$env:AIHUB_DATA_DIR = $hubData
$hubProcess = $null
function Find-By($root,[System.Windows.Automation.AutomationProperty]$property,[string]$value) {
 $condition = [System.Windows.Automation.PropertyCondition]::new($property,$value)
 $deadline=(Get-Date).AddSeconds(20)
 do { $found=$root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$condition); if (-not $found) { Start-Sleep -Milliseconds 100 } } until ($found -or (Get-Date) -gt $deadline)
 if (-not $found) { throw "Missing control: $value" }; return $found
}
function Find-Control($root,[string]$id) { Find-By $root ([System.Windows.Automation.AutomationElement]::AutomationIdProperty) $id }
function Find-Named($root,[string]$name) { Find-By $root ([System.Windows.Automation.AutomationElement]::NameProperty) $name }
function Invoke-Element($element) { $element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
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
 # 1. The feedback button appears on the agent's message and opens the dialog for it.
 Invoke-Element (Find-Named $main 'Feedback on message')
 $dialog=Window "Feedback on Codex's message"
 (Find-Control $dialog 'FeedbackKindNeedsCorrection').GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
 (Find-Control $dialog 'FeedbackExplanation').GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('Missed the month boundary.')
 Invoke-Element (Find-Control $dialog 'SaveFeedbackButton')
 Start-Sleep -Milliseconds 600
 # 2. The badge shows the judgement under the message.
 $badge=(Find-Named $main 'Feedback badge').Current
 if (-not $badge.Name) { throw 'Badge is missing' }
 $badgeText=(Find-Named $main 'Feedback badge').FindFirst([System.Windows.Automation.TreeScope]::Element,[System.Windows.Automation.Condition]::TrueCondition)
 # 3. The record is on disk with its links, the outcome unknown (no task), and no transcript text.
 $file=Join-Path $hubData 'feedback.json'
 if (-not (Test-Path $file)) { throw 'feedback.json was not written' }
 $records=@(Get-Content -Raw -LiteralPath $file | ConvertFrom-Json)
 if ($records.Count -ne 1) { throw "Expected one record, found $($records.Count)" }
 $record=$records[0]
 if ($record.MessageId -ne $messageId -or $record.DispatchId -ne $dispatchId -or $record.RoomId -ne $roomId -or $record.Kind -ne 1 -or $record.Explanation -ne 'Missed the month boundary.' -or $record.Agent -ne 'Codex') { throw "Record links or content are wrong: $($record | ConvertTo-Json -Compress)" }
 if ((Get-Content -Raw -LiteralPath $file).Contains('raw date string')) { throw 'The judged text was copied into the feedback file' }
 if ($record.ExcerptHash.Length -ne 64 -or $record.StrategyVersion.Length -ne 64) { throw 'Hashes were not recorded' }
 # 4. Your feedback lists it; reopening the dialog edits the same record (badge updates), and the list reflects it.
 Invoke-Element (Find-Control $main 'TasksButton')
 $tasks=Window 'Project tasks and notes'
 Invoke-Element (Find-Control $tasks 'AllFeedbackButton')
 $listWindow=Window 'Your feedback'
 $list=Find-Control $listWindow 'FeedbackList'
 $items=$list.FindAll([System.Windows.Automation.TreeScope]::Children,[System.Windows.Automation.Condition]::TrueCondition)
 if ($items.Count -ne 1) { throw "Feedback list shows $($items.Count) items" }
 if (-not $items[0].Current.Name.Contains('Needs correction')) { throw "List item text is wrong: $($items[0].Current.Name)" }
 # 4b. Make a preference from the feedback (0.30.0): the developer confirms the text; the record links back to the feedback.
 $items[0].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
 Invoke-Element (Find-Control $listWindow 'MakePreferenceButton')
 $prefDialog=Window 'Make a preference from this feedback'
 $prefText=Find-Control $prefDialog 'PreferenceText'
 if ($prefText.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value -ne 'Missed the month boundary.') { throw 'Preference draft did not start from the feedback explanation' }
 $prefText.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('Always test date handling across a month boundary.')
 Invoke-Element (Find-Control $prefDialog 'SavePreferenceButton')
 Start-Sleep -Milliseconds 400
 $prefFile=Join-Path $hubData 'preferences.json'
 if (-not (Test-Path $prefFile)) { throw 'preferences.json was not written' }
 $prefs=@(Get-Content -Raw -LiteralPath $prefFile | ConvertFrom-Json)
 if ($prefs.Count -ne 1 -or $prefs[0].Text -ne 'Always test date handling across a month boundary.' -or $prefs[0].Origin -ne 'from_feedback' -or @($prefs[0].SupportingFeedbackIds)[0] -ne $record.Id -or $prefs[0].Scope -ne 1 -or $prefs[0].Workspace -ne $workspace -or $prefs[0].Enabled -ne $true) { throw "Preference record is wrong: $($prefs[0] | ConvertTo-Json -Compress)" }
 Invoke-Element (Find-Control $listWindow 'CloseFeedbackButton')
 # 4c. The Preferences window lists it and can disable it.
 Invoke-Element (Find-Control $tasks 'PreferencesButton')
 $prefWindow=Window 'Preferences'
 $prefList=Find-Control $prefWindow 'PreferenceList'
 $prefItems=$prefList.FindAll([System.Windows.Automation.TreeScope]::Children,[System.Windows.Automation.Condition]::TrueCondition)
 if ($prefItems.Count -ne 1 -or -not $prefItems[0].Current.Name.Contains('from feedback')) { throw "Preferences list is wrong: $($prefItems.Count)" }
 $prefItems[0].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
 Invoke-Element (Find-Control $prefWindow 'TogglePreferenceButton')
 Start-Sleep -Milliseconds 400
 $prefs=@(Get-Content -Raw -LiteralPath $prefFile | ConvertFrom-Json)
 if ($prefs[0].Enabled -ne $false -or $prefs[0].Version -ne 1) { throw 'Disabling did not persist, or bumped the version' }
 Invoke-Element (Find-Control $prefWindow 'ClosePreferencesButton')
 Invoke-Element (Find-Control $tasks 'CloseTasksButton')
 Invoke-Element (Find-Named $main 'Feedback on message')
 $dialog=Window "Feedback on Codex's message"
 (Find-Control $dialog 'FeedbackKindUseful').GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
 Invoke-Element (Find-Control $dialog 'SaveFeedbackButton')
 Start-Sleep -Milliseconds 600
 $records=@(Get-Content -Raw -LiteralPath $file | ConvertFrom-Json)
 if ($records.Count -ne 1 -or $records[0].Kind -ne 0 -or $records[0].Id -ne $record.Id) { throw 'Editing did not update the same record in place' }
 # 5. Closing the app leaves the file intact; a restart loads it.
 Invoke-Element (Find-Control $main 'CloseButton')
 if (-not $hubProcess.WaitForExit(15000) -or $hubProcess.ExitCode -ne 0) { throw 'App did not close cleanly' }
 $records=@(Get-Content -Raw -LiteralPath $file | ConvertFrom-Json)
 if ($records.Count -ne 1) { throw 'Feedback did not survive shutdown' }
 Write-Output "PASS feedback on a seeded agent message: dialog, badge, on-disk record with links and hashes, Your feedback list, in-place edit, survives shutdown; profile: $hubData"
}
finally {
 if ($hubProcess -and -not $hubProcess.HasExited) { try { Stop-Process -Id $hubProcess.Id -Force } catch { } }
 if ($null -eq $hubPrevious) { Remove-Item Env:AIHUB_DATA_DIR -ErrorAction SilentlyContinue } else { $env:AIHUB_DATA_DIR = $hubPrevious }
}
