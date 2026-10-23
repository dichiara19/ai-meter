@echo off
rem Starts AI Meter from source with Windows PowerShell, without a console window.
rem Use this when Smart App Control blocks the unsigned AIMeter.exe.
start "" "%WINDIR%\System32\conhost.exe" --headless powershell.exe -NoProfile -ExecutionPolicy Bypass -Sta -File "%~dp0AIMeter.ps1" %*
