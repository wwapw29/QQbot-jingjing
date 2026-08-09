@echo off
chcp 65001 >nul
rem ============================================
rem  QQBot (JingJing) - Stop
rem ============================================
taskkill /F /IM QQBot.exe >nul 2>&1
echo [OK] QQBot stopped.
