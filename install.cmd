@echo off
rem Installs Clyppey under %LOCALAPPDATA%\Programs\Clyppey and makes a desktop shortcut.
rem Running it from there lets it write to Videos / AppData like any normal app.
setlocal
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "scripts\install.ps1" %*
if errorlevel 1 (
  echo.
  echo インストールに失敗しました。
)
pause
