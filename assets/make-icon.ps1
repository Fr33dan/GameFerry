<#
    Draws the application logo and writes it as app.ico (every size Windows asks for) and logo.png.

        pwsh assets\make-icon.ps1

    The logo is a game disc going onto an SD card, which carries an arrow for the copy, on a blue rounded tile.
    It is drawn from scratch at each size rather than scaled down, and the smallest sizes leave out the fine
    detail.
#>
param(
    [string] $OutputFolder = $PSScriptRoot,

    # Optional: also write a sheet showing every size on light and dark backgrounds, for checking by eye.
    [string] $PreviewPath
)

Add-Type -AssemblyName System.Drawing

$IconSizes = 16, 20, 24, 32, 40, 48, 64, 128, 256

function New-Color([int] $r, [int] $g, [int] $b, [int] $a = 255) { [Drawing.Color]::FromArgb($a, $r, $g, $b) }

function New-RoundedRectangle([float] $x, [float] $y, [float] $w, [float] $h, [float] $radius) {
    $d = 2 * $radius
    $path = [Drawing.Drawing2D.GraphicsPath]::new()
    $path.AddArc($x, $y, $d, $d, 180, 90)
    $path.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $path.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $path.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    $path
}

# Everything is laid out on a 256-unit square and scaled to the size being drawn.
function New-LogoBitmap([int] $size) {
    $bitmap = [Drawing.Bitmap]::new($size, $size, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [Drawing.Graphics]::FromImage($bitmap)
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'HighQuality'
    $g.ScaleTransform($size / 256.0, $size / 256.0)

    $detailed = $size -ge 48     # room for the disc's sheen, rim and shadow
    $labelled = $size -ge 32     # room for the arrow
    $contacts = $size -ge 64     # room for the card's gold contacts

    # Tile
    $tile = New-RoundedRectangle 6 6 244 244 58
    $tileBrush = [Drawing.Drawing2D.LinearGradientBrush]::new(
        [Drawing.PointF]::new(0, 0), [Drawing.PointF]::new(256, 256), (New-Color 79 195 247), (New-Color 2 119 189))
    $g.FillPath($tileBrush, $tile)

    # Disc
    $cx = 104; $cy = 104; $radius = 80
    if ($detailed) {
        $shadow = [Drawing.SolidBrush]::new((New-Color 1 60 110 70))
        $g.FillEllipse($shadow, $cx - $radius + 5, $cy - $radius + 8, 2 * $radius, 2 * $radius)
    }

    $discBrush = [Drawing.Drawing2D.LinearGradientBrush]::new(
        [Drawing.PointF]::new($cx - $radius, $cy - $radius), [Drawing.PointF]::new($cx + $radius, $cy + $radius),
        (New-Color 255 255 255), (New-Color 214 236 250))
    $g.FillEllipse($discBrush, $cx - $radius, $cy - $radius, 2 * $radius, 2 * $radius)

    if ($detailed) {
        # Two soft wedges of reflected light across the recorded area.
        $disc = [Drawing.Drawing2D.GraphicsPath]::new()
        $disc.AddEllipse($cx - $radius, $cy - $radius, 2 * $radius, 2 * $radius)
        $g.SetClip($disc)
        $sheen = [Drawing.SolidBrush]::new((New-Color 41 182 246 70))
        $g.FillPie($sheen, $cx - $radius, $cy - $radius, 2 * $radius, 2 * $radius, 200, 46)
        $g.FillPie($sheen, $cx - $radius, $cy - $radius, 2 * $radius, 2 * $radius, 20, 46)
        $g.ResetClip()

        $rim = [Drawing.Pen]::new((New-Color 129 212 250), 3)
        $g.DrawEllipse($rim, $cx - $radius + 1.5, $cy - $radius + 1.5, 2 * $radius - 3, 2 * $radius - 3)

        $hub = 34
        $g.FillEllipse([Drawing.Brushes]::White, $cx - $hub, $cy - $hub, 2 * $hub, 2 * $hub)
        $g.DrawEllipse($rim, $cx - $hub, $cy - $hub, 2 * $hub, 2 * $hub)
    }

    $hole = if ($detailed) { 14 } else { 20 }
    $holeBrush = [Drawing.SolidBrush]::new((New-Color 2 136 209))
    $g.FillEllipse($holeBrush, $cx - $hole, $cy - $hole, 2 * $hole, 2 * $hole)

    # SD card, with its clipped corner. It sits wholly inside the tile's rounded corner.
    $left = 136; $top = 116; $right = 226; $bottom = 232; $clip = 28; $corner = 16; $d = 2 * $corner
    $card = [Drawing.Drawing2D.GraphicsPath]::new()
    $card.AddLine($left + $clip, $top, $right - $corner, $top)
    $card.AddArc($right - $d, $top, $d, $d, 270, 90)
    $card.AddArc($right - $d, $bottom - $d, $d, $d, 0, 90)
    $card.AddArc($left, $bottom - $d, $d, $d, 90, 90)
    $card.AddLine($left, $bottom - $corner, $left, $top + $clip)
    $card.CloseFigure()

    # A white edge keeps the card apart from the disc and the tile behind it.
    $edgeWidth = if ($size -ge 48) { 14 } elseif ($size -ge 24) { 20 } else { 26 }
    $edge = [Drawing.Pen]::new([Drawing.Color]::White, $edgeWidth)
    $edge.LineJoin = 'Round'
    $g.DrawPath($edge, $card)

    $cardBrush = [Drawing.Drawing2D.LinearGradientBrush]::new(
        [Drawing.PointF]::new($left, $top), [Drawing.PointF]::new($right, $bottom), (New-Color 30 99 190), (New-Color 13 62 148))
    $g.FillPath($cardBrush, $card)

    if ($contacts) {
        $gold = [Drawing.SolidBrush]::new((New-Color 255 213 79))
        foreach ($i in 0..3) {
            $contact = New-RoundedRectangle (172 + $i * 12) 127 7 19 3
            $g.FillPath($gold, $contact)
        }
    }

    if ($labelled) {
        # An arrow pointing down onto the card: a shaft and a head, with the corners rounded off by the outline.
        $cx = ($left + $right) / 2
        $arrow = [Drawing.PointF[]]@(
            [Drawing.PointF]::new($cx - 8, 160), [Drawing.PointF]::new($cx + 8, 160), [Drawing.PointF]::new($cx + 8, 186),
            [Drawing.PointF]::new($cx + 23, 186), [Drawing.PointF]::new($cx, 212), [Drawing.PointF]::new($cx - 23, 186),
            [Drawing.PointF]::new($cx - 8, 186))
        $outline = [Drawing.Pen]::new([Drawing.Color]::White, 6)
        $outline.LineJoin = 'Round'
        $g.FillPolygon([Drawing.Brushes]::White, $arrow)
        $g.DrawPolygon($outline, $arrow)
    }

    $g.Dispose()
    $bitmap
}

# The functions below return byte arrays; the leading comma stops PowerShell unrolling them into single bytes.
function Get-PngBytes([Drawing.Bitmap] $bitmap) {
    $stream = [IO.MemoryStream]::new()
    $bitmap.Save($stream, [Drawing.Imaging.ImageFormat]::Png)
    , $stream.ToArray()
}

# An icon image in the classic form: a bottom-up 32-bit bitmap followed by a 1-bit transparency mask.
function Get-IconBitmapBytes([Drawing.Bitmap] $bitmap) {
    $size = $bitmap.Width
    $rect = [Drawing.Rectangle]::new(0, 0, $size, $size)
    $data = $bitmap.LockBits($rect, 'ReadOnly', [Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $pixels = [byte[]]::new($size * $size * 4)
    for ($row = 0; $row -lt $size; $row++) {
        [Runtime.InteropServices.Marshal]::Copy([IntPtr]::Add($data.Scan0, $row * $data.Stride), $pixels, $row * $size * 4, $size * 4)
    }
    $bitmap.UnlockBits($data)

    $maskStride = [int][Math]::Ceiling($size / 32.0) * 4
    $stream = [IO.MemoryStream]::new()
    $writer = [IO.BinaryWriter]::new($stream)
    $writer.Write([int]40); $writer.Write([int]$size); $writer.Write([int]($size * 2))
    $writer.Write([int16]1); $writer.Write([int16]32); $writer.Write([int]0)
    $writer.Write([int]($size * $size * 4 + $maskStride * $size))
    $writer.Write([int]0); $writer.Write([int]0); $writer.Write([int]0); $writer.Write([int]0)

    for ($row = $size - 1; $row -ge 0; $row--) { $writer.Write($pixels, $row * $size * 4, $size * 4) }
    for ($row = $size - 1; $row -ge 0; $row--) {
        $mask = [byte[]]::new($maskStride)
        for ($x = 0; $x -lt $size; $x++) {
            if ($pixels[($row * $size + $x) * 4 + 3] -eq 0) { $mask[$x -shr 3] = $mask[$x -shr 3] -bor (0x80 -shr ($x -band 7)) }
        }
        $writer.Write($mask)
    }
    $writer.Flush()
    , $stream.ToArray()
}

function Write-Icon([string] $path, [Drawing.Bitmap[]] $bitmaps) {
    # 256-pixel images are stored as PNG, the smaller ones as bitmaps, which every Windows icon reader accepts.
    $images = [Collections.Generic.List[byte[]]]::new()
    foreach ($bitmap in $bitmaps) {
        [byte[]] $image = if ($bitmap.Width -ge 256) { Get-PngBytes $bitmap } else { Get-IconBitmapBytes $bitmap }
        $images.Add($image)
    }

    $stream = [IO.File]::Create($path)
    $writer = [IO.BinaryWriter]::new($stream)
    $writer.Write([int16]0); $writer.Write([int16]1); $writer.Write([int16]$bitmaps.Count)

    $offset = 6 + 16 * $bitmaps.Count
    for ($i = 0; $i -lt $bitmaps.Count; $i++) {
        $side = if ($bitmaps[$i].Width -ge 256) { 0 } else { $bitmaps[$i].Width }
        $writer.Write([byte]$side); $writer.Write([byte]$side); $writer.Write([byte]0); $writer.Write([byte]0)
        $writer.Write([int16]1); $writer.Write([int16]32)
        $writer.Write([int]$images[$i].Length); $writer.Write([int]$offset)
        $offset += $images[$i].Length
    }
    foreach ($image in $images) { $writer.Write($image) }
    $writer.Dispose()
}

function Write-Preview([string] $path, [Drawing.Bitmap[]] $bitmaps) {
    $sheet = [Drawing.Bitmap]::new(1180, 620)
    $g = [Drawing.Graphics]::FromImage($sheet)
    $backgrounds = (New-Color 243 243 243), (New-Color 32 32 32)
    for ($band = 0; $band -lt 2; $band++) {
        $g.FillRectangle([Drawing.SolidBrush]::new($backgrounds[$band]), 0, $band * 310, 1180, 310)
        $x = 20
        foreach ($bitmap in $bitmaps) {
            $g.InterpolationMode = 'NearestNeighbor'
            $g.DrawImage($bitmap, $x, $band * 310 + 20, $bitmap.Width, $bitmap.Height)
            # Small sizes again at four times life size, pixel for pixel.
            if ($bitmap.Width -le 48) {
                $g.PixelOffsetMode = 'Half'
                $g.DrawImage($bitmap, $x, $band * 310 + 100, $bitmap.Width * 4, $bitmap.Height * 4)
                $x += $bitmap.Width * 4 + 16
            }
            else {
                $x += $bitmap.Width + 16
            }
        }
    }
    $g.Dispose()
    $sheet.Save($path, [Drawing.Imaging.ImageFormat]::Png)
}

$bitmaps = [Drawing.Bitmap[]]@($IconSizes | ForEach-Object { New-LogoBitmap $_ })
Write-Icon (Join-Path $OutputFolder 'app.ico') $bitmaps
(New-LogoBitmap 512).Save((Join-Path $OutputFolder 'logo.png'), [Drawing.Imaging.ImageFormat]::Png)
if ($PreviewPath) { Write-Preview $PreviewPath $bitmaps }

"Wrote app.ico ($($IconSizes -join ', ') px) and logo.png to $OutputFolder"
