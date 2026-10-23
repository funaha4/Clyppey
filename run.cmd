@echo off
rem Launches the installed copy when present (it has normal file permissions),
rem otherwise builds and runs straight from the repository.
setlocal
set "INSTALLED=%LOCALAPPDATA%\Programs\Clyppey\Clyppey.exe"
if exist "%INSTALLED%" (
  start "" "%INSTALLED%" %*
  exit /b 0
)
cd /d "%~dp0"
echo インストール版が見つからないため、リポジトリからビルドして起動します。
echo 保存先に書き込めない場合は install.cmd を実行してください。
dotnet build "src\Clyppey.App\Clyppey.App.csproj" -c Release --nologo
if errorlevel 1 (
  echo ビルドに失敗しました。
  pause
  exit /b 1
)
start "" "src\Clyppey.App\bin\Release\net8.0-windows\Clyppey.exe" %*
