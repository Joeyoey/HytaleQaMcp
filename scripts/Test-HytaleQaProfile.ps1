param(
    [Parameter(Mandatory = $true)]
    [string] $Profile
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$qaHome = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$profilePath = [IO.Path]::GetFullPath($Profile)
$cli = Join-Path $qaHome (
    'src\Hytale.Qa.Cli\bin\Release\' +
    'net9.0-windows10.0.20348.0\Hytale.Qa.Cli.dll')
if (-not (Test-Path -LiteralPath $cli -PathType Leaf)) {
    & dotnet build (Join-Path $qaHome 'Hytale.Qa.sln') -c Release
    if ($LASTEXITCODE -ne 0) { throw 'hytale-qa-release-build-failed' }
}
$env:HYTALE_QA_HOME = $qaHome
$env:HYTALE_QA_PROFILE = $profilePath
& dotnet $cli profile-validate
exit $LASTEXITCODE

