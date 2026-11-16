<#
.SYNOPSIS
  Builds and tests Live Core Studio on Windows.
.DESCRIPTION
  1. Configures and builds the native engine with CMake (MSVC, Ninja).
  2. Runs native unit tests (CTest).
  3. Builds the .NET solution (WPF app included); the engine is copied next to the app.
  4. Runs the managed test suite, including C# <-> C++ integration tests.
  Run from a "x64 Native Tools Command Prompt for VS 2022" or a Developer PowerShell.
.EXAMPLE
  ./scripts/build.ps1 -Configuration Release
#>
param(
    [ValidateSet('Debug', 'Release')] [string] $Configuration = 'Debug',
    [switch] $SkipTests
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    Write-Host "== Native engine ($Configuration) ==" -ForegroundColor DarkYellow
    cmake --preset windows-msvc-x64
    if ($LASTEXITCODE) { throw 'CMake configure failed' }
    cmake --build --preset "windows-msvc-x64-$($Configuration.ToLower())"
    if ($LASTEXITCODE) { throw 'Native build failed' }

    if (-not $SkipTests -and $Configuration -eq 'Debug') {
        ctest --preset windows-msvc-x64-debug
        if ($LASTEXITCODE) { throw 'Native tests failed' }
    }

    Write-Host "== Managed solution ($Configuration) ==" -ForegroundColor DarkYellow
    dotnet build LiveCoreStudio.sln -c $Configuration
    if ($LASTEXITCODE) { throw '.NET build failed' }

    if (-not $SkipTests) {
        $env:LIVECORE_ENGINE_PATH = Join-Path $root "out\build\windows-msvc-x64\bin\$Configuration\LiveCore.MediaEngine.exe"
        dotnet run --project tests/LiveCoreStudio.Tests -c $Configuration --no-build
        if ($LASTEXITCODE) { throw 'Managed tests failed' }
    }

    Write-Host "Done. App: out\managed\bin\LiveCoreStudio.App\$Configuration\net10.0-windows\LiveCoreStudio.exe" -ForegroundColor Green
}
finally {
    Pop-Location
}
