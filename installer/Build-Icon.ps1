# Run with Windows PowerShell -STA. Keep the executable icon aligned with the SVG asset.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore,WindowsBase
$assets = Join-Path (Split-Path $PSScriptRoot) 'PixelLyrics.Probe/Assets'
[xml]$svg = Get-Content (Join-Path $assets 'pixellyrics.svg') -Raw -Encoding UTF8
$frames = @()
foreach ($size in @(16,24,32,48,64,128,256)) {
    $visual = New-Object Windows.Media.DrawingVisual
    $context = $visual.RenderOpen()
    $context.PushTransform((New-Object Windows.Media.ScaleTransform ($size / 256.0),($size / 256.0)))
    foreach ($path in $svg.svg.path) {
        $brush = New-Object Windows.Media.SolidColorBrush ([Windows.Media.ColorConverter]::ConvertFromString($path.fill))
        $context.DrawGeometry($brush,$null,[Windows.Media.Geometry]::Parse($path.d))
    }
    $context.Pop()
    $context.Close()
    $bitmap = New-Object Windows.Media.Imaging.RenderTargetBitmap $size,$size,96,96,([Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)
    $encoder = New-Object Windows.Media.Imaging.PngBitmapEncoder
    $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $stream = New-Object IO.MemoryStream
    $encoder.Save($stream)
    $frames += @{ Size=$size; Bytes=$stream.ToArray() }
    $stream.Dispose()
}
$output = [IO.File]::Create((Join-Path $assets 'pixellyrics.ico'))
$writer = New-Object IO.BinaryWriter $output
try {
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$frames.Count)
    $offset = 6 + 16 * $frames.Count
    foreach ($frame in $frames) {
        $dimension = $frame.Size % 256
        $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
        $writer.Write([byte]0); $writer.Write([byte]0)
        $writer.Write([uint16]1); $writer.Write([uint16]32)
        $writer.Write([uint32]$frame.Bytes.Length); $writer.Write([uint32]$offset)
        $offset += $frame.Bytes.Length
    }
    foreach ($frame in $frames) { $writer.Write([byte[]]$frame.Bytes) }
} finally { $writer.Dispose(); $output.Dispose() }
