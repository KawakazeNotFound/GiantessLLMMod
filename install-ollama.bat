@echo off
rem GiantessLLMMod - Ollama one-click installer launcher
chcp 65001 >nul
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0install-ollama.ps1"
pause
