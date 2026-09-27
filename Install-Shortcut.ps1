$ErrorActionPreference = 'Stop'
$hubExecutable = Join-Path $PSScriptRoot 'app\AI Hub.exe'
if (-not (Test-Path -LiteralPath $hubExecutable)) { throw 'Build AI Hub first with Build.ps1.' }
$hubDesktop = [Environment]::GetFolderPath('Desktop')
$hubShortcutPath = Join-Path $hubDesktop 'AI Hub.lnk'
$hubShell = New-Object -ComObject WScript.Shell
if (Test-Path -LiteralPath $hubShortcutPath) {
    $hubExisting = $hubShell.CreateShortcut($hubShortcutPath)
    if ($hubExisting.TargetPath -ne $hubExecutable) { throw 'An unrelated AI Hub shortcut already exists. It was left unchanged.' }
}
$hubShortcut = $hubShell.CreateShortcut($hubShortcutPath)
$hubShortcut.TargetPath = $hubExecutable
$hubShortcut.WorkingDirectory = $PSScriptRoot
$hubShortcut.Description = 'Shared conversations with Codex and Claude Code'
$hubIcon = Join-Path $PSScriptRoot 'app\aihub.ico'
if (Test-Path -LiteralPath $hubIcon) { $hubShortcut.IconLocation = $hubIcon + ',0' }
$hubShortcut.Save()
if (-not ('AIHub.Desktop.WindowsAppIdentity' -as [type])) {
    Add-Type -Path (Join-Path $PSScriptRoot 'src\AIHub.Desktop\WindowsAppIdentity.cs')
}
[AIHub.Desktop.WindowsAppIdentity]::SetShortcut($hubShortcutPath)
Write-Output "Created desktop shortcut: $hubShortcutPath"

# Keep existing taskbar pins on the same icon when installing an update.
$hubChangedShortcuts = @($hubShortcutPath)
$hubTaskbar = Join-Path ([Environment]::GetFolderPath('ApplicationData')) 'Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar'
if ((Test-Path -LiteralPath $hubTaskbar) -and (Test-Path -LiteralPath $hubIcon)) {
    foreach ($hubPinnedFile in Get-ChildItem -LiteralPath $hubTaskbar -Filter '*.lnk') {
        $hubPinned = $hubShell.CreateShortcut($hubPinnedFile.FullName)
        if ($hubPinned.TargetPath -ne $hubExecutable) { continue }
        $hubPinned.IconLocation = $hubIcon + ',0'
        $hubPinned.Save()
        [AIHub.Desktop.WindowsAppIdentity]::SetShortcut($hubPinnedFile.FullName)
        $hubChangedShortcuts += $hubPinnedFile.FullName
        Write-Output "Updated AI Hub taskbar shortcut: $($hubPinnedFile.FullName)"
    }
}

if (-not ('HubShortcutNotify' -as [type])) {
    Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class HubShortcutNotify {
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    public static extern void SHChangeNotify(int change, uint flags, string path, IntPtr unused);
}
'@
}
foreach ($hubChangedPath in $hubChangedShortcuts + @($hubIcon, $hubExecutable)) {
    [HubShortcutNotify]::SHChangeNotify(0x2000, 0x1005, $hubChangedPath, [IntPtr]::Zero)
}
