# Fetches the FFmpeg the app uses for videos into vendor\ffmpeg\x64 and vendor\ffmpeg\arm64.
#   powershell -ExecutionPolicy Bypass -File tools\get-ffmpeg.ps1
# The unmodified LGPL shared builds of FFmpeg 9.0 by BtbN (https://github.com/BtbN/FFmpeg-Builds), copied to this
# repository's "ffmpeg-9.0" release so the exact files stay available; each zip is checked against its SHA-256.
# Only bin\ (without ffplay.exe) and LICENSE.txt are kept.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$release = "https://github.com/MuhammadNoman76/make-it-color/releases/download/ffmpeg-9.0"
$builds = @(
    @{ Cpu = "x64";   File = "ffmpeg-n9.0-latest-win64-lgpl-shared-9.0.zip";    Sha256 = "c1d4b6294001661303a82c36c9d4fcba39c25f3e228408e0e3f7959770c8bd83" },
    @{ Cpu = "arm64"; File = "ffmpeg-n9.0-latest-winarm64-lgpl-shared-9.0.zip"; Sha256 = "457c840e1d03aea0c4c004b45a6fe78aaa294c1fc5875072a7f63caafd89d52b" }
)
Add-Type -AssemblyName System.IO.Compression.FileSystem
$temp = Join-Path ([System.IO.Path]::GetTempPath()) "makeitcolor-ffmpeg"
New-Item -ItemType Directory -Force $temp | Out-Null

foreach ($b in $builds) {
    $zip = Join-Path $temp $b.File
    if (-not (Test-Path $zip) -or (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant() -ne $b.Sha256) {
        Write-Host "Downloading $($b.File)…"
        Invoke-WebRequest -Uri "$release/$($b.File)" -OutFile $zip -UseBasicParsing
    }
    $hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($hash -ne $b.Sha256) { throw "$($b.File): SHA-256 $hash doesn't match $($b.Sha256)" }

    $target = Join-Path $root "vendor\ffmpeg\$($b.Cpu)"
    if (Test-Path $target) { Remove-Item $target -Recurse -Force }
    New-Item -ItemType Directory -Force $target | Out-Null
    $archive = [System.IO.Compression.ZipFile]::OpenRead($zip)
    try {
        foreach ($entry in $archive.Entries) {
            $parts = $entry.FullName.Split('/')
            $keep = ($parts.Length -eq 3 -and $parts[1] -eq 'bin' -and $entry.Name -and $entry.Name -ne 'ffplay.exe') -or
                    ($parts.Length -eq 2 -and $entry.Name -eq 'LICENSE.txt')
            if ($keep) { [System.IO.Compression.ZipFileExtensions]::ExtractToFile($entry, (Join-Path $target $entry.Name), $true) }
        }
    }
    finally {
        $archive.Dispose()
    }
    Write-Host "FFmpeg $($b.Cpu): $((Get-ChildItem $target).Count) files in $target" -ForegroundColor Green
}
