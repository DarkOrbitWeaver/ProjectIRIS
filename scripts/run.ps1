<#
.SYNOPSIS
    Builds and starts CompanionOS.

.DESCRIPTION
    A thin wrapper so the project can be started the same way every time, with a clear
    error if the .NET 9 SDK is missing. Pass -BuildOnly to compile without launching,
    or -Release for an optimised build.
#>
[CmdletBinding()]
param(
    [switch]$Release,
    [switch]$BuildOnly
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$configuration = if ($Release) { 'Release' } else { 'Debug' }
$projectPath = Join-Path $repositoryRoot 'src/CompanionOS.App/CompanionOS.App.csproj'

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Host '.NET SDK not found. Install the .NET 9 SDK, then run this script again.' -ForegroundColor Red
    exit 1
}

Write-Host "Building CompanionOS ($configuration)…" -ForegroundColor Magenta
dotnet build $projectPath -c $configuration
if ($LASTEXITCODE -ne 0) {
    Write-Host 'Build failed. The errors above say which file needs attention.' -ForegroundColor Red
    exit $LASTEXITCODE
}

if ($BuildOnly) { exit 0 }

Write-Host 'Starting Iris…' -ForegroundColor Green
dotnet run --project $projectPath -c $configuration --no-build
