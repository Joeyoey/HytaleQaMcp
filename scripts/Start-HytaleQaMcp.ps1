param(
    [Parameter(Mandatory = $true)]
    [string] $Profile,
    [switch] $Build
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$qaHome = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$profilePath = [IO.Path]::GetFullPath($Profile)
if (-not (Test-Path -LiteralPath $profilePath -PathType Leaf)) {
    throw "hytale-qa-profile-not-found:$profilePath"
}

$server = Join-Path $qaHome (
    'src\Hytale.Qa.Mcp\bin\Release\' +
    'net9.0-windows10.0.20348.0\Hytale.Qa.Mcp.dll')
if ($Build -or -not (Test-Path -LiteralPath $server -PathType Leaf)) {
    & dotnet build (Join-Path $qaHome 'Hytale.Qa.sln') -c Release
    if ($LASTEXITCODE -ne 0) { throw 'hytale-qa-release-build-failed' }
}
if (-not (Test-Path -LiteralPath $server -PathType Leaf)) {
    throw 'hytale-qa-mcp-release-build-missing'
}

$previousHome = [Environment]::GetEnvironmentVariable('HYTALE_QA_HOME', 'Process')
$previousProfile = [Environment]::GetEnvironmentVariable('HYTALE_QA_PROFILE', 'Process')
try {
    [Environment]::SetEnvironmentVariable('HYTALE_QA_HOME', $qaHome, 'Process')
    [Environment]::SetEnvironmentVariable('HYTALE_QA_PROFILE', $profilePath, 'Process')
    & dotnet $server
    exit $LASTEXITCODE
} finally {
    [Environment]::SetEnvironmentVariable('HYTALE_QA_HOME', $previousHome, 'Process')
    [Environment]::SetEnvironmentVariable('HYTALE_QA_PROFILE', $previousProfile, 'Process')
}

