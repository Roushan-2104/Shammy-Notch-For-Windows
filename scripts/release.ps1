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
Write-Host "Released v$version. Installed copies will update within a few hours." -ForegroundColor Green
