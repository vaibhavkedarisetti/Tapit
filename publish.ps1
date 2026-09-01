<#
.SYNOPSIS
    Publishes Tapit as a self-contained single-file x64 build.

.DESCRIPTION
    Produces a folder that runs on any Windows 10 1809+ x64 machine with no .NET install.
    There is no installer and no code signing; both are deliberate gaps rather than
    oversights, and are listed as such in the README.

    Trimming is NOT enabled. The WASAPI layer resolves COM interfaces through built-in
    interop that a trimmer cannot see, and a trimmed build fails at runtime rather than at
    build time - which is the worst possible way to find out.
#>
[CmdletBinding()]
param(
    [string]$Output = "artifacts/publish",
    [ValidateSet("Release", "Debug")]
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"

$dotnet = Join-Path $env:ProgramFiles "dotnet\dotnet.exe"
if (-not (Test-Path $dotnet)) {
    $dotnet = "dotnet"
}

Write-Host "Building and testing before publish..." -ForegroundColor Cyan
& $dotnet test "Tapit.sln" -c $Configuration --nologo
if ($LASTEXITCODE -ne 0) {
    throw "Tests failed. Not publishing."
}

$projects = @(
    @{ Path = "src/Tapit.App/Tapit.App.csproj";               Name = "Tapit (app)" },
    @{ Path = "tools/Tapit.MicCheck/Tapit.MicCheck.csproj";   Name = "MicCheck" },
    @{ Path = "tools/Tapit.AudioReplay/Tapit.AudioReplay.csproj"; Name = "AudioReplay" }
)

foreach ($project in $projects) {
    Write-Host ""
    Write-Host "Publishing $($project.Name)..." -ForegroundColor Cyan

    & $dotnet publish $project.Path `
        -c $Configuration `
        -r win-x64 `
        --self-contained true `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true `
        -p:DebugType=none `
        -o $Output `
        --nologo

    if ($LASTEXITCODE -ne 0) {
        throw "Publish failed for $($project.Name)."
    }
}

Write-Host ""
Write-Host "Published to $Output" -ForegroundColor Green
Get-ChildItem $Output -Filter *.exe | ForEach-Object {
    "  {0,-26} {1,8:N1} MB" -f $_.Name, ($_.Length / 1MB)
}

Write-Host ""
Write-Host "Reminder: Tapit has not been validated against a real desk tap." -ForegroundColor Yellow
Write-Host "Calibrate and run an evaluation before trusting any zone binding." -ForegroundColor Yellow
