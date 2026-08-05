@echo off
chcp 65001 >nul
setlocal
set "DINGLATER_EXE=%~dp0BuildOutput\DingLater.exe"
if not exist "%DINGLATER_EXE%" (
    echo [DingLater] 还没有生成最新测试版。
    echo 请先在项目根目录运行：powershell -ExecutionPolicy Bypass -File .\scripts\Build-Portable.ps1
    pause
    exit /b 1
)
start "" "%DINGLATER_EXE%"
