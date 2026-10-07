# Building Patchy

[Back to Patchy](../README.md)

## Build from source

Build the dependency-light core and tests without the Qt app:

```sh
cmake --preset dev -DPATCHY_BUILD_APP=OFF
cmake --build --preset dev
ctest --preset dev
```

Build the Qt desktop app:

```sh
cmake --preset qt-local
cmake --build --preset qt-local
```

The local Qt app preset writes `patchy.exe` under `build/app`.

Run the standard local test script:

```powershell
powershell -ExecutionPolicy Bypass -File scripts/run-tests.ps1
```

### macOS and Linux

Install Qt 6.8.3 into `.deps/Qt` (for example `pip install aqtinstall && aqt install-qt
mac desktop 6.8.3 -m qtimageformats qtpdf -O .deps/Qt`, or `linux desktop 6.8.3
linux_gcc_64` on Linux), then build the matching preset. The `qtpdf` module is optional:
without it Patchy still exports PDF, it just cannot open one.

```sh
cmake --preset mac-release      # or linux-release
cmake --build --preset mac-release
```

macOS produces `build/mac-release/Patchy.app`; Linux produces
`build/linux-release/patchy`. `packaging/macos/make-dmg.sh` and
`packaging/linux/make-flatpak.sh` create the distributable artifacts. Both test suites
run offscreen on all three platforms (`QT_QPA_PLATFORM=offscreen`).

## Windows Release Package

Create local Windows release artifacts:

```bat
scripts\release\build-release.bat
```

The script configures and builds the `release` preset, signs `build\release\patchy.exe`, the installer helper executables, and the installer when the local signing environment is available, deploys the minimum Qt runtime needed by the current app, copies third-party notices, and creates:

```text
build\package\PatchyWindowsNoInstaller.zip
build\package\PatchyWindowsInstaller.exe
```

The zip contains a top-level `Patchy` folder so it can be dragged anywhere and does not include installer-only helpers. The installer is a local per-user installer that installs to `%LOCALAPPDATA%\Programs\Patchy`, creates a Start Menu shortcut, offers a desktop shortcut, and registers an uninstall entry.  `latest_version.json` is the update metadata file.
