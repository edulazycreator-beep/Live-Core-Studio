#!/usr/bin/env bash
# Linux CI: builds everything that is portable and runs all tests that do not need Windows.
#   - native engine + unit tests (GCC/Clang, Unix-socket transport)
#   - Win32 code path compile check via MinGW-w64 (if installed); runs its tests under Wine if available
#   - managed core libraries + test suite, including real C# <-> C++ integration tests
# The WPF projects are Windows-only and are not built here.
set -euo pipefail
cd "$(dirname "$0")/.."

echo "== native (linux-dev) =="
cmake --preset linux-dev
cmake --build --preset linux-dev -j"$(nproc)"
ctest --preset linux-dev

if command -v x86_64-w64-mingw32-g++-posix >/dev/null; then
  echo "== native (mingw cross-compile, Win32 named pipes) =="
  cmake --preset mingw-cross-x64
  cmake --build --preset mingw-cross-x64 -j"$(nproc)"
  WINE="$(command -v wine || true)"; [ -z "$WINE" ] && [ -x /usr/lib/wine/wine64 ] && WINE=/usr/lib/wine/wine64
  if [ -n "$WINE" ]; then
    WINEDEBUG=-all "$WINE" out/build/mingw-cross-x64/bin/livecore_core_tests.exe
  fi
fi

echo "== managed (portable projects) =="
dotnet build LiveCoreStudio.Portable.slnf -c Release
LIVECORE_ENGINE_PATH="$PWD/out/build/linux-dev/bin/LiveCore.MediaEngine" \
  dotnet out/managed/bin/LiveCoreStudio.Tests/Release/net10.0/LiveCoreStudio.Tests.dll
