param(
    [int]$Seconds = 20,
    [int]$PeriodMs = 2100,
    [switch]$PressHotkey,
    [string]$Hotkey = 'Ctrl+F10'
)

# Audio/video sync check, part 1: a full-screen white flash and a 1 kHz beep at the same instant,
# then (optionally) a hotkey press so the app saves a clip that contains them.
#
# Run this, then run measure-audio-sync.ps1 on the saved clip.
#
# Everything is written under <repo>\.synctest, never %TEMP%: an exe that lives inside the repo runs at
# low integrity and cannot write outside it (AGENTS.md §10), and ffmpeg writes the beep file. The measured offset is what the
# design in AGENTS.md §12/v3.8 is tuned against: measure, change, measure again.

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System

$root = Split-Path $PSScriptRoot -Parent
$ff = Join-Path $root 'tools\ffmpeg\ffmpeg.exe'
$wav = Join-Path $root '.synctest\beep.wav'
New-Item -ItemType Directory -Force (Split-Path $wav) | Out-Null

if (-not (Test-Path $wav)) {
    & $ff -y -v error -f lavfi -i 'sine=frequency=1000:duration=0.12:sample_rate=48000' -ac 1 -c:a pcm_s16le $wav
}

$player = New-Object System.Media.SoundPlayer $wav
$player.Load()

$form = New-Object System.Windows.Forms.Form
$form.FormBorderStyle = 'None'
$form.WindowState = 'Maximized'
$form.TopMost = $true
$form.BackColor = [System.Drawing.Color]::Black
$form.ShowInTaskbar = $false
$form.StartPosition = 'Manual'
$form.Bounds = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
$form.Show()
[System.Windows.Forms.Application]::DoEvents()

$sw = [Diagnostics.Stopwatch]::StartNew()
$next = 0.0
$count = 0
while ($sw.Elapsed.TotalSeconds -lt $Seconds) {
    if ($sw.Elapsed.TotalSeconds -ge $next) {
        $next += ($PeriodMs / 1000.0)
        $count++

        $form.BackColor = [System.Drawing.Color]::White
        [System.Windows.Forms.Application]::DoEvents()
        $player.Play()

        $flash = [Diagnostics.Stopwatch]::StartNew()
        while ($flash.Elapsed.TotalMilliseconds -lt 200) {
            [System.Windows.Forms.Application]::DoEvents()
            Start-Sleep -Milliseconds 5
        }

        $form.BackColor = [System.Drawing.Color]::Black
        [System.Windows.Forms.Application]::DoEvents()
    }

    [System.Windows.Forms.Application]::DoEvents()
    Start-Sleep -Milliseconds 5
}

if ($PressHotkey) {
    if (-not ([System.Management.Automation.PSTypeName]'ClyppeySyncKeys').Type) {
        Add-Type -Namespace '' -Name 'ClyppeySyncKeys' -MemberDefinition @'
[System.Runtime.InteropServices.DllImport("user32.dll")]
public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, System.UIntPtr dwExtraInfo);
'@
    }

    # "Ctrl+F10" -> VK 0x11 + 0x79. Only the modifier + F10/F9/F8 style hotkeys are supported here;
    # change the ids below if the app registered something else (see settings.json).
    $parts = $Hotkey.Split('+') | ForEach-Object { $_.Trim().ToUpperInvariant() }
    $mods = @()
    $key = 0
    foreach ($p in $parts) {
        switch ($p) {
            'CTRL' { $mods += 0x11 }
            'ALT' { $mods += 0x12 }
            'SHIFT' { $mods += 0x10 }
            default {
                if ($p -match '^F(\d+)$' -and [int]$Matches[1] -ge 1 -and [int]$Matches[1] -le 24) {
                    $key = 0x6F + [int]$Matches[1]    # F1 = 0x70
                }
            }
        }
    }

    foreach ($m in $mods) { [ClyppeySyncKeys]::keybd_event([byte]$m, 0, 0, [UIntPtr]::Zero); Start-Sleep -Milliseconds 40 }
    if ($key -gt 0) {
        [ClyppeySyncKeys]::keybd_event([byte]$key, 0, 0, [UIntPtr]::Zero)
        Start-Sleep -Milliseconds 60
        [ClyppeySyncKeys]::keybd_event([byte]$key, 0, 2, [UIntPtr]::Zero)
    }
    Start-Sleep -Milliseconds 40
    foreach ($m in $mods) { [ClyppeySyncKeys]::keybd_event([byte]$m, 0, 2, [UIntPtr]::Zero); Start-Sleep -Milliseconds 30 }
}

$form.Close()
$form.Dispose()
"flashes: $count"
