#!/bin/bash

# macOS DMG Installer Creator for FPVTrackside Core
# This script creates a professional DMG installer from the .app bundle

set -e  # Exit on any error

# Configuration
APP_NAME="FPVTrackside"
DMG_NAME="FPVTrackside-Installer"
VOLUME_NAME="FPVTrackside Installer"
BUNDLE_ID="com.fpvtrackside.core"
VERSION="2.0.68"
SOURCE_DIR="./dist"
OUTPUT_DIR="./dist"

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
    echo "  -a, --arch ARCH        Target architecture (osx-x64, osx-arm64, or universal, default: osx-x64)"
    echo "  -s, --source DIR       Source directory containing .app bundle (default: ./dist)"
    echo "  -o, --output DIR       Output directory for DMG (default: ./dist)"
    echo "  -n, --name NAME        DMG file name without extension (default: FPVTrackside-Installer)"
    echo "  -v, --volume NAME      Volume name (default: FPVTrackside Installer)"
    echo "  --sign IDENTITY        Code signing identity for DMG (optional)"
    echo "  -h, --help             Show this help message"
    echo ""
    echo "Examples:"
    echo "  $0                                    # Create DMG for Intel Macs"
    echo "  $0 --arch osx-arm64                 # Create DMG for Apple Silicon"
    echo "  $0 --arch universal                 # Create universal DMG (requires both .app bundles)"
    echo "  $0 --sign \"Developer ID Application: Your Name\""
}

# Parse command line arguments
ARCH="osx-x64"
SIGNING_IDENTITY=""

while [[ $# -gt 0 ]]; do
    case $1 in
        -a|--arch)
            ARCH="$2"
            shift 2
            ;;
        -s|--source)
            SOURCE_DIR="$2"
            shift 2
            ;;
        -o|--output)
            OUTPUT_DIR="$2"
            shift 2
            ;;
        -n|--name)
            DMG_NAME="$2"
            shift 2
            ;;
        -v|--volume)
            VOLUME_NAME="$2"
            shift 2
            ;;
        --sign)
            SIGNING_IDENTITY="$2"
            shift 2
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

# Check if running on macOS
if [[ "$OSTYPE" != "darwin"* ]]; then
    print_error "This script must be run on macOS"
    exit 1
fi

# Set output name based on architecture
if [ "$ARCH" = "osx-arm64" ]; then
    DMG_OUTPUT="$OUTPUT_DIR/${DMG_NAME}-${VERSION}-AppleSilicon.dmg"
elif [ "$ARCH" = "osx-x64" ]; then
    DMG_OUTPUT="$OUTPUT_DIR/${DMG_NAME}-${VERSION}-Intel.dmg"
elif [ "$ARCH" = "universal" ]; then
    DMG_OUTPUT="$OUTPUT_DIR/${DMG_NAME}-${VERSION}-Universal.dmg"
else
    DMG_OUTPUT="$OUTPUT_DIR/${DMG_NAME}-${VERSION}.dmg"
fi

print_status "Creating DMG installer for $APP_NAME"
print_status "Architecture: $ARCH"
print_status "Source directory: $SOURCE_DIR"
print_status "Output DMG: $DMG_OUTPUT"

# Check if app bundle exists
APP_BUNDLE="$SOURCE_DIR/$APP_NAME.app"
if [ ! -d "$APP_BUNDLE" ]; then
    print_error "App bundle not found at: $APP_BUNDLE"
    print_error "Please run ./scripts/create-macos-app.sh first to create the app bundle"
    exit 1
fi

# Create output directory
mkdir -p "$OUTPUT_DIR"

# Remove existing DMG if it exists
if [ -f "$DMG_OUTPUT" ]; then
    print_status "Removing existing DMG..."
    rm -f "$DMG_OUTPUT"
fi

# Create temporary directory for DMG contents
TEMP_DIR=$(mktemp -d)
DMG_DIR="$TEMP_DIR/dmg"
mkdir -p "$DMG_DIR"

print_status "Preparing DMG contents..."

# Copy app bundle to DMG directory
cp -R "$APP_BUNDLE" "$DMG_DIR/"

# Create Applications symlink
ln -s /Applications "$DMG_DIR/Applications"

# Create background image if we can
BACKGROUND_DIR="$DMG_DIR/.background"
mkdir -p "$BACKGROUND_DIR"

# Create a simple background image using built-in tools
create_background_image() {
    local bg_file="$BACKGROUND_DIR/background.png"
    
    # Try to create a simple background using sips (if available)
    if command -v sips &> /dev/null; then
        # Create a solid color background (we'll use built-in patterns)
        cat > "$TEMP_DIR/create_bg.sh" << 'EOF'
#!/bin/bash
# Create a simple gradient background
bg_width=600
bg_height=400
bg_file="$1"

# Create a temporary solid color image and convert it
printf "P3\n$bg_width $bg_height\n255\n" > "$TEMP_DIR/temp.ppm"
for ((y=0; y<bg_height; y++)); do
    for ((x=0; x<bg_width; x++)); do
        # Create a simple gradient from light blue to white
        r=$((240 + (y * 15 / bg_height)))
        g=$((245 + (y * 10 / bg_height)))
        b=$((255))
        printf "$r $g $b "
    done
    printf "\n"
done >> "$TEMP_DIR/temp.ppm"

# Convert to PNG
sips -s format png "$TEMP_DIR/temp.ppm" --out "$bg_file" 2>/dev/null || {
    # Fallback: create a solid color background
    sips -s format png -z 400 600 /System/Library/Desktop\ Pictures/Solid\ Colors/Solid\ Aqua\ Blue.png --out "$bg_file" 2>/dev/null || true
}
EOF
        chmod +x "$TEMP_DIR/create_bg.sh"
        "$TEMP_DIR/create_bg.sh" "$bg_file" || true
    fi
    
    # If we couldn't create a background, that's ok - DMG will work without it
    if [ ! -f "$bg_file" ]; then
        print_warning "Could not create background image, DMG will use default appearance"
        rm -rf "$BACKGROUND_DIR"
    else
        print_status "Background image created"
    fi
}

create_background_image

# Create DS_Store file for proper icon positioning
create_ds_store() {
    cat > "$TEMP_DIR/set_dmg_layout.applescript" << EOF
tell application "Finder"
    tell disk "$VOLUME_NAME"
        open
        set current view of container window to icon view
        set toolbar visible of container window to false
        set statusbar visible of container window to false
        set the bounds of container window to {100, 100, 700, 500}
        set viewOptions to the icon view options of container window
        set arrangement of viewOptions to not arranged
        set icon size of viewOptions to 128
        try
            set background picture of viewOptions to file ".background:background.png"
        end try
        set position of item "$APP_NAME.app" of container window to {150, 200}
        set position of item "Applications" of container window to {450, 200}
        close
        open
        update without registering applications
        delay 2
    end tell
end tell
EOF
}

# Calculate DMG size
print_status "Calculating DMG size..."
DMG_SIZE=$(du -sk "$DMG_DIR" | cut -f1)
DMG_SIZE=$((DMG_SIZE + 10240))  # Add 10MB padding

print_status "Creating temporary DMG..."

# Create temporary DMG
TEMP_DMG="$TEMP_DIR/temp.dmg"
hdiutil create -srcfolder "$DMG_DIR" -volname "$VOLUME_NAME" -fs HFS+ -fsargs "-c c=64,a=16,e=16" -format UDRW -size ${DMG_SIZE}k "$TEMP_DMG"

# Mount the temporary DMG
print_status "Mounting DMG for customization..."
MOUNT_DIR=$(hdiutil attach -readwrite -noverify -noautoopen "$TEMP_DMG" | egrep '^/dev/' | sed 1q | awk '{print $3}')

if [ -z "$MOUNT_DIR" ]; then
    print_error "Failed to mount temporary DMG"
    rm -rf "$TEMP_DIR"
    exit 1
fi

# Set up DMG appearance
if [ -f "$BACKGROUND_DIR/background.png" ]; then
    # Copy background to mounted DMG
    mkdir -p "$MOUNT_DIR/.background"
    cp "$BACKGROUND_DIR/background.png" "$MOUNT_DIR/.background/"
fi

# Apply DMG customizations using AppleScript
print_status "Customizing DMG appearance..."
create_ds_store

# Run AppleScript to set up the DMG layout
if [ -f "$TEMP_DIR/set_dmg_layout.applescript" ]; then
    osascript "$TEMP_DIR/set_dmg_layout.applescript" 2>/dev/null || {
        print_warning "Could not apply custom DMG layout via AppleScript"
        print_warning "The DMG will still work but may not have optimal appearance"
    }
fi

# Hide background folder
if [ -d "$MOUNT_DIR/.background" ]; then
    chflags hidden "$MOUNT_DIR/.background"
fi

# Sync and unmount
print_status "Finalizing DMG..."
sync
hdiutil detach "$MOUNT_DIR"

# Convert to compressed read-only DMG
print_status "Compressing DMG..."
hdiutil convert "$TEMP_DMG" -format UDZO -imagekey zlib-level=9 -o "$DMG_OUTPUT"

# Code sign the DMG if requested
if [ -n "$SIGNING_IDENTITY" ]; then
    print_status "Code signing DMG..."
    codesign --force --sign "$SIGNING_IDENTITY" "$DMG_OUTPUT"
    
    if [ $? -eq 0 ]; then
        print_success "DMG code signing completed"
        
        # Verify signature
        print_status "Verifying DMG signature..."
        codesign --verify --strict "$DMG_OUTPUT"
        if [ $? -eq 0 ]; then
            print_success "DMG signature verification passed"
        else
            print_warning "DMG signature verification failed"
        fi
    else
        print_error "DMG code signing failed"
        exit 1
    fi
fi

# Clean up
rm -rf "$TEMP_DIR"

print_success "DMG installer created successfully!"
print_status "DMG location: $DMG_OUTPUT"
print_status "DMG size: $(du -sh "$DMG_OUTPUT" | cut -f1)"

# Display installation instructions
print_status "Installation instructions for users:"
echo "  1. Double-click the DMG file to mount it"
echo "  2. Drag $APP_NAME.app to the Applications folder"
echo "  3. Eject the DMG"
echo "  4. Launch $APP_NAME from Applications or Spotlight"

# Test the DMG
print_status "Testing DMG..."
if hdiutil verify "$DMG_OUTPUT" >/dev/null 2>&1; then
    print_success "DMG verification passed"
else
    print_warning "DMG verification failed - the DMG may be corrupted"
fi

print_status "To test the installer: open \"$DMG_OUTPUT\"" 