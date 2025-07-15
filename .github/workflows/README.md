# GitHub Actions Workflows

This directory contains the GitHub Actions workflows for building and releasing FPVTrackside Core.

## Workflows

### 1. Continuous Integration (`ci.yml`)

**Triggers:**
- Push to `master`, `main`, or `develop` branches
- Pull requests to `master` or `main` branches

**What it does:**
- Builds the Windows version (`FPVTrackside - Core.sln`) on Windows runners
- Builds the macOS version (`FPVMacside - Core.sln`) on macOS runners  
- Runs a cross-platform build test on Linux
- Runs tests (if available) to ensure code quality

**Purpose:**
Ensures that every check-in builds successfully on all target platforms before merging.

### 2. Release (`release.yml`)

**Triggers:**
- Push of tags matching `v*` pattern (e.g., `v2.0.68`, `v1.0.0`)

**What it does:**
- Creates a GitHub release with the tag name
- Builds and publishes Windows executable (`FPVTracksideCore.exe`)
- Builds and publishes macOS executables for both Intel (`osx-x64`) and Apple Silicon (`osx-arm64`)
- Uploads all binaries as release assets

**Release Artifacts:**
- `FPVTracksideCore-{version}-win-x64.zip` - Windows executable
- `FPVMacSideCore-{version}-osx-x64.zip` - macOS Intel executable  
- `FPVMacSideCore-{version}-osx-arm64.zip` - macOS Apple Silicon executable

## Creating a Release

To create a new release:

1. **Update version numbers** in your project files if needed
2. **Commit and push** all changes to the `master` branch
3. **Create and push a tag:**
   ```bash
   git tag v2.0.69
   git push origin v2.0.69
   ```
4. **Wait for the workflow** to complete (~10-15 minutes)
5. **Check the Releases page** on GitHub for your new release

## Build Configuration

### Windows Build
- **Project**: `FPVTracksideCore/FPVTracksideCore.csproj`
- **Solution**: `FPVTrackside - Core.sln`
- **Target Framework**: `net6.0-windows`
- **Runtime**: `win-x64`
- **Output**: Self-contained single-file executable

### macOS Build
- **Project**: `FPVMacSideCore/FPVMacsideCore.csproj`
- **Solution**: `FPVMacside - Core.sln`
- **Target Framework**: `net6.0`
- **Runtimes**: `osx-x64` (Intel) and `osx-arm64` (Apple Silicon)
- **Output**: Self-contained single-file executables

## Directory.Build.props

The `Directory.Build.props` file at the root provides common build settings for all projects:
- Consistent versioning across all projects
- Common metadata (company, copyright, etc.)
- Build optimizations for release builds
- Source linking for better debugging in CI environments

## Troubleshooting

### Build Failures
- Check that all project references are correct
- Ensure all required NuGet packages are available
- Verify that the target framework is properly set

### Release Issues
- Ensure the tag follows the `v*` pattern (e.g., `v1.0.0`)
- Check that the tag was pushed to the repository
- Verify GitHub token permissions are sufficient for creating releases

### Platform-Specific Issues
- **Windows**: Ensure Windows-specific dependencies are properly configured
- **macOS**: Check that MonoGame.Framework.DesktopGL is used instead of WindowsDX
- **Cross-platform**: Use appropriate conditional compilation directives where needed

## Local Development

To build locally:

```bash
# Windows
dotnet restore "FPVTrackside - Core.sln"
dotnet build "FPVTrackside - Core.sln" --configuration Release

# macOS/Linux  
dotnet restore "FPVMacside - Core.sln"
dotnet build "FPVMacside - Core.sln" --configuration Release
```

To create local release builds:

```bash
# Windows
dotnet publish FPVTracksideCore/FPVTracksideCore.csproj --configuration Release --runtime win-x64 --self-contained true

# macOS Intel
dotnet publish FPVMacSideCore/FPVMacsideCore.csproj --configuration Release --runtime osx-x64 --self-contained true

# macOS Apple Silicon
dotnet publish FPVMacSideCore/FPVMacsideCore.csproj --configuration Release --runtime osx-arm64 --self-contained true
``` 