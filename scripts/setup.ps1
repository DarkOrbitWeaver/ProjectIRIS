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
    all, brain, runtime, ears or voice. Defaults to all.

.PARAMETER Force
    Re-download even when the file already exists.

.PARAMETER VerifyOnly
    Only report what is present; download nothing.

.PARAMETER KokoroDirectML
    Replace Kokoro's CPU ONNX Runtime with the optional DirectML runtime.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts/setup.ps1
#>
[CmdletBinding()]
param(
    [ValidateSet('all', 'brain', 'runtime', 'ears', 'voice')]
    [string]$Component = 'all',
    [switch]$Force,
    [switch]$VerifyOnly,
    [switch]$KokoroDirectML
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
function Provision-KokoroVoice {
    param([string]$DataRoot, [switch]$Force, [bool]$VerifyOnly, [bool]$KokoroDirectML)

    $kokoroRuntimeDir = Join-Path $DataRoot 'runtimes\kokoro-tts'
    $kokoroModelDir = Join-Path $DataRoot 'models\kokoro-cache'
    $kokoroPython = Join-Path $kokoroRuntimeDir 'venv\Scripts\python.exe'
    $kokoroScript = Join-Path $kokoroRuntimeDir 'kokoro-server.py'
    $modelPresent = (Test-Path (Join-Path $kokoroModelDir 'kokoro-v1.0.onnx')) -and
                    (Test-Path (Join-Path $kokoroModelDir 'voices-v1.0.bin'))

    if ($VerifyOnly) {
        $script:results += [pscustomobject]@{ Artifact = 'kokoro-runtime'; Status = if ((Test-Path $kokoroPython) -and (Test-Path $kokoroScript)) { 'present' } else { 'missing' } }
        $script:results += [pscustomobject]@{ Artifact = 'kokoro-model'; Status = if ($modelPresent) { 'present' } else { 'missing' } }
        if ($KokoroDirectML -and (Test-Path $kokoroPython)) {
            $provider = & $kokoroPython -c "import onnxruntime as ort; print('DmlExecutionProvider' in ort.get_available_providers())"
            $script:results += [pscustomobject]@{ Artifact = 'kokoro-directml'; Status = if ($provider -eq 'True') { 'present' } else { 'missing' } }
        }
        return
    }

    try {
        New-Item -ItemType Directory -Force -Path $kokoroRuntimeDir, $kokoroModelDir | Out-Null

        if ((-not (Test-Path $kokoroPython)) -or (-not (Test-Path $kokoroScript)) -or $Force) {
            $version = & python --version 2>&1
            if ($LASTEXITCODE -ne 0 -or $version -notmatch '3\.(1[0-3])') {
                throw "Kokoro needs Python 3.10-3.13 on the system PATH; found: $version"
            }
            Write-Host '  creating the Kokoro runtime environment (kokoro-onnx 0.6.1, no CUDA packages)' -ForegroundColor Cyan
            python -m venv $kokoroRuntimeDir\venv
            if ($LASTEXITCODE -ne 0) { throw 'Could not create the Kokoro python environment.' }
            & $kokoroPython -m pip install --quiet kokoro-onnx==0.6.1 misaki soundfile
            if ($LASTEXITCODE -ne 0) { throw 'kokoro-onnx installation failed.' }
        } else {
            Write-Host '  present: Kokoro runtime; keeping the existing environment' -ForegroundColor DarkGray
        }

        Copy-Item (Join-Path $PSScriptRoot 'kokoro-server.py') $kokoroScript -Force

        if ($KokoroDirectML) {
            $provider = & $kokoroPython -c "import onnxruntime as ort; print('DmlExecutionProvider' in ort.get_available_providers())"
            if ($provider -ne 'True') {
                Write-Host '  installing optional ONNX Runtime DirectML provider' -ForegroundColor Cyan
                & $kokoroPython -m pip uninstall --quiet --yes onnxruntime onnxruntime-directml
                & $kokoroPython -m pip install --quiet --no-deps onnxruntime-directml==1.22.0
                if ($LASTEXITCODE -ne 0) { throw 'ONNX Runtime DirectML installation failed.' }
            }
            $provider = & $kokoroPython -c "import onnxruntime as ort; print('DmlExecutionProvider' in ort.get_available_providers())"
            if ($provider -ne 'True') { throw 'DirectML installed, but ONNX Runtime does not expose DmlExecutionProvider.' }
        }

        Write-Host '  verifying Kokoro artifacts (pinned, SHA-256 checked)' -ForegroundColor Cyan
        & $kokoroPython (Join-Path $PSScriptRoot 'prepare-kokoro.py') $kokoroModelDir
        if ($LASTEXITCODE -ne 0) { throw 'Kokoro model download failed. Check network access and rerun.' }
        $script:results += [pscustomobject]@{ Artifact = 'kokoro-voice'; Status = 'downloads verified' }
    }
    catch {
        Write-Host "  failed: Kokoro setup — $($_.Exception.Message)" -ForegroundColor Red
        $script:results += [pscustomobject]@{ Artifact = 'kokoro-voice'; Status = 'failed' }
    }
}

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
    param([string]$Url, [string]$Destination, [string]$Label, [string]$Sha256)

    Write-Host "  downloading $Label" -ForegroundColor Cyan
    $temporaryPath = "$Destination.part"
    if (Test-Path $temporaryPath) { Remove-Item $temporaryPath -Force }

    Invoke-WebRequest -Uri $Url -OutFile $temporaryPath -UseBasicParsing
    if ($Sha256) {
        $actualHash = (Get-FileHash -LiteralPath $temporaryPath -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($actualHash -ne $Sha256.ToLowerInvariant()) {
            Remove-Item -LiteralPath $temporaryPath -Force -ErrorAction SilentlyContinue
            throw "SHA-256 verification failed for $Label."
        }
    }
    Move-Item -Path $temporaryPath -Destination $Destination -Force

    $sizeMb = [math]::Round((Get-Item $Destination).Length / 1MB, 1)
    Write-Host "  done: $Label ($sizeMb MB)" -ForegroundColor Green
}

function Get-ReleaseAssetUrl {
    param([string]$Repo, [string]$Pattern, [string]$FallbackPattern, [string]$ReleaseTag)

    # Some releases carry no binaries at all (brand tags such as v0.7.0); the actual
    # builds land on the newest per-build tags. Walk the newest releases and use the
    # first one that actually contains what the manifest asks for.
    $releaseUri = if ($ReleaseTag) {
        "https://api.github.com/repos/$Repo/releases/tags/$ReleaseTag"
    } else {
        "https://api.github.com/repos/$Repo/releases?per_page=15"
    }
    $releases = Invoke-RestMethod -Uri $releaseUri -UseBasicParsing
    if ($ReleaseTag) { $releases = @($releases) }
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
        ears    = @('ears-parakeet', 'ears-transcribe-runtime')
        runtime = @('runtime', 'runtime-cuda')
        voice   = @('pocket-uv')
    }

    if ($Component -ne 'all' -and $componentArtifacts[$Component] -notcontains $artifact.id) {
        continue
    }

    if ($artifact.id -eq 'pocket-uv' -and -not $Force -and
        (Test-Path (Join-Path $runtimeDir 'pocket-tts\pocket-tts.exe'))) {
        Write-Host '  present: compatible Pocket TTS runtime; keeping the existing installation' -ForegroundColor DarkGray
        $results += [pscustomobject]@{ Artifact = $artifact.id; Status = 'present' }
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
            Get-ChildItem -Path $artifactFolder -Filter $artifact.sentinelFile -File -Recurse -ErrorAction SilentlyContinue |
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
                Save-Download -Url $artifact.url -Destination $targetPath -Label $artifact.targetName `
                    -Sha256 $artifact.sha256
            }

            'github-release' {
                $releaseAsset = Get-ReleaseAssetUrl -Repo $artifact.repo -Pattern $artifact.assetPattern `
                    -FallbackPattern $artifact.fallbackAssetPattern -ReleaseTag $artifact.releaseTag
                $zipPath = Join-Path $env:TEMP $releaseAsset.Name
                Save-Download -Url $releaseAsset.Url -Destination $zipPath -Label $releaseAsset.Name -Sha256 $artifact.sha256

                $extractPath = Join-Path $runtimeDir $artifact.targetName
                if (-not $artifact.overlay) {
                    # The primary runtime is unpacked clean each time. Overlay artifacts
                    # (the CUDA DLL pack) must merge into it, never wipe it.
                    if (Test-Path $extractPath) { Remove-Item $extractPath -Recurse -Force }
                }
                New-Item -ItemType Directory -Force -Path $extractPath | Out-Null
                if ($releaseAsset.Name -like '*.tar.gz' -or $releaseAsset.Name -like '*.tgz') {
                    # tar ships with Windows 10+; the transcribe.cpp runtime arrives as a .tar.gz.
                    tar -xzf $zipPath -C $extractPath
                    if ($LASTEXITCODE -ne 0) { throw "tar extraction failed for $releaseAsset.Name." }
                } else {
                    Expand-Archive -Path $zipPath -DestinationPath $extractPath -Force
                }
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

$voiceRequested = $Component -in @('all', 'voice')
if ($voiceRequested) {
    $voiceRuntimeDir = Join-Path $runtimeDir 'pocket-tts'
    $voiceModelDir = Join-Path $modelsDir 'pocket-tts-cache'
    $voiceExecutable = Join-Path $voiceRuntimeDir 'pocket-tts.exe'
    $pocketPython = Join-Path $voiceRuntimeDir 'tools\pocket-tts\Scripts\python.exe'
    $voiceRuntimePresent = Test-Path $voiceExecutable
    $voiceRequiredFiles = @('model.safetensors', 'tokenizer.model', 'alba.safetensors', 'anna.safetensors',
        'eve.safetensors', 'mary.safetensors', 'vera.safetensors', 'estelle.safetensors')
    $voiceMissingFiles = @()
    foreach ($requiredFile in $voiceRequiredFiles) {
        if (-not (Get-ChildItem -Path $voiceModelDir -Filter $requiredFile -File -Recurse -ErrorAction SilentlyContinue |
                Select-Object -First 1)) {
            $voiceMissingFiles += $requiredFile
        }
    }
    $voiceModelPresent = $voiceMissingFiles.Count -eq 0

    if ($VerifyOnly) {
        $results += [pscustomobject]@{ Artifact = 'pocket-tts-runtime'; Status = if ($voiceRuntimePresent) { 'present' } else { 'missing or incomplete' } }
        $modelStatus = if ($voiceModelPresent) { 'present' } else { "missing: $($voiceMissingFiles -join ', ')" }
        $results += [pscustomobject]@{ Artifact = 'pocket-tts-model'; Status = $modelStatus }
    } else {
        try {
            New-Item -ItemType Directory -Force -Path $voiceRuntimeDir, $voiceModelDir | Out-Null
            $env:UV_CACHE_DIR = Join-Path $dataRoot 'cache\uv'
            $env:UV_TOOL_DIR = Join-Path $voiceRuntimeDir 'tools'
            $env:UV_TOOL_BIN_DIR = $voiceRuntimeDir
            $env:UV_PYTHON_INSTALL_DIR = Join-Path $runtimeDir 'python'
            $env:HF_HOME = $voiceModelDir

            if (-not $voiceRuntimePresent -or $Force) {
                $uvFolder = Join-Path $runtimeDir 'pocket-uv'
                $uvExecutable = Get-ChildItem -Path $uvFolder -Filter 'uv.exe' -File -Recurse -ErrorAction SilentlyContinue |
                    Select-Object -First 1 -ExpandProperty FullName
                if (-not $uvExecutable) { throw 'The pinned uv.exe installer is missing. Rerun setup with -Component voice.' }

                Write-Host '  installing Pocket TTS 4.0.1 and its Python 3.12 CPU dependencies (no CUDA packages)' -ForegroundColor Cyan
                & $uvExecutable tool install --python 3.12 --force 'pocket-tts==4.0.1'
                if ($LASTEXITCODE -ne 0) { throw "Pocket TTS installation failed (uv exit $LASTEXITCODE)." }
                if (-not (Test-Path $pocketPython)) { throw 'Pocket TTS installed, but its managed Python environment is incomplete.' }
            } else {
                Write-Host '  present: compatible Pocket TTS runtime; keeping the existing installation' -ForegroundColor DarkGray
            }

            if (-not $voiceModelPresent -or $Force) {
                if (-not (Test-Path $pocketPython)) {
                    throw 'The existing Pocket runtime is present, but its model cache is incomplete. Rerun setup with -Component voice -Force to provision the managed model installer.'
                }
                $prepareScript = Join-Path $PSScriptRoot 'prepare-pocket-tts.py'
                & $pocketPython $prepareScript
                if ($LASTEXITCODE -ne 0) { throw "Pocket TTS model download failed (python exit $LASTEXITCODE). Check network access and rerun setup." }
                $results += [pscustomobject]@{ Artifact = 'pocket-tts-model'; Status = 'downloaded or verified' }
            } else {
                $results += [pscustomobject]@{ Artifact = 'pocket-tts-model'; Status = 'present' }
            }
            $results += [pscustomobject]@{ Artifact = 'pocket-tts-runtime'; Status = if (Test-Path $voiceExecutable) { 'present' } else { 'failed' } }
        }
        catch {
            Write-Host "  failed: Pocket TTS setup — $($_.Exception.Message)" -ForegroundColor Red
            $results += [pscustomobject]@{ Artifact = 'pocket-tts-runtime'; Status = 'failed' }
        }
    }
}

# Kokoro shares the voice component: her second local engine, provisioned the
# same honest way (pinned runtime, pinned artifacts, hash-verified downloads).
if ($voiceRequested) {
    Provision-KokoroVoice -DataRoot $dataRoot -Force:$Force -VerifyOnly:([bool]$VerifyOnly.IsPresent) -KokoroDirectML:([bool]$KokoroDirectML.IsPresent)
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
Write-Host '    1. Voice: Pocket TTS preset voices use the public no-cloning checkpoint; setup downloads only its English model and six selected voices.'
Write-Host '       The runtime and model cache live under %LOCALAPPDATA%\CompanionOS\runtimes and models.'
Write-Host '    2. Start her:  powershell -ExecutionPolicy Bypass -File scripts/run.ps1'
Write-Host ''
