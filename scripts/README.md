# Build Scripts

This directory contains scripts for building and testing FPVTrackside Core.

## macOS Installer Scripts

### Quick Start

```bash
# Build professional macOS installers (both Intel and Apple Silicon)
./scripts/build-macos-installer.sh

# Output: 
# - dist/FPVTrackside.app
# - dist/FPVTrackside-Installer-2.0.68-Intel.dmg
# - dist/FPVTrackside-Installer-2.0.68-AppleSilicon.dmg
```

### Scripts Overview

| Script | Description | Usage |
|--------|-------------|--------|
| **`build-macos-installer.sh`** | **Main script** - builds both .app bundles and DMG installers | `./scripts/build-macos-installer.sh [options]` |
| `create-macos-app.sh` | Creates macOS .app bundle from .NET executable | `./scripts/create-macos-app.sh --arch osx-arm64` |
| `create-macos-dmg.sh` | Creates DMG installer from .app bundle | `./scripts/create-macos-dmg.sh --arch osx-arm64` |

### Common Usage Examples

```bash
# Development: Quick app bundle for testing
./scripts/build-macos-installer.sh --arch osx-arm64 --app-only

# Release: Signed installers for distribution  
./scripts/build-macos-installer.sh --sign "Developer ID Application: Your Name"

# Debug: Debug build to custom location
./scripts/build-macos-installer.sh --configuration Debug --output ./debug-build --clean
```

### Requirements

- **macOS only** (scripts use macOS-specific tools)
- **.NET 6.0 SDK**: `brew install --cask dotnet-sdk`
- **Xcode Command Line Tools**: `xcode-select --install`

## Legacy Scripts

| Script | Description |
|--------|-------------|
| `build-test.sh` | Cross-platform build testing (Linux/macOS/Windows) |
| `build-test.ps1` | PowerShell version of build testing (Windows) |

## Documentation

For detailed instructions, troubleshooting, and code signing setup, see:
**[docs/MACOS_INSTALLER_GUIDE.md](../docs/MACOS_INSTALLER_GUIDE.md)**

## CI/CD

These scripts are used by the GitHub Actions workflow:
**`.github/workflows/build-macos-installer.yml`**

Manual CI trigger: **Actions** → **Build macOS Installer** → **Run workflow** 