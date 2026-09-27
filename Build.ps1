param([switch]$Test, [string]$RuntimeVersion = '10.0.12', [string]$OutputDirectory = '')
$ErrorActionPreference = 'Stop'
$hubProject = Join-Path $PSScriptRoot 'src\AIHub.Desktop\AIHub.Desktop.csproj'
$hubOutput = if ($OutputDirectory) { [IO.Path]::GetFullPath($OutputDirectory) } else { Join-Path $PSScriptRoot 'app' }
& dotnet publish $hubProject -c Release -r win-x64 --self-contained true "-p:RuntimeFrameworkVersion=$RuntimeVersion" -p:UseSharedCompilation=false -m:1 -o $hubOutput
if ($LASTEXITCODE -ne 0) { throw 'AI Hub build failed. Close the app before rebuilding.' }
& dotnet publish (Join-Path $PSScriptRoot 'src\AIHub.McpBridge\AIHub.McpBridge.csproj') -c Release -r win-x64 --self-contained true "-p:RuntimeFrameworkVersion=$RuntimeVersion" -p:UseSharedCompilation=false -m:1 -o (Join-Path $hubOutput 'bridge')
if ($LASTEXITCODE -ne 0) { throw 'AI Hub collaboration bridge build failed.' }
if ($Test) {
    & dotnet run --project (Join-Path $PSScriptRoot 'tests\AIHub.Tests') -c Release
    if ($LASTEXITCODE -ne 0) { throw 'AI Hub tests failed.' }
}
Write-Output "AI Hub is ready: $hubOutput\AI Hub.exe"
