$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName PresentationCore
Add-Type -AssemblyName PresentationFramework
Add-Type -AssemblyName WindowsBase

$desktopRoot = Split-Path $PSScriptRoot -Parent
$assetsRoot = Join-Path $desktopRoot 'src\PadP2P.App\Assets'
$svgPath = Join-Path $assetsRoot 'peer-to-peer.svg'
if (-not (Test-Path -LiteralPath $svgPath)) { throw "Source icon not found: $svgPath" }

[xml]$svg = Get-Content -LiteralPath $svgPath -Raw
$viewBoxParts = $svg.DocumentElement.GetAttribute('viewBox') -split '[ ,]+' | Where-Object { $_ }
if ($viewBoxParts.Count -ne 4) { throw "Invalid SVG viewBox in $svgPath" }
$viewBox = [double[]]@($viewBoxParts | ForEach-Object { [double]::Parse($_, [Globalization.CultureInfo]::InvariantCulture) })
$shapes = $svg.SelectNodes("//*[local-name()='rect' or local-name()='circle' or local-name()='path']")
if ($shapes.Count -eq 0) { throw "No drawable SVG shapes were found in $svgPath" }

$minX = [double]::PositiveInfinity
$minY = [double]::PositiveInfinity
$maxX = [double]::NegativeInfinity
$maxY = [double]::NegativeInfinity
foreach ($shape in $shapes) {
    $shapeBounds = switch ($shape.LocalName) {
        'rect' {
            [System.Windows.Rect]::new(
                [double]::Parse($shape.GetAttribute('x'), [Globalization.CultureInfo]::InvariantCulture),
                [double]::Parse($shape.GetAttribute('y'), [Globalization.CultureInfo]::InvariantCulture),
                [double]::Parse($shape.GetAttribute('width'), [Globalization.CultureInfo]::InvariantCulture),
                [double]::Parse($shape.GetAttribute('height'), [Globalization.CultureInfo]::InvariantCulture))
        }
        'circle' {
            $cx = [double]::Parse($shape.GetAttribute('cx'), [Globalization.CultureInfo]::InvariantCulture)
            $cy = [double]::Parse($shape.GetAttribute('cy'), [Globalization.CultureInfo]::InvariantCulture)
            $radius = [double]::Parse($shape.GetAttribute('r'), [Globalization.CultureInfo]::InvariantCulture)
            [System.Windows.Rect]::new($cx - $radius, $cy - $radius, 2 * $radius, 2 * $radius)
        }
        'path' { [System.Windows.Media.Geometry]::Parse($shape.GetAttribute('d')).Bounds }
        default { throw "Unsupported SVG shape: $($shape.LocalName)" }
    }
    $strokeText = $shape.GetAttribute('stroke')
    if ($shape.GetAttribute('class') -eq 'link') { $strokeText = '#07877f' }
    if (-not [string]::IsNullOrWhiteSpace($strokeText) -and $strokeText -ne 'none') {
        $strokeWidthText = $shape.GetAttribute('stroke-width')
        $strokeWidth = if ($strokeWidthText) {
            [double]::Parse($strokeWidthText, [Globalization.CultureInfo]::InvariantCulture)
        }
        else { 1 }
        $shapeBounds.Inflate($strokeWidth / 2, $strokeWidth / 2)
    }
    $minX = [Math]::Min($minX, $shapeBounds.Left)
    $minY = [Math]::Min($minY, $shapeBounds.Top)
    $maxX = [Math]::Max($maxX, $shapeBounds.Right)
    $maxY = [Math]::Max($maxY, $shapeBounds.Bottom)
}
$artBounds = [System.Windows.Rect]::new($minX, $minY, $maxX - $minX, $maxY - $minY)

$iconSizes = @(16, 24, 32, 48, 64, 128, 256)
$palettes = @{
    Light = @{
        Foreground = [System.Windows.Media.Color]([System.Windows.Media.ColorConverter]::ConvertFromString('#26364d'))
        Accent = [System.Windows.Media.Color]([System.Windows.Media.ColorConverter]::ConvertFromString('#07877f'))
        BadgeBackground = [System.Windows.Media.Color]([System.Windows.Media.ColorConverter]::ConvertFromString('#eff5fa'))
        StatusOutline = [System.Windows.Media.Color]([System.Windows.Media.ColorConverter]::ConvertFromString('#ffffff'))
    }
    Dark = @{
        Foreground = [System.Windows.Media.Color]([System.Windows.Media.ColorConverter]::ConvertFromString('#edf4ff'))
        Accent = [System.Windows.Media.Color]([System.Windows.Media.ColorConverter]::ConvertFromString('#63dfcc'))
        BadgeBackground = [System.Windows.Media.Color]([System.Windows.Media.ColorConverter]::ConvertFromString('#26364d'))
        StatusOutline = [System.Windows.Media.Color]([System.Windows.Media.ColorConverter]::ConvertFromString('#26364d'))
    }
}

function New-Brush {
    param([Parameter(Mandatory)][System.Windows.Media.Color]$Color)
    $brush = [System.Windows.Media.SolidColorBrush]::new($Color)
    $brush.Freeze()
    return $brush
}

function Get-ShapeBrush {
    param(
        [Parameter(Mandatory)][string]$ColorText,
        [Parameter(Mandatory)][System.Windows.Media.Color]$Foreground,
        [Parameter(Mandatory)][System.Windows.Media.Color]$Accent
    )

    if ([string]::IsNullOrWhiteSpace($ColorText) -or $ColorText -eq 'none') { return $null }
    if ($ColorText -eq 'currentColor') { return New-Brush -Color $Foreground }
    if ($ColorText -eq '#07877f' -or $ColorText -eq '#63dfcc') { return New-Brush -Color $Accent }
    return New-Brush -Color ([System.Windows.Media.Color]([System.Windows.Media.ColorConverter]::ConvertFromString($ColorText)))
}

function Get-ShapeNumber {
    param([Parameter(Mandatory)][System.Xml.XmlElement]$Node, [Parameter(Mandatory)][string]$Attribute, [double]$Default = 0)
    $text = $Node.GetAttribute($Attribute)
    if ([string]::IsNullOrWhiteSpace($text)) { return $Default }
    return [double]::Parse($text, [Globalization.CultureInfo]::InvariantCulture)
}

function New-ShapePen {
    param([System.Xml.XmlElement]$Node, [System.Windows.Media.Color]$Foreground, [System.Windows.Media.Color]$Accent)

    $strokeText = $Node.GetAttribute('stroke')
    if ($Node.GetAttribute('class') -eq 'link') { $strokeText = '#07877f' }
    if ([string]::IsNullOrWhiteSpace($strokeText) -or $strokeText -eq 'none') { return $null }

    $pen = [System.Windows.Media.Pen]::new(
        (Get-ShapeBrush -ColorText $strokeText -Foreground $Foreground -Accent $Accent),
        (Get-ShapeNumber -Node $Node -Attribute 'stroke-width' -Default 1))
    if ($Node.GetAttribute('stroke-linecap') -eq 'round') {
        $pen.StartLineCap = [System.Windows.Media.PenLineCap]::Round
        $pen.EndLineCap = [System.Windows.Media.PenLineCap]::Round
        $pen.DashCap = [System.Windows.Media.PenLineCap]::Round
    }
    if ($Node.GetAttribute('stroke-linejoin') -eq 'round') {
        $pen.LineJoin = [System.Windows.Media.PenLineJoin]::Round
    }
    $pen.Freeze()
    return $pen
}

function Draw-SvgShape {
    param(
        [Parameter(Mandatory)][System.Windows.Media.DrawingContext]$Context,
        [Parameter(Mandatory)][System.Xml.XmlElement]$Node,
        [Parameter(Mandatory)][System.Windows.Media.Color]$Foreground,
        [Parameter(Mandatory)][System.Windows.Media.Color]$Accent
    )

    $pen = New-ShapePen -Node $Node -Foreground $Foreground -Accent $Accent
    $fillText = $Node.GetAttribute('fill')
    $fill = if ($fillText) { Get-ShapeBrush -ColorText $fillText -Foreground $Foreground -Accent $Accent } else { New-Brush -Color $Foreground }

    switch ($Node.LocalName) {
        'rect' {
            $rect = [System.Windows.Rect]::new(
                (Get-ShapeNumber -Node $Node -Attribute 'x'),
                (Get-ShapeNumber -Node $Node -Attribute 'y'),
                (Get-ShapeNumber -Node $Node -Attribute 'width'),
                (Get-ShapeNumber -Node $Node -Attribute 'height'))
            $radiusX = Get-ShapeNumber -Node $Node -Attribute 'rx'
            $radiusY = Get-ShapeNumber -Node $Node -Attribute 'ry' -Default $radiusX
            $Context.DrawRoundedRectangle($fill, $pen, $rect, $radiusX, $radiusY)
        }
        'circle' {
            $Context.DrawEllipse(
                $fill,
                $pen,
                [System.Windows.Point]::new(
                    (Get-ShapeNumber -Node $Node -Attribute 'cx'),
                    (Get-ShapeNumber -Node $Node -Attribute 'cy')),
                (Get-ShapeNumber -Node $Node -Attribute 'r'),
                (Get-ShapeNumber -Node $Node -Attribute 'r'))
        }
        'path' {
            $geometry = [System.Windows.Media.Geometry]::Parse($Node.GetAttribute('d'))
            $geometry.Freeze()
            $Context.DrawGeometry($fill, $pen, $geometry)
        }
        default { throw "Unsupported SVG shape: $($Node.LocalName)" }
    }
}

function New-PngFrame {
    param(
        [Parameter(Mandatory)][int]$Size,
        [Parameter(Mandatory)][ValidateSet('Light', 'Dark')][string]$Theme,
        [System.Windows.Media.Color]$StatusColor,
        [switch]$Badge
    )

    $palette = $palettes[$Theme]
    $visual = [System.Windows.Media.DrawingVisual]::new()
    $context = $visual.RenderOpen()
    if ($Badge) {
        $inset = $Size * 0.045
        $rect = [System.Windows.Rect]::new($inset, $inset, $Size - (2 * $inset), $Size - (2 * $inset))
        $radius = $Size * 0.22
        $context.DrawRoundedRectangle((New-Brush -Color $palette.BadgeBackground), $null, $rect, $radius, $radius)
    }

    $scale = [Math]::Min(($Size * 0.92) / $artBounds.Width, ($Size * 0.92) / $artBounds.Height)
    $offsetX = ($Size - ($artBounds.Width * $scale)) / 2 - ($artBounds.X * $scale)
    $offsetY = ($Size - ($artBounds.Height * $scale)) / 2 - ($artBounds.Y * $scale)
    $matrix = [System.Windows.Media.Matrix]::new($scale, 0, 0, $scale, $offsetX, $offsetY)
    $context.PushTransform([System.Windows.Media.MatrixTransform]::new($matrix))
    foreach ($shape in $shapes) {
        Draw-SvgShape -Context $context -Node $shape -Foreground $palette.Foreground -Accent $palette.Accent
    }
    $context.Pop()

    if ($PSBoundParameters.ContainsKey('StatusColor') -and $StatusColor.A -gt 0) {
        $statusBrush = New-Brush -Color $StatusColor
        $outline = [System.Windows.Media.Pen]::new((New-Brush -Color $palette.StatusOutline), [Math]::Max(0.75, $Size * 0.035))
        $outline.Freeze()
        $dotRadius = $Size * 0.09
        $dotCenter = [System.Windows.Point]::new($Size * 0.86, $Size * 0.86)
        $context.DrawEllipse($statusBrush, $outline, $dotCenter, $dotRadius, $dotRadius)
    }

    $context.Close()
    $bitmap = [System.Windows.Media.Imaging.RenderTargetBitmap]::new(
        $Size, $Size, 96, 96, [System.Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)
    $encoder = [System.Windows.Media.Imaging.PngBitmapEncoder]::new()
    $encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $stream = [System.IO.MemoryStream]::new()
    try {
        $encoder.Save($stream)
        return ,$stream.ToArray()
    }
    finally { $stream.Dispose() }
}

function Save-Ico {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][ValidateSet('Light', 'Dark')][string]$Theme,
        [System.Windows.Media.Color]$StatusColor,
        [switch]$Badge
    )

    $frames = foreach ($size in $iconSizes) {
        $png = if ($PSBoundParameters.ContainsKey('StatusColor') -and $StatusColor.A -gt 0) {
            New-PngFrame -Size $size -Theme $Theme -StatusColor $StatusColor -Badge:$Badge
        }
        else {
            New-PngFrame -Size $size -Theme $Theme -Badge:$Badge
        }
        [pscustomobject]@{ Size = $size; Png = $png }
    }

    $stream = [System.IO.MemoryStream]::new()
    $writer = [System.IO.BinaryWriter]::new($stream)
    try {
        $writer.Write([UInt16]0)
        $writer.Write([UInt16]1)
        $writer.Write([UInt16]$frames.Count)
        $offset = 6 + (16 * $frames.Count)
        foreach ($frame in $frames) {
            $dimension = if ($frame.Size -eq 256) { [byte]0 } else { [byte]$frame.Size }
            $writer.Write($dimension)
            $writer.Write($dimension)
            $writer.Write([byte]0)
            $writer.Write([byte]0)
            $writer.Write([UInt16]1)
            $writer.Write([UInt16]32)
            $writer.Write([UInt32]$frame.Png.Length)
            $writer.Write([UInt32]$offset)
            $offset += $frame.Png.Length
        }
        foreach ($frame in $frames) { $writer.Write([byte[]]$frame.Png) }
        $writer.Flush()
        [System.IO.File]::WriteAllBytes($Path, $stream.ToArray())
    }
    finally {
        $writer.Dispose()
        $stream.Dispose()
    }
}

$neutral = [System.Windows.Media.Color]::FromRgb(137, 148, 159)
$running = [System.Windows.Media.Color]::FromRgb(47, 145, 93)
$errorStatusColor = [System.Windows.Media.Color]::FromRgb(184, 91, 96)

Save-Ico -Path (Join-Path $assetsRoot 'PadP2P.ico') -Theme Light -Badge
foreach ($theme in @('Light', 'Dark')) {
    Save-Ico -Path (Join-Path $assetsRoot "PadP2P.$theme.ico") -Theme $theme -Badge
    Save-Ico -Path (Join-Path $assetsRoot "PadP2P.Stopped.$theme.ico") -Theme $theme -StatusColor $neutral -Badge
    Save-Ico -Path (Join-Path $assetsRoot "PadP2P.Running.$theme.ico") -Theme $theme -StatusColor $running -Badge
    Save-Ico -Path (Join-Path $assetsRoot "PadP2P.Error.$theme.ico") -Theme $theme -StatusColor $errorStatusColor -Badge
    [System.IO.File]::WriteAllBytes((Join-Path $assetsRoot "PadP2P.$theme.png"), (New-PngFrame -Size 256 -Theme $theme))
}
foreach ($state in @('Stopped', 'Running', 'Error')) {
    Copy-Item -LiteralPath (Join-Path $assetsRoot "PadP2P.$state.Light.ico") -Destination (Join-Path $assetsRoot "PadP2P.$state.ico") -Force
}
[System.IO.File]::WriteAllBytes((Join-Path $assetsRoot 'PadP2P.png'), (New-PngFrame -Size 256 -Theme Light))

foreach ($iconFile in Get-ChildItem -LiteralPath $assetsRoot -Filter 'PadP2P*.ico') {
    $decoder = [System.Windows.Media.Imaging.BitmapDecoder]::Create(
        $iconFile.FullName,
        [System.Windows.Media.Imaging.BitmapCreateOptions]::PreservePixelFormat,
        [System.Windows.Media.Imaging.BitmapCacheOption]::OnLoad)
    if ($decoder.Frames.Count -ne $iconSizes.Count) {
        throw "Expected $($iconSizes.Count) icon sizes in $($iconFile.Name), found $($decoder.Frames.Count)."
    }
}

Get-ChildItem -LiteralPath $assetsRoot -Filter 'PadP2P*.ico' |
    Select-Object Name, Length, LastWriteTime
