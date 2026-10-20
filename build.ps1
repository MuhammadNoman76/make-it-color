# Builds MakeItColor: the x64 and ARM64 apps and their installers.
#   powershell -ExecutionPolicy Bypass -File build.ps1                    # both installers -> dist\
#   powershell -ExecutionPolicy Bypass -File build.ps1 -Editions x64      # just one
#   powershell -ExecutionPolicy Bypass -File build.ps1 -SkipInstaller     # apps only -> publish\<edition>
# Output: dist\MakeItColor-Setup-<version>-<edition>.exe and dist\SHA256SUMS.txt. They are published as assets of the
# GitHub release v<version> (assets may be up to 2 GB; the x64 installer is over the 100 MB limit for files in the repo).
# The colour model isn't in the installer: the app downloads it on first use (SHA-256 checked).
# FFmpeg comes from vendor\ffmpeg\<cpu> (tools\get-ffmpeg.ps1 fetches and checks it).
param(
    [string]$Version = "1.0.0",
    [ValidateSet("x64", "arm64")]
    [string[]]$Editions = @("x64", "arm64"),
    [switch]$SkipInstaller
)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$project = Join-Path $root "src\MakeItColor\MakeItColor.csproj"
$publishRoot = Join-Path $root "publish"
$dist = Join-Path $root "dist"
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

function Find-Tool([string[]]$candidates) {
    foreach ($c in $candidates) { if ($c -and (Test-Path $c)) { return $c } }
    return $null
}

$dotnetCmd = Get-Command dotnet -ErrorAction SilentlyContinue
$dotnet = Find-Tool @("$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe", $(if ($dotnetCmd) { $dotnetCmd.Source }))
if (-not $dotnet) { throw ".NET SDK not found. Install the .NET 10 SDK from https://dot.net" }
$iscc = Find-Tool @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe")
if (-not $iscc -and -not $SkipInstaller) { throw "Inno Setup 6 not found. Install it from https://jrsoftware.org/isdl.php" }

$running = Get-Process MakeItColor -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.StartsWith($publishRoot, [StringComparison]::OrdinalIgnoreCase) }
if ($running) { throw "MakeItColor is running from $publishRoot - close it and run the build again." }

foreach ($edition in $Editions) {
    if (-not (Test-Path (Join-Path $root "vendor\ffmpeg\$edition\ffmpeg.exe"))) {
        & powershell -ExecutionPolicy Bypass -File (Join-Path $root "tools\get-ffmpeg.ps1")
        if ($LASTEXITCODE -ne 0) { throw "Could not get FFmpeg" }
    }
    $out = Join-Path $publishRoot $edition
    Write-Host "==> Publishing MakeItColor $Version ($edition)" -ForegroundColor Cyan
    if (Test-Path $out) { Remove-Item $out -Recurse -Force }
    & $dotnet publish $project -c Release -r "win-$edition" --self-contained true -o $out -p:DebugType=none -p:Version=$Version -p:ColorEdition=$edition
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $edition" }
    if (-not (Test-Path (Join-Path $out "ffmpeg\ffmpeg.exe"))) { throw "FFmpeg is missing from the $edition build" }
}

if ($SkipInstaller) {
    Write-Host "Done: $publishRoot" -ForegroundColor Green
    return
}

New-Item -ItemType Directory -Force $dist | Out-Null
foreach ($edition in $Editions) {
    Write-Host "==> Compiling installer ($edition)" -ForegroundColor Cyan
    & $iscc /Q "/DAppVersion=$Version" "/DEdition=$edition" (Join-Path $root "installer\MakeItColor.iss")
    if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed for $edition" }
}

$setups = Get-ChildItem $dist -Filter "MakeItColor-Setup-$Version-*.exe" | Sort-Object Name
$sums = $setups | ForEach-Object { "{0}  {1}" -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $_.Name }
Set-Content -Path (Join-Path $dist "SHA256SUMS.txt") -Value $sums -Encoding ASCII
foreach ($s in $setups) {
    Write-Host ("    {0}  {1:N0} bytes ({2:0.0} MB)" -f $s.Name, $s.Length, ($s.Length / 1MB)) -ForegroundColor Green
}
Write-Host "Done: $dist" -ForegroundColor Green
