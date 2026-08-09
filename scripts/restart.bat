@echo off
chcp 65001 >nul
rem ============================================
rem  QQBot (JingJing) - Restart
rem  stop -> sync config -> start
rem  Usage: double-click, or run: restart.bat
rem ============================================
set ROOT=%~dp0..
set RUN=%ROOT%\src\QQBot\bin\Debug\net10.0

echo [1/3] Stopping QQBot...
taskkill /F /IM QQBot.exe >nul 2>&1
timeout /t 2 /nobreak >nul

echo [2/3] Syncing config...
copy /Y "%ROOT%\src\QQBot\appsettings.json" "%RUN%\appsettings.json" >nul

echo [3/3] Starting QQBot...
start "QQBot" "%RUN%\QQBot.exe"

echo.
echo Done. New window shows live logs.
