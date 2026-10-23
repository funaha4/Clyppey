@echo off
rem Headless self-test: verifies capture, buffering, saving and the written file.
setlocal
cd /d "%~dp0"
dotnet build "src\Clyppey.Cli\Clyppey.Cli.csproj" -c Debug --nologo >nul
if errorlevel 1 (
  echo ビルドに失敗しました。
  pause
  exit /b 1
)
echo エンドツーエンド検証を実行します（約 30 秒）...
"src\Clyppey.Cli\bin\Debug\net8.0-windows\clyppey.exe" selftest --seconds %1
pause
