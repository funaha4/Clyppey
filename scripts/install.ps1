# Installs Clyppey to a normal per-user location and creates a desktop shortcut.
#
# Why not just run it from the repo? An executable that lives inside a sandboxed/monitored
# workspace folder can inherit that folder's restrictions (low integrity token, writes limited
# to the workspace). Installing under %LOCALAPPDATA%\Programs\Clyppey makes it an ordinary app
# that can write to Videos, %LOCALAPPDATA%, and so on.
#
# Usage: powershell -ExecutionPolicy Bypass -File scripts\install.ps1
[CmdletBinding()]
param(
    [string]$Destination = (Join-Path $env:LOCALAPPDATA "Programs\Clyppey"),
    [switch]$NoShortcut
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$project = Join-Path $root "src\Clyppey.App\Clyppey.App.csproj"
$ffmpeg = Join-Path $root "tools\ffmpeg\ffmpeg.exe"

Write-Host "ビルドして配置します: $Destination"
Get-Process Clyppey -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 400

if (Test-Path $Destination) {
    Remove-Item $Destination -Recurse -Force
}

dotnet publish $project -c Release --nologo -v q -o $Destination
if ($LASTEXITCODE -ne 0) { throw "publish に失敗しました" }

if (Test-Path $ffmpeg) {
    Copy-Item $ffmpeg (Join-Path $Destination "ffmpeg.exe") -Force
} else {
    Write-Warning "tools\ffmpeg\ffmpeg.exe がありません。scripts\setup-ffmpeg.ps1 を先に実行してください。"
}

$exe = Join-Path $Destination "Clyppey.exe"
if (-not (Test-Path $exe)) { throw "Clyppey.exe が見つかりません: $exe" }

if (-not $NoShortcut) {
    $shell = New-Object -ComObject WScript.Shell
    $desktop = [Environment]::GetFolderPath('Desktop')
    $link = Join-Path $desktop "Clyppey.lnk"
    $shortcut = $shell.CreateShortcut($link)
    $shortcut.TargetPath = $exe
    $shortcut.WorkingDirectory = $Destination
    $shortcut.IconLocation = "$exe,0"
    $shortcut.Description = "Clyppey - Instant Replay"
    $shortcut.Save()
    [System.Runtime.InteropServices.Marshal]::ReleaseComObject($shell) | Out-Null
    Write-Host "ショートカットを作成しました: $link"
}

Write-Host "完了しました。デスクトップの Clyppey から起動してください。"
Write-Host "  $exe"
