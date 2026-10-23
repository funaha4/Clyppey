@echo off
rem Build Clyppey (Release).
setlocal
cd /d "%~dp0"
dotnet build "src\Clyppey.App\Clyppey.App.csproj" -c Release --nologo
if errorlevel 1 (
  echo.
  echo ビルドに失敗しました。
  pause
  exit /b 1
)
echo.
echo ビルドが完了しました: src\Clyppey.App\bin\Release\net8.0-windows\Clyppey.exe
pause
