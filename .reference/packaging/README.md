# Packaging

Patchy ships as native packages, one directory per platform:

- [windows](windows/README.md): signed installer and portable zip, built by
  `scripts\release\build-release.bat`.
- [macos](macos/README.md): signed and notarized DMG.
- [linux](linux/README.md): Flatpak, as a signed repository and a single-file bundle.
- `web`: the WebAssembly site shell.
- [branding](branding/README.md): logo and icon source art.

The release order, safety checks and upload scripts are in
[docs/release-process.md](../docs/release-process.md).
