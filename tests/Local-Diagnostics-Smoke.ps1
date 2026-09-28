param([string]$AppDirectory = '')
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$hubRoot = Split-Path $PSScriptRoot -Parent
if (-not $AppDirectory) { $AppDirectory = Join-Path $hubRoot 'app' }
$hubData = Join-Path $hubRoot ('artifacts\local-diagnostics-smoke-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path (Join-Path $hubData 'diagnostics') | Out-Null
# A finding recorded by an earlier version must load, display its version, and survive the session.
# Enums are persisted numerically: AuditCode.ProviderError = 0, Agent.Codex = 0.
$seeded = @{Findings=@(@{Code=0;Room='';Task='';Agent=0;First='2026-09-27T01:00:00+00:00';Last='2026-09-27T01:05:00+00:00';Count=3;ExceptionType='';Version='0.11.0'});Recent=@();Evicted=0}
ConvertTo-Json -Depth 6 -InputObject $seeded | Set-Content -Encoding UTF8 -LiteralPath (Join-Path $hubData 'diagnostics\audit.json')
@{Workspace=$hubData;AllowEdits=$true;AutoExchange=$true;ReduceMotion=$true;CollectLocalDiagnostics=$true} | ConvertTo-Json | Set-Content -Encoding UTF8 -LiteralPath (Join-Path $hubData 'settings.json')
# Built from code points so the script stays ASCII; PowerShell 5.1 reads BOM-less scripts as ANSI.
$hubDot = [string][char]0xB7
$hubDialogTitle = "AI Hub $hubDot Local diagnostics"
$hubReviewMode = "Audit review $hubDot read only"
$hubPrevious = $env:AIHUB_DATA_DIR
$env:AIHUB_DATA_DIR = $hubData
$hubProcess = $null
function Find-Control($root,[string]$id) {
 $condition = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty,$id)
 $deadline=(Get-Date).AddSeconds(20)
 do { $found=$root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$condition); if (-not $found) { Start-Sleep -Milliseconds 100 } } until ($found -or (Get-Date) -gt $deadline)
 if (-not $found) { throw "Missing control: $id" }; return $found
}
function Invoke-Control($root,[string]$id) { (Find-Control $root $id).GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
function Value-Of($root,[string]$id) { (Find-Control $root $id).GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value }
function Window([string]$name) {
 $condition=[System.Windows.Automation.AndCondition]::new(
  [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ProcessIdProperty,$hubProcess.Id),
  [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,$name))
 $deadline=(Get-Date).AddSeconds(20)
 do { $found=[System.Windows.Automation.AutomationElement]::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$condition); if (-not $found) { Start-Sleep -Milliseconds 100 } } until ($found -or (Get-Date) -gt $deadline)
 if (-not $found) { throw "Missing window: $name" }; return $found
}
function Close-App() {
 Invoke-Control $script:main 'CloseButton'
 if (-not $hubProcess.WaitForExit(15000) -or $hubProcess.ExitCode -ne 0) { throw 'App did not close cleanly' }
}
try {
 $hubProcess=Start-Process -FilePath (Join-Path $AppDirectory 'AI Hub.exe') -WindowStyle Hidden -PassThru
 $main=Window 'AI Hub'
 Invoke-Control $main 'AuditButton'
 $dialog=Window $hubDialogTitle
 $report=Value-Of $dialog 'AuditReportText'
 $report | Set-Content -Encoding UTF8 -LiteralPath (Join-Path $hubData 'report-text.txt')
 foreach ($expected in @('# AI Hub local audit','Collection: enabled','ProviderError (3 observations)','version 0.11.0','No automatic AI reviews run')) {
  if (-not $report.Contains($expected)) { throw "Diagnostics report missing: $expected (report saved to report-text.txt)" }
 }
 if ($report.Contains('Storage notice')) { throw 'Seeded diagnostics were rejected on load' }
 Invoke-Control $dialog 'PrepareAuditReviewButton'
 Start-Sleep -Milliseconds 800
 $draft=Value-Of $main 'Composer'
 if (-not $draft.Contains('Review AI Hub') -or -not $draft.Contains('ProviderError (3 observations)')) { throw 'Prepared review draft did not carry the report' }
 $mode=(Find-Control $main 'ModeLabel').Current.Name
 if ($mode -ne $hubReviewMode) { throw "Audit review room is not read only: '$mode'" }
 Close-App
 $rooms=Get-Content -Raw -LiteralPath (Join-Path $hubData 'rooms.json') | ConvertFrom-Json
 $review=@($rooms | Where-Object { $_.IsAuditReview -eq $true })
 if ($review.Count -ne 1 -or $review[0].Title -ne 'AI Hub audit review' -or -not $review[0].Draft.Contains('Review AI Hub')) { throw 'Audit review room was not persisted' }
 $audit=Get-Content -Raw -LiteralPath (Join-Path $hubData 'diagnostics\audit.json') | ConvertFrom-Json
 if (@($audit.Findings | Where-Object { $_.Code -eq 0 -and $_.Version -eq '0.11.0' -and $_.Count -eq 3 }).Count -ne 1) { throw 'Seeded finding was lost or merged across versions' }
 if ((Get-Content -Raw -LiteralPath (Join-Path $hubData 'diagnostics\audit.json')).Contains('Review AI Hub')) { throw 'Diagnostics file contains conversation text' }
 $hubProcess=Start-Process -FilePath (Join-Path $AppDirectory 'AI Hub.exe') -WindowStyle Hidden -PassThru
 $main=Window 'AI Hub'
 if ((Find-Control $main 'ModeLabel').Current.Name -ne $hubReviewMode) { throw 'Reopened audit review room lost its read-only mode' }
 Invoke-Control $main 'AuditButton'
 $dialog=Window $hubDialogTitle
 if (-not (Value-Of $dialog 'AuditReportText').Contains('ProviderError (3 observations)')) { throw 'Finding did not survive restart' }
 $dialog.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close()
 Close-App
 Write-Output "PASS local diagnostics report, per-version findings, prepared read-only review room, restart persistence and text exclusion; profile: $hubData"
}
finally {
 if ($hubProcess -and -not $hubProcess.HasExited) { $hubProcess.CloseMainWindow() | Out-Null; if (-not $hubProcess.WaitForExit(15000)) { $hubProcess.Kill(); $hubProcess.WaitForExit() } }
 $env:AIHUB_DATA_DIR=$hubPrevious
}
