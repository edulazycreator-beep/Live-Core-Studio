# Building Live Core Studio

## Windows (product build)

### Prerequisites

| Tool | Version | Notes |
|---|---|---|
| Windows | 10 21H2+ / 11, x64 | |
| Visual Studio 2022 | 17.10+ | Workloads: *Desktop development with C++* (MSVC v143, Windows 11 SDK) and *.NET desktop development* |
| .NET SDK | 10.0.x | `dotnet --list-sdks` |
| CMake | 3.25+ | Bundled with VS, or standalone |
| Ninja | any | Bundled with VS |

### Steps

```powershell
# Developer PowerShell for VS 2022, from the repository root
cmake --preset windows-msvc-x64
cmake --build --preset windows-msvc-x64-debug
ctest --preset windows-msvc-x64-debug

dotnet build LiveCoreStudio.sln -c Debug          # copies the engine next to the app
$env:LIVECORE_ENGINE_PATH = "$PWD\out\build\windows-msvc-x64\bin\Debug\LiveCore.MediaEngine.exe"
dotnet run --project tests/LiveCoreStudio.Tests   # unit + C#↔C++ integration tests

.\out\managed\bin\LiveCoreStudio.App\Debug\net10.0-windows\LiveCoreStudio.exe
```

`scripts/build.ps1` runs all of the above.

Build the native engine **before** the .NET solution, so `LiveCoreStudio.App` can copy `LiveCore.MediaEngine.exe` next to `LiveCoreStudio.exe`. If it is missing, the build warns and the engine path can be set in **Settings**.

Opening `LiveCoreStudio.sln` in Visual Studio works for the managed side; open the folder (CMake mode) for the native side.

## Linux (CI only)

Builds the portable engine core, the managed core libraries and runs every test that does not need Windows, including real C#↔C++ integration tests over a Unix socket. If MinGW-w64 is installed it also cross-compiles the Win32 pipe code, and runs its tests under Wine when available.

```bash
sudo apt install cmake g++ dotnet-sdk-10.0 g++-mingw-w64-x86-64-posix wine64   # mingw/wine optional
./scripts/build-ci-linux.sh
```

## Engine command line

```
LiveCore.MediaEngine --pipe <name> [--parent-pid <pid>] [--clock-rate <hz>]
                     [--log-level trace|debug|info|warn|error|off] [--log-file <path>]
LiveCore.MediaEngine --version
```

Exit codes: `0` clean shutdown · `1` bad arguments · `2` pipe endpoint unavailable (already owned).
