# Regenerate the Windows icon from the repository-owned vector geometry.
# Windows / WPF only. No device access or installation.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore, WindowsBase
$assets = Join-Path $PSScriptRoot 'assets'
[xml]$svg = Get-Content (Join-Path $assets 'FanOrder.svg') -Raw
function Brush($hex) { [Windows.Media.BrushConverter]::new().ConvertFromString($hex) }
function Gradient($node) {
    $b = [Windows.Media.LinearGradientBrush]::new()
    $b.MappingMode = [Windows.Media.BrushMappingMode]::Absolute
    $b.StartPoint = [Windows.Point]::new([double]$node.x1,[double]$node.y1)
    $b.EndPoint = [Windows.Point]::new([double]$node.x2,[double]$node.y2)
    foreach($stop in $node.stop) {
        $b.GradientStops.Add([Windows.Media.GradientStop]::new(
            [Windows.Media.ColorConverter]::ConvertFromString($stop.'stop-color'),[double]$stop.offset))
    }
    return $b
}
$surface = Gradient $svg.svg.defs.linearGradient[0]
$blade = Gradient $svg.svg.defs.linearGradient[1]
$frames = @()
$sizes = @(16,20,24,32,40,48,64,96,128,256)
foreach($size in $sizes) {
    $v = [Windows.Media.DrawingVisual]::new()
    $dc = $v.RenderOpen()
    $dc.PushTransform([Windows.Media.ScaleTransform]::new($size/256.0,$size/256.0))
    $r = $svg.svg.rect
    $dc.DrawRoundedRectangle($surface,$null,[Windows.Rect]::new([double]$r.x,[double]$r.y,[double]$r.width,[double]$r.height),[double]$r.rx,[double]$r.rx)
    $ring = $svg.svg.circle[0]
    $fill = Brush $ring.fill
    $fill.Opacity = [double]$ring.'fill-opacity'
    $stroke = Brush $ring.stroke
    $stroke.Opacity = [double]$ring.'stroke-opacity'
    $dc.DrawEllipse($fill,[Windows.Media.Pen]::new($stroke,[double]$ring.'stroke-width'),[Windows.Point]::new(128,128),[double]$ring.r,[double]$ring.r)
    foreach($p in $svg.svg.g.path) { $dc.DrawGeometry($blade,$null,[Windows.Media.Geometry]::Parse($p.d)) }
    foreach($c in @($svg.svg.circle)[1..2]) {
        $dc.DrawEllipse((Brush $c.fill),$null,[Windows.Point]::new([double]$c.cx,[double]$c.cy),[double]$c.r,[double]$c.r)
    }
    $dc.Pop()
    $dc.Close()
    $bitmap = [Windows.Media.Imaging.RenderTargetBitmap]::new($size,$size,96,96,[Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($v)
    $encoder = [Windows.Media.Imaging.PngBitmapEncoder]::new()
    $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $stream = [IO.MemoryStream]::new()
    $encoder.Save($stream)
    $frames += ,$stream.ToArray()
    $stream.Dispose()
}
# PNG payloads retain RGBA transparency in every ICO frame.
$stream = [IO.MemoryStream]::new()
$writer = [IO.BinaryWriter]::new($stream)
$writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for($i=0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i] % 256
    $writer.Write([byte]$s); $writer.Write([byte]$s)
    $writer.Write([byte]0); $writer.Write([byte]0)
    $writer.Write([uint16]1); $writer.Write([uint16]32)
    $writer.Write([uint32]$frames[$i].Length); $writer.Write([uint32]$offset)
    $offset += $frames[$i].Length
}
foreach($frame in $frames) { $writer.Write([byte[]]$frame) }
$writer.Flush()
[IO.File]::WriteAllBytes((Join-Path $assets 'FanOrder.ico'),$stream.ToArray())
[IO.File]::WriteAllBytes((Join-Path $assets 'FanOrder.png'),$frames[-1])
$writer.Dispose(); $stream.Dispose()
Write-Output "Generated transparent ICO frames: $($sizes -join ', ')"
