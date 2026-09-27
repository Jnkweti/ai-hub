@echo off
if not exist "%~dp0app\AI Hub.exe" (
    powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Build.ps1"
    if errorlevel 1 (
        pause
        exit /b 1
    )
)
start "" "%~dp0app\AI Hub.exe"
