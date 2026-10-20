# Renders MakeItColor's icon from the vector logo in src\MakeItColor\Assets\Logo.xaml:
#   src\MakeItColor\Assets\MakeItColor.ico   app icon (16-256 px; the 16-24 px frames use the simplified mark)
#   docs\icon.png                             256 px preview
#   powershell -ExecutionPolicy Bypass -File tools\make-icon.ps1
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase, System.Drawing

$root = Split-Path -Parent $PSScriptRoot
$assets = Join-Path $root "src\MakeItColor\Assets"
$xaml = [System.IO.File]::ReadAllText((Join-Path $assets "Logo.xaml"))
$logo = [System.Windows.Markup.XamlReader]::Parse($xaml)

function Render-Png([System.Windows.Media.Drawing]$drawing, [int]$size) {
    $visual = New-Object System.Windows.Media.DrawingVisual
    $dc = $visual.RenderOpen()
    $scale = $size / 256.0
    $dc.PushTransform((New-Object System.Windows.Media.ScaleTransform $scale, $scale))
    $dc.DrawDrawing($drawing)
    $dc.Pop()
    $dc.Close()
    [System.Windows.Media.RenderOptions]::SetEdgeMode($visual, [System.Windows.Media.EdgeMode]::Unspecified)
    $bmp = New-Object System.Windows.Media.Imaging.RenderTargetBitmap $size, $size, 96, 96, ([System.Windows.Media.PixelFormats]::Pbgra32)
    $bmp.Render($visual)
    $encoder = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
    $encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bmp))
    $ms = New-Object System.IO.MemoryStream
    $encoder.Save($ms)
    return , $ms.ToArray()
}

# Sizes up to 48 px as classic 32-bit bitmaps (best compatibility), larger ones as PNG.
function Get-IcoFrame([byte[]]$png, [int]$size) {
    if ($size -gt 48) { return , $png }
    $bmp = New-Object System.Drawing.Bitmap (New-Object System.IO.MemoryStream (, $png))
    $ms = New-Object System.IO.MemoryStream
    $w = New-Object System.IO.BinaryWriter $ms
    $w.Write([UInt32]40); $w.Write([Int32]$size); $w.Write([Int32]($size * 2))
    $w.Write([UInt16]1); $w.Write([UInt16]32); $w.Write([UInt32]0)
    $w.Write([UInt32]0); $w.Write([Int32]0); $w.Write([Int32]0); $w.Write([UInt32]0); $w.Write([UInt32]0)
    for ($y = $size - 1; $y -ge 0; $y--) {
        for ($x = 0; $x -lt $size; $x++) {
            $c = $bmp.GetPixel($x, $y)
            $w.Write([byte]$c.B); $w.Write([byte]$c.G); $w.Write([byte]$c.R); $w.Write([byte]$c.A)
        }
    }
    $rowBytes = [int]([Math]::Ceiling($size / 32.0) * 4)
    $w.Write((New-Object byte[] ($rowBytes * $size)))
    $w.Flush()
    $bmp.Dispose()
    return , $ms.ToArray()
}

function Write-Ico([string]$path, [scriptblock]$drawingFor) {
    $sizes = 16, 20, 24, 32, 40, 48, 64, 96, 128, 256
    $frames = @()
    foreach ($s in $sizes) {
        $png = Render-Png (& $drawingFor $s) $s
        $frames += , @{ Size = $s; Bytes = (Get-IcoFrame $png $s) }
    }
    $out = New-Object System.IO.MemoryStream
    $w = New-Object System.IO.BinaryWriter $out
    $w.Write([UInt16]0); $w.Write([UInt16]1); $w.Write([UInt16]$frames.Count)
    $offset = 6 + 16 * $frames.Count
    foreach ($f in $frames) {
        $dim = if ($f.Size -ge 256) { 0 } else { $f.Size }
        $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
        $w.Write([UInt16]1); $w.Write([UInt16]32)
        $w.Write([UInt32]$f.Bytes.Length); $w.Write([UInt32]$offset)
        $offset += $f.Bytes.Length
    }
    foreach ($f in $frames) { $w.Write([byte[]]$f.Bytes) }
    $w.Flush()
    [System.IO.File]::WriteAllBytes($path, $out.ToArray())
    Write-Host "Icon written: $path ($($out.Length) bytes)"
}

$mark = $logo["Logo.Mark"].Drawing
$markSmall = $logo["Logo.MarkSmall"].Drawing
Write-Ico (Join-Path $assets "MakeItColor.ico") { param($s) if ($s -le 24) { $markSmall } else { $mark } }
New-Item -ItemType Directory -Force (Join-Path $root "docs") | Out-Null
[System.IO.File]::WriteAllBytes((Join-Path $root "docs\icon.png"), (Render-Png $mark 256))
Write-Host "Preview written: docs\icon.png"
