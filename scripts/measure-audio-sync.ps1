param(
    [Parameter(Mandatory = $true)][string]$Clip,
    [int]$Tail = 40
)

# Audio/video sync check, part 2: find the white flashes in the picture and the 1 kHz beeps in the
# sound, pair them up and report the median offset. Both are triggered at the same instant by
# test-audio-sync.ps1, so the offset is the A/V error of the recording path.
#
# Note the measurement has a small bias: the beep starts at once, but the white frame only reaches
# the capture a frame or two later, so a real error of 0 shows up as roughly -20 ms.

$ErrorActionPreference = 'Continue'
$root = Split-Path $PSScriptRoot -Parent
$ff = Join-Path $root 'tools\ffmpeg\ffmpeg.exe'
$work = Join-Path $root '.synctest'
New-Item -ItemType Directory -Force $work | Out-Null
$yavg = Join-Path $work 'clyppey-sync-yavg.txt'
$pcm = Join-Path $work 'clyppey-sync-audio.pcm'
Remove-Item $yavg, $pcm -Force -ErrorAction SilentlyContinue

# Only the tail matters (the stimulus is the last thing that happens). Seeking both sides by the
# same amount keeps them comparable while cutting the analysis to a few seconds of data.
Push-Location $work
try {
    & $ff -v error -nostdin -sseof "-$Tail" -i $Clip -an -vf "signalstats,metadata=print:key=lavfi.signalstats.YAVG:file=clyppey-sync-yavg.txt" -f null - 2>&1 | Out-Null
    & $ff -v error -nostdin -sseof "-$Tail" -i $Clip -vn -af "bandpass=f=1000:width_type=h:w=120" -ac 1 -ar 8000 -f s16le clyppey-sync-audio.pcm 2>&1 | Out-Null
}
finally {
    Pop-Location
}

if (-not (Test-Path $yavg)) { "ffmpeg did not write the luma trace"; exit 1 }

$luma = New-Object System.Collections.Generic.List[object]
$t = 0.0
foreach ($line in Get-Content $yavg) {
    if ($line -match 'pts_time:([0-9.]+)') { $t = [double]$Matches[1] }
    elseif ($line -match 'YAVG=([0-9.]+)') { $luma.Add([pscustomobject]@{ T = $t; Y = [double]$Matches[1] }) }
}

$flashes = New-Object System.Collections.Generic.List[double]
for ($i = 1; $i -lt $luma.Count; $i++) {
    $jump = $luma[$i].Y - $luma[$i - 1].Y
    if ($jump -gt 50 -and $luma[$i].Y -gt 90) { $flashes.Add($luma[$i].T) }
}

$bytes = [System.IO.File]::ReadAllBytes($pcm)
$n = [int]($bytes.Length / 2)
$win = 160
$count = [int][math]::Floor($n / $win)
$env = New-Object double[] $count
$acc = 0.0
$c = 0
$idx = 0
for ($i = 0; $i -lt ($count * $win); $i++) {
    $v = [BitConverter]::ToInt16($bytes, $i * 2) / 32768.0
    $acc += $v * $v
    $c++
    if ($c -eq $win) {
        $env[$idx] = [math]::Sqrt($acc / $win)
        $idx++
        $acc = 0
        $c = 0
    }
}

$peak = ($env | Measure-Object -Maximum).Maximum
$sortedEnv = @($env | Sort-Object)
$medianEnv = $sortedEnv[[int]($sortedEnv.Count / 2)]
$thr = [math]::Max(0.005, $medianEnv * 3.0)
if ($thr -gt $peak * 0.9) { $thr = $peak * 0.6 }

$beeps = New-Object System.Collections.Generic.List[double]
$last = -10.0
$quiet = 999
for ($i = 0; $i -lt $env.Length; $i++) {
    if ($env[$i] -gt $thr) {
        if ($quiet -ge 5) {
            $ts = $i * $win / 8000.0
            if ($ts - $last -gt 0.4) { $beeps.Add($ts); $last = $ts }
        }
        $quiet = 0
    }
    else { $quiet++ }
}

"clip          : $(Split-Path $Clip -Leaf)"
"video flashes : $($flashes.Count)"
"audio beeps   : $($beeps.Count)"

if ($flashes.Count -eq 0 -or $beeps.Count -eq 0) { "nothing to pair - was the stimulus on the captured monitor?"; exit 1 }

$deltas = New-Object System.Collections.Generic.List[double]
foreach ($f in $flashes) {
    $bestD = $null
    foreach ($b in $beeps) {
        $d = $b - $f
        if ([math]::Abs($d) -lt 0.6) {
            if ($null -eq $bestD -or [math]::Abs($d) -lt [math]::Abs($bestD)) { $bestD = $d }
        }
    }
    if ($null -ne $bestD) { $deltas.Add($bestD) }
}

if ($deltas.Count -eq 0) { "no pairs within 600 ms"; exit 1 }

$sorted = @($deltas | Sort-Object)
$median = $sorted[[int]($sorted.Count / 2)]
"pairs         : $($deltas.Count)"
"median offset : {0:N0} ms (audio minus video; positive = sound plays late)" -f ($median * 1000)
"per pair      : " + (($deltas | ForEach-Object { "{0:N0}" -f ($_ * 1000) }) -join ', ')

Remove-Item $yavg, $pcm -Force -ErrorAction SilentlyContinue
