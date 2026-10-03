param([string]$AppDirectory = '')
# Offline native UI checks. Every conversation and provider belongs to this isolated fixture.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class ConversationWindow {
 [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr handle, IntPtr dc, uint flags);
 [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr handle);
 [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr handle, uint message, IntPtr wparam, IntPtr lparam);
}
'@
$hubRoot = Split-Path $PSScriptRoot -Parent
if (-not $AppDirectory) { $AppDirectory = Join-Path $hubRoot 'app' }
$hubData = Join-Path $hubRoot ('artifacts\conversation-smoke-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $hubData -Force | Out-Null
$hubFixture = Join-Path $PSScriptRoot 'AIHub.Tests\bin\Release\net10.0\AIHub.Tests.exe'
if (-not (Test-Path -LiteralPath $hubFixture)) { throw 'Build the fixture runner first.' }
$hubRooms = @(
 @{Id='primary';Title='Primary project';Workspace=$hubData;Draft='A preserved draft';Target='Codex';Messages=@(@{Speaker='You';Text='Saved primary transcript';Complete=$true})},
 @{Id='other';Title='Other project';Workspace=$hubData;Draft='Other private draft';Target='Claude';Messages=@()},
 @{Id='older';Title='Older archive';Workspace=$hubData;Draft='Archived draft';IsArchived=$true;Target='Both';Messages=@(@{Speaker='Claude';Text='Readable archived answer';Complete=$true})}
)
ConvertTo-Json -InputObject $hubRooms -Depth 8 | Set-Content -Encoding UTF8 -LiteralPath (Join-Path $hubData 'rooms.json')
@{Workspace=$hubData;LastRoomId='primary';CodexPath=$hubFixture;ClaudePath=$hubFixture;AllowEdits=$true;AutoExchange=$true;ReduceMotion=$true} | ConvertTo-Json | Set-Content -Encoding UTF8 -LiteralPath (Join-Path $hubData 'settings.json')
'other log' | Set-Content -Encoding UTF8 -LiteralPath (Join-Path $hubData 'activity-other.jsonl')
'project sentinel' | Set-Content -Encoding UTF8 -LiteralPath (Join-Path $hubData 'project.txt')
$env:AIHUB_DATA_DIR = $hubData; $env:AIHUB_UI_TEST = '1'
$hubApp = $null
function Control([string]$id, $root=$script:hubWindow) {
 $condition = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty,$id)
 $deadline = (Get-Date).AddSeconds(5)
 do { $element = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$condition); if ($null -eq $element) { Start-Sleep -Milliseconds 75 } } until ($null -ne $element -or (Get-Date) -gt $deadline)
 if ($null -eq $element) { throw "Missing control: $id" }; return $element
}
function Invoke-Control([string]$id, $root=$script:hubWindow) { (Control $id $root).GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
function Wait-For([scriptblock]$condition, [string]$failure) {
 $deadline = (Get-Date).AddSeconds(10)
 do { if (& $condition) { return }; Start-Sleep -Milliseconds 100 } until ((Get-Date) -gt $deadline)
 throw $failure
}
function Room-Items {
 $condition = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::ListItem)
 return (Control 'RoomList').FindAll([System.Windows.Automation.TreeScope]::Children,$condition)
}
function Choose-View([string]$label) {
 $filter = Control 'RoomFilter'
 $filter.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
 $condition = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,$label)
 $item = $filter.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$condition)
 if ($null -eq $item) { throw "Missing conversation view: $label" }
 $item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
 $filter.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Collapse()
 Wait-For { $filter.GetCurrentPattern([System.Windows.Automation.SelectionPattern]::Pattern).Current.GetSelection()[0].Current.Name -eq $label } "Conversation view was not selected: $label"
 Start-Sleep -Milliseconds 250
}
function Select-Room([string]$title) {
 $condition = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,$title)
 $item = Room-Items | Where-Object { $_.Current.Name -eq $title -or $null -ne $_.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$condition) } | Select-Object -First 1
 if ($null -eq $item) { throw "Missing conversation: $title" }
 $item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
 Wait-For { (Control 'RoomTitle').Current.Name -eq $title -and (Control 'RoomList').Current.IsEnabled } "Conversation did not open: $title"
}
function Composer-Value { return (Control 'Composer').GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern) }
function Read-Rooms {
 $savedRooms = Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $hubData 'rooms.json') | ConvertFrom-Json
 foreach ($room in $savedRooms) { Write-Output $room }
}
function Dialog([string]$title) {
 $condition = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::Window)
 return $script:hubWindow.FindAll([System.Windows.Automation.TreeScope]::Descendants,$condition) | Where-Object { $_.Current.Name -like $title } | Select-Object -First 1
}
function Start-Hub {
 $script:hubApp = Start-Process -FilePath (Join-Path $AppDirectory 'AI Hub.exe') -WindowStyle Hidden -PassThru
 Wait-For { $script:hubApp.Refresh(); $script:hubApp.MainWindowHandle -ne 0 } 'No conversation test window'
 # WPF's HWND can exist before its automation provider is attached. Avoid caching that early stub.
 Start-Sleep -Milliseconds 1000
 $script:hubWindow = [System.Windows.Automation.AutomationElement]::FromHandle($script:hubApp.MainWindowHandle)
 $null = Control 'Composer'
}
function Close-Hub {
 Invoke-Control 'CloseButton'
 if (-not $script:hubApp.WaitForExit(10000) -or $script:hubApp.ExitCode -ne 0) { throw 'App did not close cleanly' }
 $script:hubApp.Dispose(); $script:hubApp = $null
}
function Capture([string]$name) {
 $rect = $script:hubWindow.Current.BoundingRectangle
 $bitmap = [System.Drawing.Bitmap]::new([int]$rect.Width,[int]$rect.Height)
 $graphics = [System.Drawing.Graphics]::FromImage($bitmap); $dc = $graphics.GetHdc()
 try { if (-not [ConversationWindow]::PrintWindow($script:hubApp.MainWindowHandle,$dc,2)) { throw 'Window capture failed' } }
 finally { $graphics.ReleaseHdc($dc); $graphics.Dispose() }
 try { $bitmap.Save((Join-Path $hubData $name),[System.Drawing.Imaging.ImageFormat]::Png) } finally { $bitmap.Dispose() }
}
function Owned-Providers {
 return @(Get-CimInstance Win32_Process -Filter "Name='AIHub.Tests.exe'" | Where-Object { $_.ParentProcessId -eq $script:hubApp.Id })
}
function Start-Pending-Turn {
 (Composer-Value).SetValue('Review this isolated fixture project.')
 Invoke-Control 'SendButton'
 Wait-For { $null -ne (Pending-Approval) } 'Fixture approval was not shown'
 if ((Owned-Providers).Count -eq 0) { throw 'Fixture provider was not owned by the test window' }
}
function Pending-Approval {
 $condition = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty,'AgentRequestCard')
 return $script:hubWindow.FindAll([System.Windows.Automation.TreeScope]::Descendants,$condition) | Where-Object { $_.Current.Name -eq 'Codex' -and (Control 'RequestStatus' $_).Current.Name -eq 'Waiting for you' } | Select-Object -Last 1
}
try {
 Start-Hub
 if ((Room-Items).Count -ne 2) { throw 'Archived conversations leaked into the active list' }
 if ((Composer-Value).Current.Value -ne 'A preserved draft') { throw 'Initial draft missing' }
 Choose-View 'Archived conversations'
 if (@(Room-Items).Count -ne 1 -or (Control 'RoomTitle').Current.Name -ne 'Primary project') { throw ('Changing view failed: visible rooms=' + @(Room-Items).Count + ', title=' + (Control 'RoomTitle').Current.Name) }
 Select-Room 'Older archive'
 if (-not (Composer-Value).Current.IsReadOnly -or (Control 'SendButton').Current.IsEnabled) { throw 'Archived conversation can send messages' }
 if ((Control 'RestoreConversationButton').Current.IsOffscreen -or -not (Control 'ExportButton').Current.IsEnabled) { throw 'Archive lacks restore or export access' }
 if ((Composer-Value).Current.Value -ne 'Archived draft') { throw 'Archive lost its draft' }
 (Control 'SearchBox').GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('Older')
 if ((Room-Items).Count -ne 1) { throw 'Archived title search failed' }
 (Control 'SearchBox').GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('absent')
 if ((Room-Items).Count -ne 0 -or (Control 'SearchEmpty').Current.IsOffscreen) { throw 'Empty archived search has no feedback' }
 (Control 'SearchBox').GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('')
 Close-Hub
 Start-Hub
 if ((Control 'RoomTitle').Current.Name -ne 'Older archive' -or -not (Composer-Value).Current.IsReadOnly) { throw 'Restart did not restore the selected archived room' }
 if ((Owned-Providers).Count -ne 0) { throw 'Reading archived conversation launched providers' }
 $scale = [ConversationWindow]::GetDpiForWindow($hubApp.MainWindowHandle) / 96.0
 $hubWindow.GetCurrentPattern([System.Windows.Automation.TransformPattern]::Pattern).Resize((1080*$scale),(700*$scale))
 Start-Sleep -Milliseconds 200
 foreach ($id in @('RoomFilter','ArchiveButton','DeleteConversationButton','ExportButton','RestoreConversationButton','Composer','SendButton')) {
  $item = Control $id
  if ($item.Current.IsOffscreen -or -not $hubWindow.Current.BoundingRectangle.Contains($item.Current.BoundingRectangle)) { throw "Compact archive clips $id" }
 }
 Capture 'archived-compact.png'
 Invoke-Control 'RestoreConversationButton'
 Wait-For { (Control 'RoomTitle').Current.Name -eq 'Older archive' -and -not (Composer-Value).Current.IsReadOnly } 'Restore did not make the conversation active'
 if ((Room-Items).Count -ne 3 -or (Composer-Value).Current.Value -ne 'Archived draft' -or (Owned-Providers).Count -ne 0) { throw 'Restore lost draft, active list, or started providers' }
 Write-Output 'PASS archive filtering, search, read-only view, export access, restart, compact layout, and explicit restore'

 Select-Room 'Primary project'
 Start-Pending-Turn
 (Composer-Value).SetValue('Unsent draft during work')
 Invoke-Control 'ArchiveButton'
 Wait-For { (Control 'RoomTitle').Current.Name -eq 'Other project' } 'Archiving did not select a remaining active room'
 Wait-For { (Owned-Providers).Count -eq 0 -and $null -eq (Pending-Approval) } 'Archive left its provider or approval alive'
 $archived = Read-Rooms | Where-Object { $_.Id -eq 'primary' }
 if (-not $archived.IsArchived -or $archived.Draft -ne 'Unsent draft during work' -or $archived.Target -ne 'Codex') { throw 'Active archive lost state' }
 Choose-View 'Archived conversations'; Select-Room 'Primary project'
 Invoke-Control 'RestoreConversationButton'
 Start-Pending-Turn
 (Composer-Value).SetValue('Draft kept when deletion is canceled')
 Invoke-Control 'DeleteConversationButton'
 Wait-For { $null -ne (Dialog 'Delete conversation?') } 'Delete has no confirmation'
 Invoke-Control 'CancelDeleteConversationButton' (Dialog 'Delete conversation?')
 if (@(Read-Rooms | Where-Object { $_.Id -eq 'primary' }).Count -ne 1 -or @(Owned-Providers).Count -eq 0 -or (Composer-Value).Current.Value -ne 'Draft kept when deletion is canceled') { throw 'Canceling delete removed the room, lost the draft, or stopped work' }
 Invoke-Control 'DeleteConversationButton'
 Wait-For { $null -ne (Dialog 'Delete conversation?') } 'Second delete confirmation missing'
 Invoke-Control 'ConfirmDeleteConversationButton' (Dialog 'Delete conversation?')
 Wait-For { (Control 'RoomTitle').Current.Name -eq 'Other project' } 'Delete did not select a remaining active room'
 Wait-For { (Owned-Providers).Count -eq 0 -and $null -eq (Pending-Approval) } 'Delete left its provider or approval alive'
 Start-Sleep -Milliseconds 500
 if (@(Read-Rooms | Where-Object { $_.Id -eq 'primary' }).Count -ne 0 -or (Test-Path -LiteralPath (Join-Path $hubData 'activity-primary.jsonl'))) { throw 'Deleted conversation or activity log remains/reappeared' }
 if (-not (Test-Path -LiteralPath (Join-Path $hubData 'activity-other.jsonl')) -or (Get-Content -Raw -LiteralPath (Join-Path $hubData 'project.txt')).Trim() -ne 'project sentinel') { throw 'Delete affected another log or project file' }
 Write-Output 'PASS active archive/delete cancel owned providers and pending approvals; cancellation and scoped deletion preserve unrelated data'

 Invoke-Control 'ArchiveButton'
 Wait-For { (Control 'RoomTitle').Current.Name -eq 'Older archive' } 'Archive did not move to the last active room'
 Invoke-Control 'ArchiveButton'
 Wait-For { (Control 'RoomTitle').Current.Name -eq 'New conversation' } 'Archiving the final active room did not create an active replacement'
 if ((Room-Items).Count -ne 1 -or (Composer-Value).Current.Value -ne '') { throw 'Replacement active room inherited data' }
 Invoke-Control 'DeleteConversationButton'
 Wait-For { $null -ne (Dialog 'Delete conversation?') } 'Final-room delete confirmation missing'
 Invoke-Control 'ConfirmDeleteConversationButton' (Dialog 'Delete conversation?')
 Wait-For { (Control 'RoomList').Current.IsEnabled } 'Final-room deletion did not finish'
 if ((Room-Items).Count -ne 1 -or @(Read-Rooms | Where-Object { -not $_.IsArchived }).Count -ne 1) { throw 'Deleting the final active room did not leave exactly one replacement' }
 Choose-View 'Archived conversations'; Select-Room 'Other project'
 Invoke-Control 'DeleteConversationButton'
 Wait-For { $null -ne (Dialog 'Delete conversation?') } 'Archived deletion confirmation missing'
 Invoke-Control 'ConfirmDeleteConversationButton' (Dialog 'Delete conversation?')
 Wait-For { (Control 'RoomTitle').Current.Name -eq 'New conversation' } 'Archived deletion did not return to active conversations'
 Close-Hub
 $finalRooms = Read-Rooms
 $lastRoom = (Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $hubData 'settings.json') | ConvertFrom-Json).LastRoomId
 if (@($finalRooms | Where-Object { $_.Id -eq $lastRoom -and -not $_.IsArchived }).Count -ne 1 -or @($finalRooms | Where-Object { $_.Id -eq 'other' }).Count -ne 0) { throw 'Last-selected persistence references a removed room' }
 Write-Output 'PASS final active room replacement, archived deletion, and last-selected persistence'
 Start-Hub
 Start-Pending-Turn
 $closingProviders = @(Owned-Providers | ForEach-Object { $_.ProcessId })
 Invoke-Control 'ArchiveButton'
 Close-Hub
 foreach ($providerId in $closingProviders) {
  if ($null -ne (Get-Process -Id $providerId -ErrorAction SilentlyContinue)) { throw 'Closing during archive abandoned an owned provider' }
 }
 if (@(Read-Rooms | Where-Object { $_.Id -eq $lastRoom -and $_.IsArchived }).Count -ne 1) { throw 'Closing during archive lost the archived state' }
 Write-Output 'PASS immediate close during archive preserves the operation and disposes owned providers'
 Write-Output "Isolated conversation-management evidence: $hubData"
}
finally {
 if ($null -ne $hubApp) {
  if (-not $hubApp.HasExited) {
   $confirm = Dialog 'Delete conversation?'
   if ($null -ne $confirm) { Invoke-Control 'CancelDeleteConversationButton' $confirm }
   try { Invoke-Control 'CloseButton' }
   catch { [ConversationWindow]::PostMessage($hubApp.MainWindowHandle,16,[IntPtr]::Zero,[IntPtr]::Zero) | Out-Null }
   if (-not $hubApp.WaitForExit(10000)) { throw 'Isolated fixture app did not close during cleanup' }
  }
  $hubApp.Dispose()
 }
}
