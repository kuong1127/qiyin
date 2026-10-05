param([Parameter(Mandatory)][string]$PngPath, [Parameter(Mandatory)][string]$IcoPath)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$original = [Drawing.Bitmap]::FromFile($PngPath)
$frames = @()
try {
    foreach ($size in @(16,24,32,48,64,128,256)) {
        $bitmap = [Drawing.Bitmap]::new($size,$size)
        $graphics = [Drawing.Graphics]::FromImage($bitmap)
        $graphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $graphics.DrawImage($original,0,0,$size,$size)
        $graphics.Dispose()
        $memory = [IO.MemoryStream]::new()
        $bitmap.Save($memory,[Drawing.Imaging.ImageFormat]::Png)
        $frames += ,[pscustomobject]@{Size=$size;Bytes=$memory.ToArray()}
        $memory.Dispose(); $bitmap.Dispose()
    }
} finally { $original.Dispose() }
$file = [IO.File]::Create($IcoPath)
$writer = [IO.BinaryWriter]::new($file)
try {
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$frames.Count)
    $offset = 6 + 16 * $frames.Count
    foreach ($frame in $frames) {
        $side = if ($frame.Size -eq 256) { 0 } else { $frame.Size }
        $writer.Write([byte]$side); $writer.Write([byte]$side)
        $writer.Write([byte]0); $writer.Write([byte]0)
        $writer.Write([uint16]1); $writer.Write([uint16]32)
        $writer.Write([uint32]$frame.Bytes.Length); $writer.Write([uint32]$offset)
        $offset += $frame.Bytes.Length
    }
    foreach ($frame in $frames) { $writer.Write([byte[]]$frame.Bytes) }
} finally { $writer.Dispose(); $file.Dispose() }
