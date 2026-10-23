# Fetches the bundled ffmpeg binary.
#
# Clyppey ships without ffmpeg in version control; this script downloads
# gyan.dev's official "ffmpeg 7.1 essentials" build (exactly what the app was
# verified against) from PyPI's imageio-ffmpeg wheel, which redistributes it.
#
# Why 7.1 and not the newest? ffmpeg 8/9 link against NVENC API 13.x, which only
# very recent NVIDIA drivers expose. 7.1 targets NVENC API 12.2, so hardware
# encoding works on both older and newer drivers.
#
# Usage: powershell -ExecutionPolicy Bypass -File scripts\setup-ffmpeg.ps1
[CmdletBinding()]
param(
    [string]$Destination = (Join-Path $PSScriptRoot "..\tools\ffmpeg")
)

$ErrorActionPreference = 'Stop'
$target = Join-Path $Destination 'ffmpeg.exe'

if (Test-Path $target) {
    Write-Host "ffmpeg は既にあります: $target"
    exit 0
}

New-Item -ItemType Directory -Force -Path $Destination | Out-Null
$work = Join-Path $env:TEMP ("clyppey-ffmpeg-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $work | Out-Null

try {
    Write-Host 'ffmpeg 7.1 を取得しています...'
    $downloaded = $null

    if (Get-Command python -ErrorAction SilentlyContinue) {
        python -m pip download imageio-ffmpeg --no-deps --only-binary=:all: `
            --platform win_amd64 --implementation py --python-version 3.11 -d $work | Out-Null
        $downloaded = Get-ChildItem $work -Filter '*.whl' | Select-Object -First 1
    }

    if (-not $downloaded) {
        # No Python: fetch the wheel straight from PyPI.
        $meta = Invoke-RestMethod -Uri 'https://pypi.org/pypi/imageio-ffmpeg/json' -TimeoutSec 60
        $file = $meta.urls | Where-Object { $_.filename -like '*win_amd64.whl' } | Select-Object -First 1
        if (-not $file) { throw 'PyPI から Windows 用 wheel が見つかりませんでした。' }
        $downloaded = Join-Path $work $file.filename
        Invoke-WebRequest -Uri $file.url -OutFile $downloaded -UseBasicParsing -TimeoutSec 300
    }

    $zip = Join-Path $work 'package.zip'
    Copy-Item $downloaded.FullName $zip -Force
    Expand-Archive -Path $zip -DestinationPath (Join-Path $work 'x') -Force

    $exe = Get-ChildItem (Join-Path $work 'x') -Recurse -Filter 'ffmpeg-win-*.exe' | Select-Object -First 1
    if (-not $exe) { throw 'wheel 内に ffmpeg が見つかりませんでした。' }

    Copy-Item $exe.FullName $target -Force
    Write-Host "配置しました: $target"
    & $target -hide_banner -version | Select-Object -First 1
}
finally {
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}
