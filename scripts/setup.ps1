<#
.SYNOPSIS
    Downloads everything CompanionOS needs to think, hear and search.

.DESCRIPTION
    Reads scripts/models.manifest.json and fetches each artifact into her data folder.
    No path, model name or version is hard-coded in the application: change the manifest
    and the setup follows.

    Artifacts can be:
      huggingface    a repo and a file pattern; the exact file is discovered through the
                     Hugging Face API, so a repo renaming its quantisation does not break us
      url            a direct download
      github-release  the newest release asset matching a pattern; releases without
                      binaries (brand tags) are skipped automatically

.PARAMETER Component
    all, brain, runtime, ears or tools. Defaults to all.

.PARAMETER Force
    Re-download even when the file already exists.

.PARAMETER VerifyOnly
    Only report what is present; download nothing.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts/setup.ps1
#>
[CmdletBinding()]
param(
    [ValidateSet('all', 'brain', 'runtime', 'ears')]
    [string]$Component = 'all',
    [switch]$Force,
    [switch]$VerifyOnly
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

function Get-DataRoot {
    if ($env:COMPANIONOS_HOME) { return $env:COMPANIONOS_HOME }
    return Join-Path $env:LOCALAPPDATA 'CompanionOS'
}

$dataRoot   = Get-DataRoot
$modelsDir  = Join-Path $dataRoot 'models'
$runtimeDir = Join-Path $dataRoot 'runtimes'
$manifestPath = Join-Path $PSScriptRoot 'models.manifest.json'

New-Item -ItemType Directory -Force -Path $modelsDir, $runtimeDir | Out-Null

Write-Host ''
Write-Host '  CompanionOS setup' -ForegroundColor Magenta
Write-Host "  data folder: $dataRoot"
Write-Host ''

if (-not (Test-Path $manifestPath)) {
    throw "Manifest not found: $manifestPath"
}

$manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json

# Resolves the real file name inside a Hugging Face repository for a wildcard pattern.
function Resolve-HuggingFaceFile {
    param([string]$Repo, [string]$Pattern, [string]$ExcludePattern)

    $apiUrl = "https://huggingface.co/api/models/$Repo"
    Write-Host "  querying $Repo" -ForegroundColor DarkGray
    $model = Invoke-RestMethod -Uri $apiUrl -UseBasicParsing

    $candidates = $model.siblings | ForEach-Object { $_.rfilename } |
        Where-Object { $_ -like $Pattern } |
        Where-Object { [string]::IsNullOrWhiteSpace($ExcludePattern) -or $_ -notlike $ExcludePattern } |
        Sort-Object

    if (-not $candidates) {
        throw "No file matching '$Pattern' in $Repo. Open https://huggingface.co/$Repo and adjust scripts/models.manifest.json."
    }

    return $candidates | Select-Object -First 1
}

function Save-Download {
    param([string]$Url, [string]$Destination, [string]$Label)

    Write-Host "  downloading $Label" -ForegroundColor Cyan
    $temporaryPath = "$Destination.part"
    if (Test-Path $temporaryPath) { Remove-Item $temporaryPath -Force }

    Invoke-WebRequest -Uri $Url -OutFile $temporaryPath -UseBasicParsing
    Move-Item -Path $temporaryPath -Destination $Destination -Force

    $sizeMb = [math]::Round((Get-Item $Destination).Length / 1MB, 1)
    Write-Host "  done: $Label ($sizeMb MB)" -ForegroundColor Green
}

function Get-ReleaseAssetUrl {
    param([string]$Repo, [string]$Pattern, [string]$FallbackPattern)

    # Some releases carry no binaries at all (brand tags such as v0.7.0); the actual
    # builds land on the newest per-build tags. Walk the newest releases and use the
    # first one that actually contains what the manifest asks for.
    $releases = Invoke-RestMethod -Uri "https://api.github.com/repos/$Repo/releases?per_page=15" -UseBasicParsing
    foreach ($release in $releases) {
        $asset = $release.assets | Where-Object { $_.name -like $Pattern } | Select-Object -First 1

        if (-not $asset -and $FallbackPattern) {
            $asset = $release.assets | Where-Object { $_.name -like $FallbackPattern } | Select-Object -First 1
        }

        if ($asset) {
            return @{ Url = $asset.browser_download_url; Name = $asset.name }
        }
    }

    throw "No asset matching '$Pattern' in the 15 newest $Repo releases. Adjust scripts/models.manifest.json."
}

$results = @()

foreach ($artifact in $manifest.artifacts) {

    # Component filter: each component owns a short list of artifact ids.
    $componentArtifacts = @{
        brain   = @('brain', 'brain-vision')
        ears    = @('ears')
        runtime = @('runtime', 'runtime-cuda')
    }

    if ($Component -ne 'all' -and $componentArtifacts[$Component] -notcontains $artifact.id) {
        continue
    }

    # Runtimes and tools live together in runtimes\, model weights in models\.
    # An artifact may override its home with targetDirectory.
    $isRuntime = $artifact.kind -eq 'github-release'
    $targetDirectory = if ($artifact.targetDirectory) { Join-Path $dataRoot $artifact.targetDirectory }
                       elseif ($isRuntime) { $runtimeDir }
                       else { $modelsDir }
    $targetPath = Join-Path $targetDirectory $artifact.targetName

    # Present-means-useful: an extracted runtime counts as present only when its
    # sentinel file is actually inside the artifact folder, so a half-finished unpack
    # or a skipped overlay (the CUDA DLL pack) reports as missing instead of fooling us.
    $isPresent =
        if ($artifact.sentinelFile) {
            $artifactFolder = Join-Path $targetDirectory $artifact.targetName
            Get-ChildItem -Path $artifactFolder -Filter $artifact.sentinelFile -ErrorAction SilentlyContinue |
                Select-Object -First 1 -ErrorAction SilentlyContinue
        } else {
            Test-Path $targetPath
        }

    if ($isPresent -and -not $Force) {
        Write-Host "  present: $($artifact.targetName)" -ForegroundColor DarkGray
        $results += [pscustomobject]@{ Artifact = $artifact.id; Status = 'present' }
        continue
    }

    if ($VerifyOnly) {
        $results += [pscustomobject]@{ Artifact = $artifact.id; Status = 'missing' }
        continue
    }

    try {
        switch ($artifact.kind) {
            'huggingface' {
                $fileName = Resolve-HuggingFaceFile -Repo $artifact.repo -Pattern $artifact.filePattern `
                    -ExcludePattern $artifact.excludePattern
                $url = "https://huggingface.co/$($artifact.repo)/resolve/main/$fileName`?download=true"
                Save-Download -Url $url -Destination $targetPath -Label $artifact.targetName
            }

            'url' {
                Save-Download -Url $artifact.url -Destination $targetPath -Label $artifact.targetName
            }

            'github-release' {
                $releaseAsset = Get-ReleaseAssetUrl -Repo $artifact.repo -Pattern $artifact.assetPattern `
                    -FallbackPattern $artifact.fallbackAssetPattern
                $zipPath = Join-Path $env:TEMP $releaseAsset.Name
                Save-Download -Url $releaseAsset.Url -Destination $zipPath -Label $releaseAsset.Name

                $extractPath = Join-Path $runtimeDir $artifact.targetName
                if (-not $artifact.overlay) {
                    # The primary runtime is unpacked clean each time. Overlay artifacts
                    # (the CUDA DLL pack) must merge into it, never wipe it.
                    if (Test-Path $extractPath) { Remove-Item $extractPath -Recurse -Force }
                }
                New-Item -ItemType Directory -Force -Path $extractPath | Out-Null
                Expand-Archive -Path $zipPath -DestinationPath $extractPath -Force
                Remove-Item $zipPath -Force -ErrorAction SilentlyContinue

                Write-Host "  unpacked to $extractPath" -ForegroundColor Green
            }
        }

        $results += [pscustomobject]@{ Artifact = $artifact.id; Status = 'downloaded' }
    }
    catch {
        Write-Host "  failed: $($artifact.id) — $($_.Exception.Message)" -ForegroundColor Red
        $results += [pscustomobject]@{ Artifact = $artifact.id; Status = 'failed' }
    }
}

Write-Host ''
Write-Host '  Summary' -ForegroundColor Magenta
$results | Format-Table -AutoSize | Out-String | Write-Host

$missingRequired = $results | Where-Object { $_.Status -eq 'failed' }
if ($missingRequired) {
    Write-Host '  Some pieces are missing. The app still starts; it will tell you what is absent.' -ForegroundColor Yellow
}

Write-Host ''
Write-Host '  Next steps:' -ForegroundColor Magenta
Write-Host '    1. Neural voice: Pocket TTS is managed locally by Iris (CPU only, capped to two logical processors).'
Write-Host '       Runtime and model cache live under %LOCALAPPDATA%\CompanionOS\runtimes and models.'
Write-Host '    2. Start her:  powershell -ExecutionPolicy Bypass -File scripts/run.ps1'
Write-Host ''
