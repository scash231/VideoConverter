<#
.SYNOPSIS
  Builds a VideoConverter release for the web installer and optionally uploads it to scash.uk.

.DESCRIPTION
  Output in dist\vc (the same layout as on the website, https://scash.uk/vc/...):
    manifest.json                     what VideoConverterSetup.exe reads (version, files, sizes, SHA-256)
    <version>\VideoConverterUI.exe    the app (framework-dependent single file)
    <version>\ferrite.exe             the Ferrite engine
    VideoConverterSetup.exe           the installer you give to people
    VideoConverter-Legacy-Python.zip  the unsupported Python engine, for manual download only

  -Upload puts everything into the R2 bucket of the website (prefix vc/) with wrangler, manifest.json last,
  so a client never sees a manifest that points at files that are not there yet. Run `npx wrangler login` once first.

.EXAMPLE
  .\installer\build_release.ps1
  .\installer\build_release.ps1 -Upload
  .\installer\build_release.ps1 -SkipBuild -Upload
#>
param(
    [switch]$Upload,
    [switch]$SkipBuild,
    [string]$Bucket = "scash-files",
    [string]$Prefix = "vc",
    [string]$WebsiteDir = (Join-Path $PSScriptRoot "..\..\scash.uk")
)

$ErrorActionPreference = "Stop"
$root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path

# -- Version (BuildInfoService.BuildVersion) and runtime (TargetFramework) --
$buildInfo = Get-Content (Join-Path $root "VideoConverterUI\Services\BuildInfoService.cs") -Raw
if ($buildInfo -notmatch 'BuildVersion\s*=>\s*"([^"]+)"') { throw "BuildVersion not found in BuildInfoService.cs" }
$version = $Matches[1]
if ($version -notmatch '^[0-9A-Za-z][0-9A-Za-z._+-]*$') { throw "Version '$version' can't be used as a folder name in the download address" }

$csproj = Get-Content (Join-Path $root "VideoConverterUI\VideoConverterUI.csproj") -Raw
if ($csproj -notmatch '<TargetFramework>net(\d+)\.') { throw "TargetFramework not found in VideoConverterUI.csproj" }
$runtimeMajor = [int]$Matches[1]

Write-Host "VideoConverter $version (.NET $runtimeMajor)" -ForegroundColor Cyan

$publish = Join-Path $root "publish"
$setupExe = Join-Path $root "VideoConverterSetup\bin\Release\net48\VideoConverterSetup.exe"

# -- Build --
if (-not $SkipBuild) {
    Write-Host "Publishing the app (also builds ferrite.exe with cargo)..." -ForegroundColor Cyan
    dotnet publish (Join-Path $root "VideoConverterUI") -c Release -o $publish
    if ($LASTEXITCODE) { throw "dotnet publish failed" }

    Write-Host "Building the installer..." -ForegroundColor Cyan
    dotnet build (Join-Path $root "VideoConverterSetup") -c Release
    if ($LASTEXITCODE) { throw "building VideoConverterSetup failed" }
}

# -- dist\vc --
$dist = Join-Path $root "dist\vc"
if (Test-Path $dist) { Get-ChildItem $dist | Remove-Item -Recurse -Force }
$versionDir = Join-Path $dist $version
New-Item -ItemType Directory -Force $versionDir | Out-Null

$files = @()
foreach ($name in @("VideoConverterUI.exe", "ferrite.exe")) {
    $src = Join-Path $publish $name
    if (-not (Test-Path $src)) { throw "$src is missing. Is the Rust toolchain installed (ferrite.exe)?" }
    Copy-Item $src (Join-Path $versionDir $name)
    $files += [ordered]@{
        name   = $name
        path   = "$version/$name"
        size   = (Get-Item $src).Length
        sha256 = (Get-FileHash $src -Algorithm SHA256).Hash.ToLowerInvariant()
    }
}

if (-not (Test-Path $setupExe)) { throw "$setupExe is missing" }
Copy-Item $setupExe (Join-Path $dist "VideoConverterSetup.exe")

# -- Legacy (Python) engine: no longer supported, only as a manual download --
$stage = Join-Path ([IO.Path]::GetTempPath()) ("vc-legacy-" + [guid]::NewGuid().ToString("N"))
$stageBackend = Join-Path $stage "backend"
New-Item -ItemType Directory -Force $stageBackend | Out-Null
# only source files: never client_secret.json / token.json (OAuth secrets), venv, caches or ffmpeg
Get-ChildItem (Join-Path $root "backend") -File | Where-Object { $_.Extension -eq ".py" -or $_.Name -eq "requirements.txt" } |
    ForEach-Object { Copy-Item $_.FullName $stageBackend }
@"
VideoConverter Legacy engine (Python) - NOT SUPPORTED
=====================================================

The Legacy engine is no longer maintained. Native and Ferrite (built into
VideoConverter) do everything it did. Use this only if you really need it.

1. Install Python 3.10 or newer from https://www.python.org (tick "Add to PATH").
2. Copy the "backend" folder from this zip into the VideoConverter install
   folder, next to VideoConverterUI.exe
   (default: %LOCALAPPDATA%\Programs\VideoConverter\backend).
3. Open a terminal in that backend folder and run:
       python -m pip install -r requirements.txt
4. In VideoConverter: Settings > Advanced > Engine > Legacy.

Subtitles also use backend\subtitler.py and need step 1 to 3.
YouTube upload needs your own Google OAuth client_secret.json in the backend folder.
"@ | Set-Content (Join-Path $stage "README.txt") -Encoding UTF8
$legacyZip = Join-Path $dist "VideoConverter-Legacy-Python.zip"
# entries added by hand: Compress-Archive and ZipFile.CreateFromDirectory on PowerShell 5.1 write backslashes into the names
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$zip = [IO.Compression.ZipFile]::Open($legacyZip, [IO.Compression.ZipArchiveMode]::Create)
try {
    Get-ChildItem $stage -Recurse -File | ForEach-Object {
        $entry = $_.FullName.Substring($stage.Length + 1).Replace([char]92, [char]47)
        [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $_.FullName, $entry)
    }
}
finally {
    $zip.Dispose()
}
Remove-Item $stage -Recurse -Force

# -- manifest.json --
$manifest = [ordered]@{
    name      = "VideoConverter"
    version   = $version
    published = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
    minSetup  = 1
    files     = $files
    runtime   = [ordered]@{
        framework = "Microsoft.WindowsDesktop.App"
        major     = $runtimeMajor
        url       = "https://aka.ms/dotnet/$runtimeMajor.0/windowsdesktop-runtime-win-x64.exe"
        size      = 62914560
    }
}
$json = $manifest | ConvertTo-Json -Depth 6
[IO.File]::WriteAllText((Join-Path $dist "manifest.json"), $json, (New-Object Text.UTF8Encoding $false))

Write-Host ""
Write-Host "Release in $dist" -ForegroundColor Green
Get-ChildItem $dist -Recurse -File | ForEach-Object {
    "  {0,-45} {1,10:N0} bytes" -f $_.FullName.Substring($dist.Length + 1), $_.Length
}

# -- Upload --
if ($Upload) {
    $wranglerDir = if (Test-Path $WebsiteDir) { (Resolve-Path $WebsiteDir).Path } else { $root }
    function Put([string]$key, [string]$file, [string]$type) {
        Write-Host "  -> $Bucket/$Prefix/$key" -ForegroundColor Cyan
        & npx wrangler r2 object put "$Bucket/$Prefix/$key" --file "$file" --content-type $type --remote
        if ($LASTEXITCODE) { throw "Upload of $key failed" }
    }
    Write-Host ""
    Write-Host "Uploading to R2..." -ForegroundColor Cyan
    Push-Location $wranglerDir
    try {
        foreach ($f in $files) { Put $f.path (Join-Path $dist $f.path) "application/vnd.microsoft.portable-executable" }
        Put "VideoConverterSetup.exe" (Join-Path $dist "VideoConverterSetup.exe") "application/vnd.microsoft.portable-executable"
        Put "VideoConverter-Legacy-Python.zip" $legacyZip "application/zip"
        Put "manifest.json" (Join-Path $dist "manifest.json") "application/json"
    }
    finally {
        Pop-Location
    }
    Write-Host ""
    Write-Host "Done. Installer link: https://scash.uk/vc/VideoConverterSetup.exe" -ForegroundColor Green
}
