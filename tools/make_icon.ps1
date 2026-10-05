# Regenerates the ViLogger app icon (all Windows sizes) and an optional 256 px preview.
# Usage (from the repo root):
#   pwsh tools/make_icon.ps1 -OutIco src/EverLogger.App/Resources/app.ico -PreviewPng docs/icon.png
param(
    [string]$OutIco,
    [string]$PreviewPng
)
Add-Type -AssemblyName System.Drawing

function New-RoundedRect([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

function Render([int]$s) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)
    $k = $s / 256.0

    # Background tile (Nightfall card colors)
    $inset = [Math]::Max(0.5, 8 * $k)
    $tile = New-RoundedRect $inset $inset ($s - 2 * $inset) ($s - 2 * $inset) ([Math]::Max(2.5, 52 * $k))
    $bg = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.PointF 0, 0), (New-Object System.Drawing.PointF 0, $s), ([System.Drawing.Color]::FromArgb(255, 0x22, 0x2E, 0x3D)), ([System.Drawing.Color]::FromArgb(255, 0x10, 0x15, 0x1C))
    $g.FillPath($bg, $tile)
    $border = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(150, 0x53, 0x9B, 0xF5)), ([Math]::Max(1.0, 6 * $k))
    $g.DrawPath($border, $tile)

    # Waveform: square dips on both sides, a deep slanted "V" notch in the middle
    $pts = @(
        @(30, 88), @(48, 88), @(48, 136), @(72, 136), @(72, 88), @(90, 88),
        @(128, 192),
        @(166, 88), @(184, 88), @(184, 136), @(208, 136), @(208, 88), @(226, 88)
    )
    [System.Drawing.PointF[]]$points = foreach ($p in $pts) { New-Object System.Drawing.PointF ([float]($p[0] * $k)), ([float]($p[1] * $k)) }
    [System.Drawing.PointF[]]$vPoints = $points[5..7]
    $penW = [Math]::Max(1.8, 20 * $k)
    $glow = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(60, 0x6C, 0xB6, 0xFF)), ($penW * 1.8)
    $glow.LineJoin = 'Round'; $glow.StartCap = 'Round'; $glow.EndCap = 'Round'
    $wave = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255, 0x53, 0x9B, 0xF5)), $penW
    $wave.LineJoin = 'Round'; $wave.StartCap = 'Round'; $wave.EndCap = 'Round'
    $vPen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255, 0xD8, 0xEB, 0xFF)), $penW
    $vPen.LineJoin = 'Round'; $vPen.StartCap = 'Round'; $vPen.EndCap = 'Round'
    if ($s -ge 32) { $g.DrawLines($glow, $points) }
    $g.DrawLines($wave, $points)
    $g.DrawLines($vPen, $vPoints)

    # Log lines under the waveform (only where there is room)
    if ($s -ge 48) {
        $lineBrush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(150, 0x8B, 0x9B, 0xB0))
        $lh = [Math]::Max(2, 12 * $k)
        $g.FillPath($lineBrush, (New-RoundedRect (30 * $k) (176 * $k) (50 * $k) $lh ($lh / 2)))
        $g.FillPath($lineBrush, (New-RoundedRect (176 * $k) (176 * $k) (50 * $k) $lh ($lh / 2)))
    }

    $g.Dispose()
    return $bmp
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$images = @()
foreach ($s in $sizes) {
    $bmp = Render $s
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $images += , @($s, $ms.ToArray())
    if ($s -eq 256 -and $PreviewPng) { $bmp.Save($PreviewPng, [System.Drawing.Imaging.ImageFormat]::Png) }
    $bmp.Dispose()
}

# Write ICO container with PNG-compressed entries
New-Item -ItemType Directory -Force (Split-Path $OutIco) | Out-Null
$fs = [System.IO.File]::Create($OutIco)
$bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$images.Count)
$offset = 6 + 16 * $images.Count
foreach ($img in $images) {
    $s = $img[0]; $data = $img[1]
    $dim = if ($s -ge 256) { 0 } else { $s }
    $bw.Write([byte]$dim); $bw.Write([byte]$dim); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([UInt16]1); $bw.Write([UInt16]32)
    $bw.Write([UInt32]$data.Length); $bw.Write([UInt32]$offset)
    $offset += $data.Length
}
foreach ($img in $images) { $bw.Write([byte[]]$img[1]) }
$bw.Close()
"Wrote $OutIco ($((Get-Item $OutIco).Length) bytes)"
