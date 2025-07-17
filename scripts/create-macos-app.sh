#!/bin/bash

# macOS App Bundle Creator for FPVTrackside Core
# This script creates a proper .app bundle from the published .NET executable

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

# Function to show usage
show_usage() {
    echo "Usage: $0 [options]"
    echo ""
    echo "Options:"
    echo "  -a, --arch ARCH        Target architecture (osx-x64 or osx-arm64, default: osx-x64)"
    echo "  -o, --output DIR       Output directory (default: ./dist)"
    echo "  -c, --configuration    Build configuration (Debug or Release, default: Release)"
    echo "  -s, --sign IDENTITY    Code signing identity (optional)"
    echo "  --notarize             Enable notarization (requires code signing)"
    echo "  -h, --help             Show this help message"
    echo ""
    echo "Examples:"
    echo "  $0                                    # Build for Intel Macs"
    echo "  $0 --arch osx-arm64                 # Build for Apple Silicon"
    echo "  $0 --sign \"Developer ID Application: Your Name\""
    echo "  $0 --arch osx-arm64 --output ./release"
}

# Parse command line arguments
ARCH="osx-x64"
OUTPUT_DIR="./dist"
CONFIGURATION="Release"
SIGNING_IDENTITY=""
NOTARIZE=false

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

# Validate architecture
if [[ "$ARCH" != "osx-x64" && "$ARCH" != "osx-arm64" ]]; then
    print_error "Invalid architecture: $ARCH. Must be osx-x64 or osx-arm64"
    exit 1
fi

# Validate configuration
if [[ "$CONFIGURATION" != "Debug" && "$CONFIGURATION" != "Release" ]]; then
    print_error "Invalid configuration: $CONFIGURATION. Must be Debug or Release"
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

print_status "Building macOS app bundle for $APP_NAME"
print_status "Architecture: $ARCH"
print_status "Configuration: $CONFIGURATION"
print_status "Output directory: $OUTPUT_DIR"

# Create output directory
mkdir -p "$OUTPUT_DIR"

# Create temporary build directory
TEMP_DIR=$(mktemp -d)
PUBLISH_DIR="$TEMP_DIR/publish"
APP_BUNDLE="$OUTPUT_DIR/$APP_NAME.app"

print_status "Publishing .NET application..."

# Publish the application
dotnet publish "$PROJECT_PATH" \
    --configuration "$CONFIGURATION" \
    --runtime "$ARCH" \
    --self-contained true \
    --output "$PUBLISH_DIR" \
    -p:PublishSingleFile=true \
    -p:IncludeNativeLibrariesForSelfExtract=true \
    -p:PublishTrimmed=false

if [ $? -ne 0 ]; then
    print_error "Failed to publish .NET application"
    rm -rf "$TEMP_DIR"
    exit 1
fi

print_success "Application published successfully"

# Remove existing app bundle if it exists
if [ -d "$APP_BUNDLE" ]; then
    print_status "Removing existing app bundle..."
    rm -rf "$APP_BUNDLE"
fi

print_status "Creating app bundle structure..."

# Create app bundle directories
mkdir -p "$APP_BUNDLE/Contents/MacOS"
mkdir -p "$APP_BUNDLE/Contents/Resources"

# Copy executable
cp "$PUBLISH_DIR/$EXECUTABLE_NAME" "$APP_BUNDLE/Contents/MacOS/"

# Copy additional files
if [ -d "$PUBLISH_DIR/bitmapfonts" ]; then
    cp -R "$PUBLISH_DIR/bitmapfonts" "$APP_BUNDLE/Contents/Resources/"
fi

# Copy any other resource files
for file in "$PUBLISH_DIR"/*; do
    filename=$(basename "$file")
    # Skip the main executable and already copied directories
    if [ "$filename" != "$EXECUTABLE_NAME" ] && [ "$filename" != "bitmapfonts" ]; then
        if [ -d "$file" ]; then
            cp -R "$file" "$APP_BUNDLE/Contents/Resources/"
        elif [ -f "$file" ] && [ "$filename" != "Info.plist" ]; then
            cp "$file" "$APP_BUNDLE/Contents/Resources/"
        fi
    fi
done

# Move native libraries from Resources to MacOS for MonoGame compatibility
if [ -f "$APP_BUNDLE/Contents/Resources/libSDL2.dylib" ]; then
    mv "$APP_BUNDLE/Contents/Resources"/*.dylib "$APP_BUNDLE/Contents/MacOS/" 2>/dev/null || true
    print_status "Native libraries moved to MacOS directory for proper loading"
fi

print_status "Creating Info.plist..."

# Create Info.plist
cat > "$APP_BUNDLE/Contents/Info.plist" << EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleDisplayName</key>
    <string>$APP_NAME</string>
    <key>CFBundleExecutable</key>
    <string>$EXECUTABLE_NAME</string>
    <key>CFBundleIdentifier</key>
    <string>$BUNDLE_ID</string>
    <key>CFBundleName</key>
    <string>$APP_NAME</string>
    <key>CFBundleShortVersionString</key>
    <string>$VERSION</string>
    <key>CFBundleVersion</key>
    <string>$VERSION</string>
    <key>CFBundlePackageType</key>
    <string>APPL</string>
    <key>CFBundleInfoDictionaryVersion</key>
    <string>6.0</string>
    <key>LSMinimumSystemVersion</key>
    <string>10.14</string>
    <key>LSApplicationCategoryType</key>
    <string>public.app-category.sports</string>
    <key>NSCameraUsageDescription</key>
    <string>FPVTrackside needs access to your camera to capture video feeds from USB capture devices for drone racing events.</string>
    <key>NSCameraUseContinuityCameraDeviceType</key>
    <true/>
    <key>NSHumanReadableCopyright</key>
    <string>Copyright © FPVTrackside</string>
    <key>NSPrincipalClass</key>
    <string>NSApplication</string>
    <key>NSHighResolutionCapable</key>
    <true/>
    <key>LSUIElement</key>
    <false/>
</dict>
</plist>
EOF

# Copy or create app icon if available
ICON_SOURCE="FPVTracksideCore/Icon.ico"
if [ -f "$ICON_SOURCE" ]; then
    print_status "Converting icon..."
    # Convert .ico to .icns if possible
    if command -v sips &> /dev/null; then
        mkdir -p "$TEMP_DIR/icon.iconset"
        
        # Extract different sizes from the ico file and create iconset
        for size in 16 32 128 256 512; do
            sips -s format png -z $size $size "$ICON_SOURCE" --out "$TEMP_DIR/icon.iconset/icon_${size}x${size}.png" 2>/dev/null || true
            if [ $((size * 2)) -le 1024 ]; then
                sips -s format png -z $((size * 2)) $((size * 2)) "$ICON_SOURCE" --out "$TEMP_DIR/icon.iconset/icon_${size}x${size}@2x.png" 2>/dev/null || true
            fi
        done
        
        # Create .icns file
        iconutil -c icns "$TEMP_DIR/icon.iconset" -o "$APP_BUNDLE/Contents/Resources/Icon.icns" 2>/dev/null || true
        
        if [ -f "$APP_BUNDLE/Contents/Resources/Icon.icns" ]; then
            # Add icon reference to Info.plist
            plutil -replace CFBundleIconFile -string "Icon.icns" "$APP_BUNDLE/Contents/Info.plist"
            print_success "Icon converted and added"
        else
            print_warning "Icon conversion failed, app will use default icon"
        fi
    else
        print_warning "sips command not available, cannot convert icon"
    fi
else
    print_warning "No icon file found at $ICON_SOURCE"
fi

# Make executable
chmod +x "$APP_BUNDLE/Contents/MacOS/$EXECUTABLE_NAME"

print_success "App bundle created at: $APP_BUNDLE"

# Code signing
if [ -n "$SIGNING_IDENTITY" ]; then
    print_status "Code signing the application..."
    
    # Sign all binaries first
    find "$APP_BUNDLE" -type f -perm +111 -exec codesign --force --deep --sign "$SIGNING_IDENTITY" {} \;
    
    # Sign the app bundle
    codesign --force --deep --sign "$SIGNING_IDENTITY" "$APP_BUNDLE"
    
    if [ $? -eq 0 ]; then
        print_success "Code signing completed"
        
        # Verify signature
        print_status "Verifying code signature..."
        codesign --verify --deep --strict "$APP_BUNDLE"
        if [ $? -eq 0 ]; then
            print_success "Code signature verification passed"
        else
            print_warning "Code signature verification failed"
        fi
        
        # Notarization
        if [ "$NOTARIZE" = true ]; then
            print_status "Notarization requested but not implemented in this script"
            print_status "Please use xcrun notarytool or altool manually for notarization"
        fi
    else
        print_error "Code signing failed"
        exit 1
    fi
else
    print_warning "No code signing identity provided. App will not be signed."
    print_warning "The app may show security warnings when launched on other Macs."
fi

# Clean up
rm -rf "$TEMP_DIR"

print_success "macOS app bundle creation completed!"
print_status "App bundle location: $APP_BUNDLE"

# Display app info
print_status "App bundle information:"
echo "  Name: $APP_NAME"
echo "  Version: $VERSION"
echo "  Architecture: $ARCH"
echo "  Bundle ID: $BUNDLE_ID"
echo "  Size: $(du -sh "$APP_BUNDLE" | cut -f1)"

print_status "To test the app: open \"$APP_BUNDLE\""
print_status "To create a DMG installer, run: ./scripts/create-macos-dmg.sh" 