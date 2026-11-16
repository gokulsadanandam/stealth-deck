<#
  Generates assets\StealthDeck.ico (16-256 px, PNG-compressed frames): accent-blue rounded tile with a white "S".
  Re-run after changing the design:  powershell -File tools\make-icon.ps1
#>
Add-Type -AssemblyName System.Drawing

$root = Split-Path $PSScriptRoot -Parent
$outDir = Join-Path $root 'assets'
New-Item -ItemType Directory $outDir -Force | Out-Null
$outFile = Join-Path $outDir 'StealthDeck.ico'

function New-Frame([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.TextRenderingHint = 'AntiAliasGridFit'
    $g.Clear([System.Drawing.Color]::Transparent)

    $pad = [math]::Max(0.5, $size * 0.03)
    $d = ($size - 2 * $pad) * 0.5 * 0.5   # corner diameter = 25% of the tile
    $r = New-Object System.Drawing.RectangleF $pad, $pad, ($size - 2 * $pad), ($size - 2 * $pad)
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc($r.Left, $r.Top, $d, $d, 180, 90)
    $path.AddArc($r.Right - $d, $r.Top, $d, $d, 270, 90)
    $path.AddArc($r.Right - $d, $r.Bottom - $d, $d, $d, 0, 90)
    $path.AddArc($r.Left, $r.Bottom - $d, $d, $d, 90, 90)
    $path.CloseFigure()

    $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush $r, ([System.Drawing.Color]::FromArgb(0x60, 0xA5, 0xFA)), ([System.Drawing.Color]::FromArgb(0x25, 0x63, 0xEB)), 90
    $g.FillPath($grad, $path)

    $font = New-Object System.Drawing.Font 'Segoe UI', ([float]($size * 0.66)), ([System.Drawing.FontStyle]::Bold), ([System.Drawing.GraphicsUnit]::Pixel)
    $fmt = New-Object System.Drawing.StringFormat
    $fmt.Alignment = 'Center'; $fmt.LineAlignment = 'Center'
    $textRect = New-Object System.Drawing.RectangleF 0, ($size * 0.04), $size, $size
    $g.DrawString('S', $font, [System.Drawing.Brushes]::White, $textRect, $fmt)

    $g.Dispose(); $font.Dispose(); $grad.Dispose(); $path.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    return , $ms.ToArray()
}

$sizes = 16, 24, 32, 48, 64, 128, 256
$frames = $sizes | ForEach-Object { , (New-Frame $_) }

$stream = [System.IO.File]::Create($outFile)
$w = New-Object System.IO.BinaryWriter $stream
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)   # ICONDIR
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $len = $frames[$i].Length
    $w.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))   # width  (0 = 256)
    $w.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))   # height
    $w.Write([byte]0); $w.Write([byte]0)                      # palette, reserved
    $w.Write([uint16]1); $w.Write([uint16]32)                 # planes, bpp
    $w.Write([uint32]$len); $w.Write([uint32]$offset)
    $offset += $len
}
foreach ($f in $frames) { $w.Write($f) }
$w.Dispose(); $stream.Dispose()

"Wrote $outFile ($((Get-Item $outFile).Length) bytes, sizes: $($sizes -join ', '))"
