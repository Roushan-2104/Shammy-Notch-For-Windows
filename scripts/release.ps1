<#
.SYNOPSIS
  Builds Shammy and publishes a release that installed copies pick up automatically.

.DESCRIPTION
  1. Bump <Version> in Directory.Build.props.
  2. Commit and push.
  3. Run:  .\scripts\release.ps1 -Publish

  Without -Publish it only builds the packages into artifacts\releases, so you can try Setup first.
  The GitHub upload uses your `gh` login.

  Installed copies of Shammy check GitHub every few hours, download the update in the background
  (only the changed parts, when the previous release is on GitHub) and offer a restart.
#>
param(
    [switch]$Publish,
    [string]$NotesFile,
    [string]$OutputDir = "artifacts\releases"
)
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root

$repo = "https://github.com/Roushan-2104/Shammy-Notch-For-Windows"
$version = ([xml](Get-Content Directory.Build.props)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { throw "No <Version> in Directory.Build.props" }
if (-not (Select-String -Path src\Shammy.Core\Updates\WhatsNew.cs -SimpleMatch "[`"$version`"]" -Quiet)) { throw "Add what's-new lines for $version in src\Shammy.Core\Updates\WhatsNew.cs" }
$publishDir = "artifacts\publish"
Write-Host "Shammy $version" -ForegroundColor Cyan

if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
dotnet publish src/Shammy.App -c Release -r win-x64 --self-contained true -p:PublishReadyToRun=true -o $publishDir
if ($LASTEXITCODE) { throw "dotnet publish failed" }

dotnet tool restore | Out-Null
$token = gh auth token
if ($LASTEXITCODE -or -not $token) { throw "Sign in with 'gh auth login' first" }

# The previous release lets vpk build a delta package, so updates download only what changed.
dotnet vpk download github --repoUrl $repo --token $token -o $OutputDir
if ($LASTEXITCODE) { Write-Warning "No previous Velopack release found; this one ships as a full package only." }

$pack = @("vpk", "pack", "-u", "HaloNotch", "-v", $version, "-p", $publishDir, "-e", "Shammy.exe", "-o", $OutputDir,
    "--packTitle", "Shammy", "--packAuthors", "Roushan", "--icon", "assets\shammy.ico")
if ($NotesFile) { $pack += @("--releaseNotes", $NotesFile) }
dotnet @pack
if ($LASTEXITCODE) { throw "vpk pack failed" }

if (-not $Publish) {
    Write-Host "Built into $OutputDir. Re-run with -Publish to release it." -ForegroundColor Yellow
    return
}
dotnet vpk upload github --repoUrl $repo --token $token -o $OutputDir --publish --releaseName "Shammy v$version" --tag "v$version" --targetCommitish main
if ($LASTEXITCODE) { throw "Upload failed" }

# The update ID stays "HaloNotch" (the app's old name) so existing installs keep updating; give people
# friendly file names to download as well.
$friendly = Join-Path $OutputDir "friendly"
New-Item -ItemType Directory -Force $friendly | Out-Null
Copy-Item (Join-Path $OutputDir "HaloNotch-win-Setup.exe") (Join-Path $friendly "Shammy-Setup.exe") -Force
Copy-Item (Join-Path $OutputDir "HaloNotch-win-Portable.zip") (Join-Path $friendly "Shammy-Portable.zip") -Force
gh release upload "v$version" (Join-Path $friendly "Shammy-Setup.exe") (Join-Path $friendly "Shammy-Portable.zip") --clobber --repo "Roushan-2104/Shammy-Notch-For-Windows"
if ($LASTEXITCODE) { Write-Warning "Uploading the Shammy-named copies failed; the HaloNotch-named files are there." }
else {
    # The same two downloads under the old name only confuse people on the release page, so they go once the
    # Shammy-named copies are up. The update packages (.nupkg) keep the old name: installed copies ask for it.
    foreach ($old in "HaloNotch-win-Setup.exe", "HaloNotch-win-Portable.zip") {
        gh release delete-asset "v$version" $old --yes --repo "Roushan-2104/Shammy-Notch-For-Windows"
        if ($LASTEXITCODE) { Write-Warning "Couldn't remove $old from the release page; it is only a duplicate." }
    }
}
# Shammy Island for Android rides along, so the phone card's "Get it from GitHub" QR (releases/latest/download/...) works.
# Only if it is signed by Shammy Island's own key (a debug-signed or swapped file never goes out).
$apk = "src\Shammy.App\Assets\Phone\ShammyIsland.apk"
$pinned = "5a9e0f78dd51bf7aaac581c49571b54cd2a8a6f568e20223070582c55ed337fe"
$apksigner = Get-ChildItem "$env:LOCALAPPDATA\Android\Sdk\build-tools\*\apksigner.bat" -ErrorAction SilentlyContinue | Sort-Object FullName | Select-Object -Last 1
$certs = if ($apksigner) { & $apksigner.FullName verify --print-certs $apk 2>&1 | Out-String } else { "" }
if ($certs -notmatch "certificate SHA-256 digest: $pinned") {
    Write-Warning "ShammyIsland.apk is not signed by Shammy Island's key (or apksigner wasn't found): not uploaded."
} else {
    gh release upload "v$version" $apk --clobber --repo "Roushan-2104/Shammy-Notch-For-Windows"
    if ($LASTEXITCODE) { Write-Warning "Uploading ShammyIsland.apk failed; the phone card's GitHub QR won't work until it's there." }
    else { Write-Host "ShammyIsland.apk SHA-256: $((Get-FileHash $apk -Algorithm SHA256).Hash.ToLowerInvariant())" }
}
Write-Host "Released v$version. Installed copies will update within a few hours." -ForegroundColor Green
