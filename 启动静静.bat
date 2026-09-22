@echo off
chcp 65001 >nul
title 静静桌面宠物
setlocal

rem =====================================================================
rem  静静桌面宠物 · 启动器
rem    · 放在仓库根目录（和 src 文件夹同级）
rem    · 没构建过会自动构建一次；构建失败会把报错留在窗口里
rem    · 她不自己连模型：说话是发给 QQBot 后端（默认 http://127.0.0.1:7088）
rem =====================================================================

set "PROJ=%~dp0src\DesktopPet"
set "OUT=%PROJ%\bin\Debug\net10.0-windows"

if not exist "%PROJ%\DesktopPet.csproj" (
  echo [X] 没找到工程：%PROJ%
  echo     这个 bat 要放在仓库根目录（和 src 同级）才能用。
  pause
  exit /b 1
)

if not exist "%OUT%\DesktopPet.exe" (
  echo [!] 还没构建过，先自动构建一次（首次要还原 NuGet 包，可能慢几分钟）...
  where dotnet >nul 2>nul
  if errorlevel 1 (
    echo [X] 没装 .NET SDK：找不到 dotnet 命令。
    echo     去 https://dotnet.microsoft.com/download 装一个 .NET 10 SDK 再回来。
    pause
    exit /b 1
  )
  pushd "%PROJ%"
  dotnet build -c Debug
  if errorlevel 1 (
    popd
    echo.
    echo [X] 构建失败，看上面的报错。
    pause
    exit /b 1
  )
  popd
)

rem 她的大脑在 QQBot 那边：没开也能启动，只是说话会提示连不上
tasklist /FI "IMAGENAME eq QQBot.exe" 2>nul | find /I "QQBot.exe" >nul
if errorlevel 1 (
  echo [!] 提示：QQBot 好像没在跑 —— 先启动她的大脑，桌宠才能真正说话。
)

start "" /d "%OUT%" DesktopPet.exe
echo 已启动。她会出现在屏幕右下角（位置会被记住）。
echo 日志：%USERPROFILE%\.jingjing-pet\pet.log
timeout /t 4 >nul 2>nul
exit /b 0
