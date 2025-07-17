# macOS Installer Build Guide

This guide explains how to build professional macOS installers for FPVTrackside Core, including both `.app` bundles and `.dmg` installer packages.

## Table of Contents

- [Quick Start](#quick-start)
- [Build Scripts Overview](#build-scripts-overview)
- [Local Development](#local-development)
- [Code Signing Setup](#code-signing-setup)
- [CI/CD Usage](#cicd-usage)
- [Troubleshooting](#troubleshooting)
- [Distribution](#distribution)

## Quick Start

### Prerequisites

- **macOS**: These build tools only work on macOS
- **.NET 6.0 SDK**: Install from [dotnet.microsoft.com](https://dotnet.microsoft.com/download)
- **Xcode Command Line Tools**: `xcode-select --install`

### Build Both Intel and Apple Silicon Installers

```bash
# Build everything (both architectures, .app bundles + DMG installers)
./scripts/build-macos-installer.sh

# Output:
# dist/FPVTrackside.app (universal or last built architecture)
# dist/FPVTrackside-Installer-2.0.68-Intel.dmg
# dist/FPVTrackside-Installer-2.0.68-AppleSilicon.dmg
```

### Build for Specific Architecture

```bash
# Intel Macs only
./scripts/build-macos-installer.sh --arch osx-x64

# Apple Silicon Macs only  
./scripts/build-macos-installer.sh --arch osx-arm64
```

## Build Scripts Overview

### 1. `build-macos-installer.sh` - Unified Builder

**Primary script** that orchestrates the entire build process.

```bash
Usage: ./scripts/build-macos-installer.sh [options]

Options:
  -a, --arch ARCH        Target architecture (osx-x64, osx-arm64, or both)
  -o, --output DIR       Output directory (default: ./dist)
  -c, --configuration    Build configuration (Debug or Release)
  -s, --sign IDENTITY    Code signing identity
  --app-only             Build only .app bundle (skip DMG)
  --dmg-only             Build only DMG (requires existing .app)
  --clean                Clean output directory before building
```

**Examples:**
```bash
# Standard release build for both architectures
./scripts/build-macos-installer.sh

# Development build for testing
./scripts/build-macos-installer.sh --arch osx-arm64 --configuration Debug --app-only

# Signed release build
./scripts/build-macos-installer.sh --sign "Developer ID Application: Your Name"

# Clean build to custom directory
./scripts/build-macos-installer.sh --clean --output ./release
```

### 2. `create-macos-app.sh` - App Bundle Creator

Creates proper macOS `.app` bundle from .NET executable.

```bash
Usage: ./scripts/create-macos-app.sh [options]

Options:
  -a, --arch ARCH        Target architecture (osx-x64 or osx-arm64)
  -o, --output DIR       Output directory
  -c, --configuration    Build configuration
  -s, --sign IDENTITY    Code signing identity
  --notarize             Enable notarization
```

### 3. `create-macos-dmg.sh` - DMG Installer Creator

Creates professional DMG installer with proper layout and background.

```bash
Usage: ./scripts/create-macos-dmg.sh [options]

Options:
  -a, --arch ARCH        Target architecture
  -s, --source DIR       Source directory containing .app bundle
  -o, --output DIR       Output directory for DMG
  --sign IDENTITY        Code signing identity for DMG
```

## Local Development

### Development Workflow

1. **Quick Testing** (fastest):
```bash
# Build app bundle only for your architecture
./scripts/build-macos-installer.sh --arch $(uname -m | sed 's/arm64/osx-arm64/; s/x86_64/osx-x64/') --app-only

# Test the app
open ./dist/FPVTrackside.app
```

2. **Full Testing**:
```bash
# Build DMG for testing installation process
./scripts/build-macos-installer.sh --arch osx-arm64

# Test the installer
open ./dist/FPVTrackside-Installer-2.0.68-AppleSilicon.dmg
```

3. **Debug Build**:
```bash
./scripts/build-macos-installer.sh --configuration Debug --app-only
```

### Manual Build Steps

If you need to understand or customize the build process:

```bash
# 1. Publish the .NET application
dotnet publish FPVMacSideCore/FPVMacsideCore.csproj \
  --configuration Release \
  --runtime osx-arm64 \
  --self-contained true \
  --output ./publish \
  -p:PublishSingleFile=true

# 2. Create app bundle structure
mkdir -p "FPVTrackside.app/Contents/MacOS"
mkdir -p "FPVTrackside.app/Contents/Resources"

# 3. Copy executable and resources
cp ./publish/FPVMacSideCore "FPVTrackside.app/Contents/MacOS/"
cp -R ./publish/bitmapfonts "FPVTrackside.app/Contents/Resources/"

# 4. Create Info.plist (see script for full content)
# 5. Make executable
chmod +x "FPVTrackside.app/Contents/MacOS/FPVMacSideCore"
```

## Code Signing Setup

### For Distribution (Recommended)

Code signing is essential for distributing macOS apps without security warnings.

#### 1. Get Apple Developer Account

- Sign up at [developer.apple.com](https://developer.apple.com)
- Enroll in Apple Developer Program ($99/year)

#### 2. Create Certificates

In Xcode or Apple Developer portal:

1. **Developer ID Application Certificate**: For direct distribution outside App Store
2. **Developer ID Installer Certificate**: For signed DMG files

#### 3. Find Your Signing Identity

```bash
# List available signing identities
security find-identity -v -p codesigning

# Look for entries like:
# "Developer ID Application: Your Name (TEAM_ID)"
# "Mac Developer: Your Name (TEAM_ID)"
```

#### 4. Sign Your Builds

```bash
# Build with code signing
./scripts/build-macos-installer.sh --sign "Developer ID Application: Your Name"

# Verify signatures
codesign --verify --deep --strict ./dist/FPVTrackside.app
codesign --verify --strict ./dist/FPVTrackside-Installer-2.0.68-Intel.dmg
```

#### 5. Notarization (Optional but Recommended)

Notarization removes additional security warnings on macOS 10.15+:

```bash
# Build with notarization flag
./scripts/build-macos-installer.sh --sign "Developer ID Application: Your Name" --notarize

# Manual notarization (after building)
xcrun notarytool submit ./dist/FPVTrackside-Installer-2.0.68-Intel.dmg \
  --apple-id your@email.com \
  --password your-app-specific-password \
  --team-id YOUR_TEAM_ID \
  --wait
```

### For Development (Unsigned)

For local development, you can build unsigned:

```bash
./scripts/build-macos-installer.sh
```

**Note**: Unsigned apps will show security warnings and require users to:
1. Go to System Preferences > Security & Privacy
2. Click "Open Anyway" after attempting to launch

## CI/CD Usage

### GitHub Actions Workflow

The project includes a comprehensive GitHub Actions workflow for automated building.

#### Automatic Builds (on Release Tags)

```bash
# Create and push a version tag
git tag v2.0.69
git push origin v2.0.69

# GitHub will automatically:
# 1. Build Intel and Apple Silicon installers
# 2. Create GitHub release
# 3. Upload DMG files as release assets
```

#### Manual Builds

Use the GitHub Actions "Build macOS Installer" workflow:

1. Go to **Actions** tab in GitHub
2. Select **Build macOS Installer** workflow
3. Click **Run workflow**
4. Choose options:
   - Architecture: `both`, `osx-x64`, or `osx-arm64`
   - Configuration: `Release` or `Debug`
   - Create DMG: `true` or `false`
   - Upload artifacts: `true` or `false`

#### Workflow Files

- **`.github/workflows/build-macos-installer.yml`**: Main installer workflow
- **`.github/workflows/release.yml`**: Legacy release workflow (now superseded)

### Code Signing in CI

To enable code signing in GitHub Actions:

1. **Add Certificates to Repository Secrets**:
   - `MACOS_CERTIFICATE`: Base64-encoded Developer ID Application certificate (.p12)
   - `MACOS_CERTIFICATE_PASSWORD`: Certificate password
   - `MACOS_INSTALLER_CERTIFICATE`: Base64-encoded Developer ID Installer certificate
   - `APPLE_ID`: Your Apple ID email
   - `APPLE_PASSWORD`: App-specific password
   - `APPLE_TEAM_ID`: Your developer team ID

2. **Update Workflow** to use certificates:
```yaml
- name: Import certificates
  run: |
    # Import signing certificates from secrets
    echo "${{ secrets.MACOS_CERTIFICATE }}" | base64 --decode > certificate.p12
    security create-keychain -p "" build.keychain
    security import certificate.p12 -k build.keychain -P "${{ secrets.MACOS_CERTIFICATE_PASSWORD }}" -T /usr/bin/codesign
    security set-key-partition-list -S apple-tool:,apple:,codesign: -s -k "" build.keychain

- name: Build signed installer
  run: |
    ./scripts/build-macos-installer.sh --sign "Developer ID Application: Your Name"
```

## Troubleshooting

### Common Issues

#### 1. "Developer cannot be verified" Error

**Problem**: macOS shows security warning when launching unsigned app.

**Solutions**:
- **For Distribution**: Use code signing (see above)
- **For Testing**: Right-click app → "Open" → Click "Open" in dialog
- **System-wide**: System Preferences → Security & Privacy → "Open Anyway"

#### 2. ".NET SDK not found"

**Problem**: Build fails with dotnet command not found.

**Solution**:
```bash
# Install .NET 6.0 SDK
brew install --cask dotnet-sdk

# Or download from: https://dotnet.microsoft.com/download
```

#### 3. "Permission denied" on Scripts

**Problem**: Build scripts aren't executable.

**Solution**:
```bash
chmod +x scripts/*.sh
```

#### 4. DMG Creation Fails

**Problem**: Background image or layout issues.

**Solution**:
- Ensure you have sufficient disk space
- Close any Finder windows showing mounted DMGs
- Try running with `--clean` flag

#### 5. App Bundle Invalid Structure

**Problem**: App won't launch or shows as broken.

**Solution**:
```bash
# Check bundle structure
ls -la ./dist/FPVTrackside.app/Contents/

# Verify Info.plist
plutil -lint ./dist/FPVTrackside.app/Contents/Info.plist

# Check executable permissions
ls -la ./dist/FPVTrackside.app/Contents/MacOS/
```

### Debug Mode

Enable verbose output for troubleshooting:

```bash
# Add debug flags to any script
./scripts/build-macos-installer.sh --arch osx-arm64 2>&1 | tee build.log
```

### Manual Testing

Test your installer thoroughly:

```bash
# 1. Test app bundle directly
open ./dist/FPVTrackside.app

# 2. Test DMG installation process  
open ./dist/FPVTrackside-Installer-2.0.68-AppleSilicon.dmg
# Drag to Applications, then launch from Applications

# 3. Test permissions
# The app should request camera access on first launch

# 4. Test on different macOS versions if possible
```

## Distribution

### Release Process

1. **Build Release Installers**:
```bash
./scripts/build-macos-installer.sh --sign "Developer ID Application: Your Name"
```

2. **Test Thoroughly**:
   - Test both architectures on appropriate hardware
   - Test installation process from DMG
   - Verify camera permissions work
   - Test on multiple macOS versions if possible

3. **Create GitHub Release**:
```bash
git tag v2.0.69
git push origin v2.0.69
# GitHub Actions will build and upload release assets
```

4. **Update Documentation**:
   - Update README with download links
   - Update version numbers
   - Document any breaking changes

### File Naming Convention

The build scripts use this naming convention:

- **App Bundle**: `FPVTrackside.app`
- **Intel DMG**: `FPVTrackside-Installer-{VERSION}-Intel.dmg`
- **Apple Silicon DMG**: `FPVTrackside-Installer-{VERSION}-AppleSilicon.dmg`
- **Release Info**: `RELEASE_INFO.md` (contains installation instructions)

### Distribution Channels

1. **GitHub Releases** (Primary):
   - Upload DMG files to GitHub releases
   - Include RELEASE_INFO.md content in release notes

2. **Direct Download**:
   - Host DMG files on your own server
   - Provide direct download links

3. **Package Managers** (Future):
   - Homebrew Cask: `brew install --cask fpvtrackside`
   - Mac App Store (requires additional setup)

### System Requirements

Include these requirements in your distribution:

- **Minimum macOS**: 10.14 (Mojave)
- **Recommended macOS**: 11.0 (Big Sur) or later
- **Architecture**: Intel or Apple Silicon
- **Permissions**: Camera access required
- **Storage**: ~50MB free space

---

## Additional Resources

- **Apple Developer Documentation**: [developer.apple.com/documentation](https://developer.apple.com/documentation/)
- **Code Signing Guide**: [developer.apple.com/support/code-signing](https://developer.apple.com/support/code-signing/)
- **Notarization Guide**: [developer.apple.com/documentation/notarization](https://developer.apple.com/documentation/notarization)
- **.NET on macOS**: [docs.microsoft.com/dotnet/core/install/macos](https://docs.microsoft.com/dotnet/core/install/macos)

## Support

If you encounter issues not covered in this guide:

1. Check the [Troubleshooting](#troubleshooting) section
2. Review GitHub Actions logs for CI/CD issues
3. Open an issue on the project repository with build logs 