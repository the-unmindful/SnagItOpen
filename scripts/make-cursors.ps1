<# Generates the multi-resolution rotate cursor without external tools. Hotspot is the rotation centre. #>
param([string]$OutFile = (Join-Path $PSScriptRoot '..\src\SnagItOpen.App\Assets\Cursors\rotate.cur'))
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

function Make-Frame([int]$size) {
    $bitmap = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $scale = $size / 32.0
    $graphics.ScaleTransform($scale, $scale)
    $outline = New-Object System.Drawing.Pen([System.Drawing.Color]::White, 5)
    $ink = New-Object System.Drawing.Pen([System.Drawing.Color]::Black, 2.5)
    $outline.StartCap = $outline.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $ink.StartCap = $ink.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    # Circular arrow, readable over both light and dark artwork.
    $graphics.DrawArc($outline, 7, 7, 18, 18, 25, 285)
    $graphics.DrawArc($ink, 7, 7, 18, 18, 25, 285)
    $arrow = New-Object System.Drawing.Drawing2D.GraphicsPath
    $arrow.AddPolygon([System.Drawing.PointF[]]@(
        (New-Object System.Drawing.PointF(20, 3)),
        (New-Object System.Drawing.PointF(28, 10)),
        (New-Object System.Drawing.PointF(18, 12))))
    $graphics.DrawPath($outline, $arrow)
    $graphics.FillPath([System.Drawing.Brushes]::Black, $arrow)
    $graphics.Dispose(); $outline.Dispose(); $ink.Dispose(); $arrow.Dispose()

    $memory = New-Object System.IO.MemoryStream
    $writer = New-Object System.IO.BinaryWriter($memory)
    $maskStride = [int]([Math]::Ceiling($size / 32.0) * 4)
    $imageBytes = $size * $size * 4 + $maskStride * $size
    $writer.Write([uint32]40)
    $writer.Write([int32]$size); $writer.Write([int32]($size * 2))
    $writer.Write([uint16]1); $writer.Write([uint16]32)
    $writer.Write([uint32]0); $writer.Write([uint32]$imageBytes)
    for ($i = 0; $i -lt 4; $i++) { $writer.Write([uint32]0) }
    for ($y = $size - 1; $y -ge 0; $y--) {
        for ($x = 0; $x -lt $size; $x++) {
            $pixel = $bitmap.GetPixel($x, $y)
            $writer.Write([byte]$pixel.B); $writer.Write([byte]$pixel.G)
            $writer.Write([byte]$pixel.R); $writer.Write([byte]$pixel.A)
        }
    }
    for ($y = $size - 1; $y -ge 0; $y--) {
        $mask = New-Object byte[] $maskStride
        for ($x = 0; $x -lt $size; $x++) {
            if ($bitmap.GetPixel($x, $y).A -eq 0) {
                $index = [int][Math]::Floor($x / 8.0)
                $mask[$index] = $mask[$index] -bor (128 -shr ($x % 8))
            }
        }
        $writer.Write($mask)
    }
    $bitmap.Dispose(); $writer.Flush()
    $bytes = $memory.ToArray()
    $writer.Dispose(); $memory.Dispose()
    return ,$bytes
}

$sizes = 32, 48, 64
$frames = @($sizes | ForEach-Object { Make-Frame $_ })
$parent = Split-Path -Parent ([System.IO.Path]::GetFullPath($OutFile))
if (-not (Test-Path -LiteralPath $parent)) { [void](New-Item -ItemType Directory -Path $parent) }
$output = New-Object System.IO.BinaryWriter([System.IO.File]::Create($OutFile))
try {
    $output.Write([uint16]0); $output.Write([uint16]2); $output.Write([uint16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($i = 0; $i -lt $sizes.Count; $i++) {
        $output.Write([byte]$sizes[$i]); $output.Write([byte]$sizes[$i])
        $output.Write([byte]0); $output.Write([byte]0)
        $output.Write([uint16]($sizes[$i] / 2)); $output.Write([uint16]($sizes[$i] / 2))
        $output.Write([uint32]$frames[$i].Length); $output.Write([uint32]$offset)
        $offset += $frames[$i].Length
    }
    foreach ($frame in $frames) { $output.Write([byte[]]$frame) }
}
finally { $output.Dispose() }
Write-Output "Generated rotate cursor: $OutFile (32, 48, 64 px)"
