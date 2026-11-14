# Regenerates the ARVSerialTool app icon (all Windows sizes) and an optional 256 px preview.
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

function New-FittedTextPath([string]$text, [float]$targetX, [float]$targetY, [float]$targetW, [float]$targetH) {
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $family = New-Object System.Drawing.FontFamily 'Segoe UI'
    $path.AddString(
        $text,
        $family,
        [int][System.Drawing.FontStyle]::Bold,
        [float]100.0,
        [System.Drawing.PointF]::Empty,
        [System.Drawing.StringFormat]::GenericTypographic)
    $family.Dispose()

    $bounds = $path.GetBounds()
    if ($bounds.Width -gt 0 -and $bounds.Height -gt 0) {
        $scale = [Math]::Min($targetW / $bounds.Width, $targetH / $bounds.Height)
        $cx = $targetX + $targetW / 2.0
        $cy = $targetY + $targetH / 2.0

        $matrix = New-Object System.Drawing.Drawing2D.Matrix
        $matrix.Translate([float]$cx, [float]$cy)
        $matrix.Scale([float]$scale, [float]$scale)
        $matrix.Translate([float](-$bounds.X - $bounds.Width / 2.0), [float](-$bounds.Y - $bounds.Height / 2.0))
        $path.Transform($matrix)
        $matrix.Dispose()
    }
    return $path
}

function Render([int]$s) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.InterpolationMode = 'HighQualityBicubic'
    $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)
    $k = $s / 256.0

    # Background squircle tile (Nightfall card palette)
    $inset = [Math]::Max(0.5, 8 * $k)
    $tile = New-RoundedRect $inset $inset ($s - 2 * $inset) ($s - 2 * $inset) ([Math]::Max(2.5, 52 * $k))
    $bg = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.PointF 0, 0), (New-Object System.Drawing.PointF 0, $s), ([System.Drawing.Color]::FromArgb(255, 0x1C, 0x26, 0x36)), ([System.Drawing.Color]::FromArgb(255, 0x0A, 0x0F, 0x17))
    $g.FillPath($bg, $tile)

    # Clip interior details cleanly inside the squircle bounds
    $g.SetClip($tile)

    if ($s -ge 40) {
        # Top terminal header bar
        $headerH = [float](72 * $k)
        $headerBrush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 0x10, 0x17, 0x22))
        $g.FillRectangle($headerBrush, [float]0, [float]0, [float]$s, $headerH)
        $headerBrush.Dispose()

        $divPen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255, 0x28, 0x3A, 0x50)), ([float][Math]::Max(1.0, 2.2 * $k))
        $g.DrawLine($divPen, [float]0, $headerH, [float]$s, $headerH)
        $divPen.Dispose()

        # Left terminal prompt ">_"
        [System.Drawing.PointF[]]$chevPts = @(
            (New-Object System.Drawing.PointF ([float](34 * $k)), ([float](33 * $k))),
            (New-Object System.Drawing.PointF ([float](48 * $k)), ([float](44 * $k))),
            (New-Object System.Drawing.PointF ([float](34 * $k)), ([float](55 * $k)))
        )
        [System.Drawing.PointF[]]$underPts = @(
            (New-Object System.Drawing.PointF ([float](55 * $k)), ([float](55 * $k))),
            (New-Object System.Drawing.PointF ([float](74 * $k)), ([float](55 * $k)))
        )

        $promptGlowW = [float](13.0 * $k)
        $promptGlow = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(60, 0x10, 0xB9, 0x81)), $promptGlowW
        $promptGlow.LineJoin = 'Round'; $promptGlow.StartCap = 'Round'; $promptGlow.EndCap = 'Round'
        $promptPen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255, 0x34, 0xD3, 0x99)), ([float](6.5 * $k))
        $promptPen.LineJoin = 'Round'; $promptPen.StartCap = 'Round'; $promptPen.EndCap = 'Round'

        $g.DrawLines($promptGlow, $chevPts)
        $g.DrawLines($promptGlow, $underPts)
        $g.DrawLines($promptPen, $chevPts)
        $g.DrawLines($promptPen, $underPts)
        $promptGlow.Dispose()
        $promptPen.Dispose()

        # Right RX / TX status LEDs
        $ledD = [float][Math]::Max(2.0, 12 * $k)
        $ledR = $ledD / 2.0
        $rxBrush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 0x34, 0xD3, 0x99))
        $txBrush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 0x53, 0x9B, 0xF5))
        $g.FillEllipse($rxBrush, [float](192 * $k - $ledR), [float](44 * $k - $ledR), $ledD, $ledD)
        $g.FillEllipse($txBrush, [float](214 * $k - $ledR), [float](44 * $k - $ledR), $ledD, $ledD)
        $rxBrush.Dispose()
        $txBrush.Dispose()

        # Center hero "ARV" wordmark
        $textPath = New-FittedTextPath 'ARV' (36 * $k) (88 * $k) (184 * $k) (68 * $k)
        $textGlowPen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(65, 0x53, 0x9B, 0xF5)), ([float](9.0 * $k))
        $textGlowPen.LineJoin = 'Round'
        $textReinforcePen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255, 0xFF, 0xFF, 0xFF)), ([float](1.5 * $k))
        $textReinforcePen.LineJoin = 'Round'
        $textFill = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.PointF 0, ([float](86 * $k))), (New-Object System.Drawing.PointF 0, ([float](158 * $k))), ([System.Drawing.Color]::FromArgb(255, 0xFF, 0xFF, 0xFF)), ([System.Drawing.Color]::FromArgb(255, 0xDC, 0xEB, 0xFF))

        $g.DrawPath($textGlowPen, $textPath)
        $g.DrawPath($textReinforcePen, $textPath)
        $g.FillPath($textFill, $textPath)
        $textGlowPen.Dispose()
        $textReinforcePen.Dispose()
        $textFill.Dispose()
        $textPath.Dispose()

        # Bottom UART digital pulse waveform
        $pts = @(
            @(30, 180), @(54, 180), @(54, 212), @(82, 212), @(82, 180), @(106, 180),
            @(106, 212), @(124, 212), @(124, 180), @(158, 180), @(158, 212), @(190, 212),
            @(190, 180), @(226, 180)
        )
        [System.Drawing.PointF[]]$points = foreach ($p in $pts) { New-Object System.Drawing.PointF ([float]($p[0] * $k)), ([float]($p[1] * $k)) }

        $waveGlow = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(70, 0x38, 0x8B, 0xFF)), ([float](18.0 * $k))
        $waveGlow.LineJoin = 'Round'; $waveGlow.StartCap = 'Round'; $waveGlow.EndCap = 'Round'
        $g.DrawLines($waveGlow, $points)
        $waveGlow.Dispose()

        $waveGrad = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.PointF ([float](26 * $k)), 0), (New-Object System.Drawing.PointF ([float](230 * $k)), 0), ([System.Drawing.Color]::FromArgb(255, 0x34, 0xD3, 0x99)), ([System.Drawing.Color]::FromArgb(255, 0x79, 0xC0, 0xFF))
        $cb = New-Object System.Drawing.Drawing2D.ColorBlend
        $cb.Colors = [System.Drawing.Color[]]@(
            [System.Drawing.Color]::FromArgb(255, 0x34, 0xD3, 0x99),
            [System.Drawing.Color]::FromArgb(255, 0x53, 0x9B, 0xF5),
            [System.Drawing.Color]::FromArgb(255, 0x79, 0xC0, 0xFF)
        )
        $cb.Positions = [float[]]@(0.0, 0.5, 1.0)
        $waveGrad.InterpolationColors = $cb

        $wavePen = New-Object System.Drawing.Pen $waveGrad, ([float](8.5 * $k))
        $wavePen.LineJoin = 'Round'; $wavePen.StartCap = 'Round'; $wavePen.EndCap = 'Round'
        $g.DrawLines($wavePen, $points)
        $wavePen.Dispose()
        $waveGrad.Dispose()
    }
    else {
        # Compact LOD for taskbar / list icons (16, 20, 24, 32 px): omit tiny header bar
        $textPath = New-FittedTextPath 'ARV' (22 * $k) (40 * $k) (212 * $k) (92 * $k)
        $textPen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255, 0xFF, 0xFF, 0xFF)), ([float][Math]::Max(0.7, 4.0 * $k))
        $textPen.LineJoin = 'Round'
        $textBrush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 0xFF, 0xFF, 0xFF))

        $g.DrawPath($textPen, $textPath)
        $g.FillPath($textBrush, $textPath)
        $textPen.Dispose()
        $textBrush.Dispose()
        $textPath.Dispose()

        $pts = @(
            @(26, 162), @(62, 162), @(62, 206), @(110, 206), @(110, 162),
            @(150, 162), @(150, 206), @(194, 206), @(194, 162), @(230, 162)
        )
        [System.Drawing.PointF[]]$points = foreach ($p in $pts) { New-Object System.Drawing.PointF ([float]($p[0] * $k)), ([float]($p[1] * $k)) }

        $waveGrad = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.PointF ([float](22 * $k)), 0), (New-Object System.Drawing.PointF ([float](234 * $k)), 0), ([System.Drawing.Color]::FromArgb(255, 0x34, 0xD3, 0x99)), ([System.Drawing.Color]::FromArgb(255, 0x58, 0xA6, 0xFF))
        $wavePen = New-Object System.Drawing.Pen $waveGrad, ([float][Math]::Max(1.5, 13.0 * $k))
        $wavePen.LineJoin = 'Round'; $wavePen.StartCap = 'Round'; $wavePen.EndCap = 'Round'
        $g.DrawLines($wavePen, $points)
        $wavePen.Dispose()
        $waveGrad.Dispose()
    }

    $g.ResetClip()

    $border = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(175, 0x53, 0x9B, 0xF5)), ([float][Math]::Max(1.0, 5.5 * $k))
    $g.DrawPath($border, $tile)
    $border.Dispose()
    $bg.Dispose()
    $tile.Dispose()
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
