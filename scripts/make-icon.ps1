# Generates the Clyppey application icon (multi-size .ico with PNG frames).
# Design: dark rounded tile, lime replay ring, white play triangle.
param(
    [string]$OutPath = (Join-Path $PSScriptRoot "..\src\Clyppey.App\Assets\clyppey.ico")
)

Add-Type -AssemblyName System.Drawing

$Accent = [System.Drawing.Color]::FromArgb(255, 163, 230, 53)   # #A3E635
$Tile   = [System.Drawing.Color]::FromArgb(255, 18, 21, 27)    # #12151B
$White  = [System.Drawing.Color]::FromArgb(255, 245, 248, 252)

function New-IconBitmap([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)

    $pad = [Math]::Max(1, [int]($size * 0.03))
    $rect = New-Object System.Drawing.Rectangle($pad, $pad, ($size - 2 * $pad), ($size - 2 * $pad))
    $radius = [int]($size * 0.24)

    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $radius * 2
    $path.AddArc($rect.X, $rect.Y, $d, $d, 180, 90)
    $path.AddArc($rect.Right - $d, $rect.Y, $d, $d, 270, 90)
    $path.AddArc($rect.Right - $d, $rect.Bottom - $d, $d, $d, 0, 90)
    $path.AddArc($rect.X, $rect.Bottom - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    $brush = New-Object System.Drawing.SolidBrush($Tile)
    $g.FillPath($brush, $path)

    # Replay ring
    $penWidth = [Math]::Max(1.5, $size * 0.085)
    $pen = New-Object System.Drawing.Pen($Accent, $penWidth)
    $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $m = $size * 0.26
    $ring = New-Object System.Drawing.RectangleF($m, $m, ($size - 2 * $m), ($size - 2 * $m))
    $g.DrawArc($pen, $ring, -55, 280)

    # Arrow head at the end of the ring
    $cx = $ring.X + $ring.Width / 2
    $cy = $ring.Y + $ring.Height / 2
    $r = $ring.Width / 2
    $angle = [Math]::PI * (-55) / 180
    $ex = $cx + $r * [Math]::Cos($angle)
    $ey = $cy + $r * [Math]::Sin($angle)
    $ah = $size * 0.13
    $tri = @(
        (New-Object System.Drawing.PointF(($ex - $ah * 0.2), ($ey - $ah))),
        (New-Object System.Drawing.PointF(($ex + $ah * 1.0), ($ey + $ah * 0.15))),
        (New-Object System.Drawing.PointF(($ex - $ah * 0.9), ($ey + $ah * 0.45)))
    )
    $g.FillPolygon((New-Object System.Drawing.SolidBrush($Accent)), $tri)

    # Play triangle in the middle
    $tw = $size * 0.16
    $th = $size * 0.19
    $play = @(
        (New-Object System.Drawing.PointF(($cx - $tw * 0.45), ($cy - $th))),
        (New-Object System.Drawing.PointF(($cx + $tw * 0.85), $cy)),
        (New-Object System.Drawing.PointF(($cx - $tw * 0.45), ($cy + $th)))
    )
    $g.FillPolygon((New-Object System.Drawing.SolidBrush($White)), $play)

    $g.Dispose()
    return $bmp
}

$sizes = @(16, 24, 32, 48, 64, 128, 256)
$frames = @()
foreach ($s in $sizes) {
    $bmp = New-IconBitmap $s
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $frames += ,@($s, $ms.ToArray())
    $bmp.Dispose()
    $ms.Dispose()
}

$dir = Split-Path -Parent $OutPath
if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }

$stream = New-Object System.IO.MemoryStream
$writer = New-Object System.IO.BinaryWriter($stream)
$writer.Write([uint16]0)              # reserved
$writer.Write([uint16]1)              # type: icon
$writer.Write([uint16]$frames.Count)  # count

$offset = 6 + 16 * $frames.Count
foreach ($frame in $frames) {
    $size = $frame[0]
    $bytes = $frame[1]
    $dim = if ($size -ge 256) { 0 } else { $size }
    $writer.Write([byte]$dim)
    $writer.Write([byte]$dim)
    $writer.Write([byte]0)            # palette
    $writer.Write([byte]0)            # reserved
    $writer.Write([uint16]1)          # planes
    $writer.Write([uint16]32)         # bpp
    $writer.Write([uint32]$bytes.Length)
    $writer.Write([uint32]$offset)
    $offset += $bytes.Length
}
foreach ($frame in $frames) {
    $writer.Write([byte[]]$frame[1])
}
$writer.Flush()
[System.IO.File]::WriteAllBytes($OutPath, $stream.ToArray())
$writer.Dispose()
$stream.Dispose()

Write-Host "wrote $OutPath ($([Math]::Round((Get-Item $OutPath).Length / 1KB, 1)) KB)"
