<#
.SYNOPSIS
  Builds every GAC 2.0 component and installer on the development PC.

  1. dotnet publish (Release, win-x64, self-contained) for Server, Agent, Tray, Console
  2. Portable single-file GAC-Demo.exe
  3. Inno Setup: GAC-Server-Setup.exe, GAC-Console-Setup.exe, GAC-Agent-Setup.exe

  Output: /dist. School PCs never build anything and need no .NET runtime.
  Requires: .NET 10 SDK, Inno Setup 6 (ISCC.exe). NuGet restore uses the internet on the dev PC only.
#>
param(
    [string]$Configuration = "Release",
    [switch]$SkipTests
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$publish = Join-Path $root "artifacts\publish"
$dist = Join-Path $root "dist"

function Invoke-Step([string]$title, [scriptblock]$action) {
    Write-Host "==> $title" -ForegroundColor Cyan
    & $action
    if ($LASTEXITCODE -ne 0) { throw "$title failed (exit $LASTEXITCODE)" }
}

Remove-Item $publish, $dist -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $publish, $dist | Out-Null

if (-not $SkipTests) {
    Invoke-Step "dotnet test" { dotnet test (Join-Path $root "Gac.slnx") -c $Configuration --nologo -v q }
}

$common = @("-c", $Configuration, "-r", "win-x64", "--self-contained", "true", "--nologo", "-v", "q", "-p:DebugType=none", "-p:GenerateDocumentationFile=false")

$apps = [ordered]@{
    server  = "src\Gac.Server\Gac.Server.csproj"
    agent   = "src\Gac.Agent\Gac.Agent.csproj"
    tray    = "src\Gac.Tray\Gac.Tray.csproj"
    console = "src\Gac.Console\Gac.Console.csproj"
}
foreach ($name in $apps.Keys) {
    Invoke-Step "publish $name" { dotnet publish (Join-Path $root $apps[$name]) @common -o (Join-Path $publish $name) }
}

Invoke-Step "publish GAC-Demo.exe (single file)" {
    dotnet publish (Join-Path $root "src\Gac.Demo\Gac.Demo.csproj") @common `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
        -o (Join-Path $publish "demo")
}
Copy-Item (Join-Path $publish "demo\GAC-Demo.exe") $dist

$iscc = @(
    $env:ISCC,
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if (-not $iscc) { throw "Inno Setup 6 (ISCC.exe) not found. Install it or set the ISCC environment variable." }

foreach ($iss in "GAC-Server.iss", "GAC-Console.iss", "GAC-Agent.iss") {
    Invoke-Step "compile $iss" { & $iscc "/Q" "/DPublishDir=$publish" (Join-Path $root "installer\$iss") }
}

Write-Host ""
Write-Host "Done. Output in $dist" -ForegroundColor Green
Get-ChildItem $dist | Format-Table Name, @{ n = "MB"; e = { [math]::Round($_.Length / 1MB, 1) } } -AutoSize
