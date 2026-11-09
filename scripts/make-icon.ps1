<#
.SYNOPSIS
  Generates src/UpdateForge.Desktop/Assets/UpdateForge.ico (multi-size, PNG-compressed) and a 512px PNG.
  Re-run after changing the design below. Pure System.Drawing; no external tools or assets.
#>
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$root = Split-Path -Parent $PSScriptRoot
$outDir = Join-Path $root 'src/UpdateForge.Desktop/Assets'
New-Item -ItemType Directory -Force $outDir | Out-Null

function New-RoundedRect([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $p.AddArc($x, $y, $d, $d, 180, 90); $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90); $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure(); return $p
}

function New-IconBitmap([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.PixelOffsetMode = 'HighQuality'; $g.Clear([System.Drawing.Color]::Transparent)
    $s = [float]$size

    # Tile: rounded square, diagonal blue -> teal gradient
    $pad = $s * 0.04
    $tile = New-RoundedRect $pad $pad ($s - 2 * $pad) ($s - 2 * $pad) ($s * 0.22)
    $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush ((New-Object System.Drawing.PointF 0, 0), (New-Object System.Drawing.PointF $s, $s), ([System.Drawing.Color]::FromArgb(255, 37, 99, 235)), ([System.Drawing.Color]::FromArgb(255, 6, 182, 212)))
    $g.FillPath($grad, $tile)

    $white = [System.Drawing.Color]::White
    $cx = $s / 2; $cy = $s / 2

    # Refresh arc (about 270 degrees) with round caps, leaving a gap at the top-right for the arrowhead
    $rad = $s * 0.30
    $penW = [Math]::Max(1.6, $s * 0.085)
    $pen = New-Object System.Drawing.Pen $white, $penW
    $pen.StartCap = 'Round'; $pen.EndCap = 'Round'
    $startDeg = -30; $sweepDeg = 270                       # clockwise sweep; ends at 240 degrees, leaving a gap at the top
    $g.DrawArc($pen, ($cx - $rad), ($cy - $rad), (2 * $rad), (2 * $rad), $startDeg, $sweepDeg)

    # Arrowhead at the END of the sweep, pointing along the direction of travel (clockwise tangent) into the gap
    $a = ($startDeg + $sweepDeg) * [Math]::PI / 180
    $px = $cx + $rad * [Math]::Cos($a); $py = $cy + $rad * [Math]::Sin($a)
    $tx = -[Math]::Sin($a); $ty = [Math]::Cos($a)          # clockwise tangent (direction of travel)
    $nx = [Math]::Cos($a); $ny = [Math]::Sin($a)           # outward normal
    $len = $s * 0.20; $half = $s * 0.125
    $tip = New-Object System.Drawing.PointF ([float]($px + $tx * $len)), ([float]($py + $ty * $len))
    $b1 = New-Object System.Drawing.PointF ([float]($px + $nx * $half)), ([float]($py + $ny * $half))
    $b2 = New-Object System.Drawing.PointF ([float]($px - $nx * $half)), ([float]($py - $ny * $half))
    $g.FillPolygon((New-Object System.Drawing.SolidBrush $white), [System.Drawing.PointF[]]@($tip, $b1, $b2))

    # Up arrow in the middle (the "update")
    $aw = $s * 0.13; $ah = $s * 0.16; $shaft = $s * 0.055
    $top = $cy - $ah; $bottom = $cy + $s * 0.15
    $arrow = [System.Drawing.PointF[]]@(
        (New-Object System.Drawing.PointF $cx, $top),
        (New-Object System.Drawing.PointF ($cx + $aw), ($top + $ah * 0.95)),
        (New-Object System.Drawing.PointF ($cx + $shaft), ($top + $ah * 0.95)),
        (New-Object System.Drawing.PointF ($cx + $shaft), $bottom),
        (New-Object System.Drawing.PointF ($cx - $shaft), $bottom),
        (New-Object System.Drawing.PointF ($cx - $shaft), ($top + $ah * 0.95)),
        (New-Object System.Drawing.PointF ($cx - $aw), ($top + $ah * 0.95)))
    $g.FillPolygon((New-Object System.Drawing.SolidBrush $white), $arrow)

    $g.Dispose(); return $bmp
}

# The leading comma stops PowerShell from unrolling the byte[] into a pipeline of individual bytes.
function Get-PngBytes($bmp) { $ms = New-Object System.IO.MemoryStream; $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png); return , $ms.ToArray() }

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$images = @(foreach ($sz in $sizes) { $b = New-IconBitmap $sz; [pscustomobject]@{ Size = $sz; Bytes = [byte[]](Get-PngBytes $b) }; $b.Dispose() })

$icoPath = Join-Path $outDir 'UpdateForge.ico'
$fs = [System.IO.File]::Create($icoPath); $bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$images.Count)       # ICONDIR
$offset = 6 + 16 * $images.Count
foreach ($i in $images) {
    $dim = if ($i.Size -ge 256) { 0 } else { $i.Size }
    $bw.Write([byte]$dim); $bw.Write([byte]$dim); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32); $bw.Write([uint32]$i.Bytes.Length); $bw.Write([uint32]$offset)
    $offset += $i.Bytes.Length
}
foreach ($i in $images) { $bw.Write([byte[]]$i.Bytes) }
$bw.Dispose(); $fs.Dispose()

$big = New-IconBitmap 512; $big.Save((Join-Path $outDir 'UpdateForge-512.png'), [System.Drawing.Imaging.ImageFormat]::Png); $big.Dispose()
Write-Host "Wrote $icoPath ($((Get-Item $icoPath).Length) bytes, sizes: $($sizes -join ', '))"
