@echo off
chcp 65001 >nul
rem ============================================
rem  QQBot (JingJing) - Start
rem  sync appsettings.json first, then start.
rem ============================================
set ROOT=%~dp0..
set RUN=%ROOT%\src\QQBot\bin\Debug\net10.0

taskkill /F /IM QQBot.exe >nul 2>&1
timeout /t 2 /nobreak >nul

copy /Y "%ROOT%\src\QQBot\appsettings.json" "%RUN%\appsettings.json" >nul
echo [OK] config synced.

start "QQBot" "%RUN%\QQBot.exe"
echo [OK] QQBot started. New window shows logs.
