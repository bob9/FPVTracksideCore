#!/bin/bash

# Unified macOS Installer Builder for FPVTrackside Core
# This script builds both .app bundle and DMG installer in one command

set -e  # Exit on any error

# Configuration
APP_NAME="FPVTrackside"
BUNDLE_ID="com.fpvtrackside.core"
VERSION="2.0.68"
EXECUTABLE_NAME="FPVMacSideCore"
PROJECT_PATH="FPVMacSideCore/FPVMacsideCore.csproj"

# Colors for output
RED='\033[0;31m'
GREEN='\033[0;32m'
BLUE='\033[0;34m'
YELLOW='\033[1;33m'
PURPLE='\033[0;35m'
NC='\033[0m' # No Color

# Function to print colored output
print_status() {
    echo -e "${BLUE}[INFO]${NC} $1"
}

print_success() {
    echo -e "${GREEN}[SUCCESS]${NC} $1"
}

print_warning() {
    echo -e "${YELLOW}[WARNING]${NC} $1"
}

print_error() {
    echo -e "${RED}[ERROR]${NC} $1"
}

print_header() {
    echo -e "${PURPLE}[BUILD]${NC} $1"
}

# Function to show usage
show_usage() {
    echo "FPVTrackside Core - macOS Installer Builder"
    echo ""
    echo "Usage: $0 [options]"
    echo ""
    echo "Options:"
    echo "  -a, --arch ARCH        Target architecture (osx-x64, osx-arm64, or both, default: both)"
    echo "  -o, --output DIR       Output directory (default: ./dist)"
    echo "  -c, --configuration    Build configuration (Debug or Release, default: Release)"
    echo "  -s, --sign IDENTITY    Code signing identity (optional)"
    echo "  --notarize             Enable notarization (requires code signing)"
    echo "  --app-only             Build only .app bundle (skip DMG creation)"
    echo "  --dmg-only             Build only DMG (requires existing .app bundle)"
    echo "  --clean                Clean output directory before building"
    echo "  -h, --help             Show this help message"
    echo ""
    echo "Examples:"
    echo "  $0                                           # Build both Intel and Apple Silicon installers"
    echo "  $0 --arch osx-x64                          # Build only Intel installer"
    echo "  $0 --arch osx-arm64                        # Build only Apple Silicon installer"
    echo "  $0 --sign \"Developer ID Application: Your Name\"  # Build with code signing"
    echo "  $0 --app-only                              # Build only .app bundles"
    echo "  $0 --clean --output ./release              # Clean build to ./release directory"
    echo ""
    echo "Output files:"
    echo "  dist/FPVTrackside.app                      # macOS app bundle"
    echo "  dist/FPVTrackside-Installer-VERSION-Intel.dmg      # Intel DMG installer"
    echo "  dist/FPVTrackside-Installer-VERSION-AppleSilicon.dmg # Apple Silicon DMG installer"
}

# Parse command line arguments
ARCH="both"
OUTPUT_DIR="./dist"
CONFIGURATION="Release"
SIGNING_IDENTITY=""
NOTARIZE=false
APP_ONLY=false
DMG_ONLY=false
CLEAN=false

while [[ $# -gt 0 ]]; do
    case $1 in
        -a|--arch)
            ARCH="$2"
            shift 2
            ;;
        -o|--output)
            OUTPUT_DIR="$2"
            shift 2
            ;;
        -c|--configuration)
            CONFIGURATION="$2"
            shift 2
            ;;
        -s|--sign)
            SIGNING_IDENTITY="$2"
            shift 2
            ;;
        --notarize)
            NOTARIZE=true
            shift
            ;;
        --app-only)
            APP_ONLY=true
            shift
            ;;
        --dmg-only)
            DMG_ONLY=true
            shift
            ;;
        --clean)
            CLEAN=true
            shift
            ;;
        -h|--help)
            show_usage
            exit 0
            ;;
        *)
            print_error "Unknown option: $1"
            show_usage
            exit 1
            ;;
    esac
done

# Validate arguments
if [[ "$ARCH" != "osx-x64" && "$ARCH" != "osx-arm64" && "$ARCH" != "both" ]]; then
    print_error "Invalid architecture: $ARCH. Must be osx-x64, osx-arm64, or both"
    exit 1
fi

if [[ "$CONFIGURATION" != "Debug" && "$CONFIGURATION" != "Release" ]]; then
    print_error "Invalid configuration: $CONFIGURATION. Must be Debug or Release"
    exit 1
fi

if [[ "$APP_ONLY" = true && "$DMG_ONLY" = true ]]; then
    print_error "Cannot specify both --app-only and --dmg-only"
    exit 1
fi

# Check if running on macOS
if [[ "$OSTYPE" != "darwin"* ]]; then
    print_error "This script must be run on macOS"
    exit 1
fi

# Check if dotnet is installed
if ! command -v dotnet &> /dev/null; then
    print_error ".NET SDK is not installed or not in PATH"
    print_error "Please install .NET 6.0 SDK from https://dotnet.microsoft.com/download"
    exit 1
fi

# Check if required scripts exist
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
APP_SCRIPT="$SCRIPT_DIR/create-macos-app.sh"
DMG_SCRIPT="$SCRIPT_DIR/create-macos-dmg.sh"

if [[ "$DMG_ONLY" = false && ! -f "$APP_SCRIPT" ]]; then
    print_error "App bundle script not found: $APP_SCRIPT"
    exit 1
fi

if [[ "$APP_ONLY" = false && ! -f "$DMG_SCRIPT" ]]; then
    print_error "DMG script not found: $DMG_SCRIPT"
    exit 1
fi

print_header "FPVTrackside Core - macOS Installer Build"
print_status "Configuration: $CONFIGURATION"
print_status "Architecture(s): $ARCH"
print_status "Output directory: $OUTPUT_DIR"
if [ -n "$SIGNING_IDENTITY" ]; then
    print_status "Code signing: $SIGNING_IDENTITY"
    if [ "$NOTARIZE" = true ]; then
        print_status "Notarization: Enabled"
    fi
else
    print_warning "Code signing: Disabled"
fi

# Clean output directory if requested
if [ "$CLEAN" = true ]; then
    print_status "Cleaning output directory..."
    rm -rf "$OUTPUT_DIR"
fi

# Create output directory
mkdir -p "$OUTPUT_DIR"

# Determine which architectures to build
if [ "$ARCH" = "both" ]; then
    ARCHITECTURES=("osx-x64" "osx-arm64")
else
    ARCHITECTURES=("$ARCH")
fi

# Build function
build_for_arch() {
    local arch="$1"
    local arch_name=""
    
    if [ "$arch" = "osx-x64" ]; then
        arch_name="Intel"
    elif [ "$arch" = "osx-arm64" ]; then
        arch_name="Apple Silicon"
    fi
    
    print_header "Building for $arch_name ($arch)"
    
    # Build app bundle if not DMG-only
    if [ "$DMG_ONLY" = false ]; then
        print_status "Creating .app bundle for $arch_name..."
        
        local app_args=(
            "--arch" "$arch"
            "--output" "$OUTPUT_DIR"
            "--configuration" "$CONFIGURATION"
        )
        
        if [ -n "$SIGNING_IDENTITY" ]; then
            app_args+=("--sign" "$SIGNING_IDENTITY")
            if [ "$NOTARIZE" = true ]; then
                app_args+=("--notarize")
            fi
        fi
        
        chmod +x "$APP_SCRIPT"
        "$APP_SCRIPT" "${app_args[@]}"
        
        if [ $? -ne 0 ]; then
            print_error "Failed to create app bundle for $arch_name"
            return 1
        fi
        
        print_success "App bundle created for $arch_name"
    fi
    
    # Build DMG if not app-only
    if [ "$APP_ONLY" = false ]; then
        print_status "Creating DMG installer for $arch_name..."
        
        local dmg_args=(
            "--arch" "$arch"
            "--source" "$OUTPUT_DIR"
            "--output" "$OUTPUT_DIR"
        )
        
        if [ -n "$SIGNING_IDENTITY" ]; then
            dmg_args+=("--sign" "$SIGNING_IDENTITY")
        fi
        
        chmod +x "$DMG_SCRIPT"
        "$DMG_SCRIPT" "${dmg_args[@]}"
        
        if [ $? -ne 0 ]; then
            print_error "Failed to create DMG installer for $arch_name"
            return 1
        fi
        
        print_success "DMG installer created for $arch_name"
    fi
}

# Build for each architecture
for arch in "${ARCHITECTURES[@]}"; do
    build_for_arch "$arch"
    
    if [ $? -ne 0 ]; then
        print_error "Build failed for $arch"
        exit 1
    fi
    
    echo ""
done

# Summary
print_header "Build Summary"
print_success "All builds completed successfully!"

if [ -d "$OUTPUT_DIR" ]; then
    print_status "Output directory contents:"
    ls -la "$OUTPUT_DIR" | while read line; do
        echo "  $line"
    done
    
    echo ""
    print_status "Total output size: $(du -sh "$OUTPUT_DIR" | cut -f1)"
fi

# Installation instructions
if [ "$APP_ONLY" = false ]; then
    print_status "Distribution instructions:"
    echo "  1. Upload the DMG files to your distribution platform"
    echo "  2. Users should download the appropriate DMG for their Mac:"
    echo "     - Intel Macs: *-Intel.dmg"
    echo "     - Apple Silicon Macs: *-AppleSilicon.dmg"
    echo "  3. Users double-click the DMG and drag the app to Applications"
fi

if [ "$DMG_ONLY" = false ]; then
    print_status "Development testing:"
    echo "  - Test the app bundle directly: open \"$OUTPUT_DIR/$APP_NAME.app\""
fi

if [ -z "$SIGNING_IDENTITY" ]; then
    print_warning "Unsigned builds may show security warnings on other Macs"
    print_warning "Consider using code signing for distribution"
fi

print_header "Build completed successfully! 🎉" 