#!/bin/bash

# Build test script for FPVTrackside Core
# This script tests that both Windows and macOS solutions build correctly

set -e  # Exit on any error

echo "=== FPVTrackside Core Build Test ==="
echo ""

# Check if dotnet is installed
if ! command -v dotnet &> /dev/null; then
    echo "❌ Error: .NET SDK is not installed or not in PATH"
    echo "Please install .NET 6.0 SDK from https://dotnet.microsoft.com/download"
    exit 1
fi

# Check .NET version
echo "📋 Checking .NET version..."
dotnet --version
echo ""

# Function to test building a solution
test_solution() {
    local solution_name="$1"
    echo "🔨 Testing build for: $solution_name"
    
    if [ ! -f "$solution_name" ]; then
        echo "❌ Error: Solution file '$solution_name' not found"
        return 1
    fi
    
    echo "   📦 Restoring packages..."
    if ! dotnet restore "$solution_name" --verbosity quiet; then
        echo "❌ Error: Failed to restore packages for $solution_name"
        return 1
    fi
    
    echo "   🔧 Building solution..."
    if ! dotnet build "$solution_name" --configuration Release --no-restore --verbosity quiet; then
        echo "❌ Error: Failed to build $solution_name"
        return 1
    fi
    
    echo "   ✅ $solution_name built successfully"
    echo ""
}

# Test Windows solution (if on Windows or has Windows workloads)
if [[ "$OSTYPE" == "msys" || "$OSTYPE" == "win32" || "$OSTYPE" == "cygwin" ]]; then
    test_solution "FPVTrackside - Core.sln"
else
    echo "⏭️  Skipping Windows solution (not on Windows platform)"
    echo ""
fi

# Test macOS/cross-platform solution
test_solution "FPVMacside - Core.sln"

echo "✅ All build tests completed successfully!"
echo ""

# Optional: Test publish commands
echo "🚀 Testing publish commands..."

echo "   📦 Testing macOS Intel publish..."
if dotnet publish FPVMacSideCore/FPVMacsideCore.csproj \
    --configuration Release \
    --runtime osx-x64 \
    --no-restore \
    --verbosity quiet \
    --output ./test-output/osx-x64; then
    echo "   ✅ macOS Intel publish successful"
    rm -rf ./test-output/osx-x64
else
    echo "   ⚠️  macOS Intel publish failed (this may be expected on some platforms)"
fi

echo "   📦 Testing macOS Apple Silicon publish..."
if dotnet publish FPVMacSideCore/FPVMacsideCore.csproj \
    --configuration Release \
    --runtime osx-arm64 \
    --no-restore \
    --verbosity quiet \
    --output ./test-output/osx-arm64; then
    echo "   ✅ macOS Apple Silicon publish successful"
    rm -rf ./test-output/osx-arm64
else
    echo "   ⚠️  macOS Apple Silicon publish failed (this may be expected on some platforms)"
fi

# Test Windows publish only on Windows
if [[ "$OSTYPE" == "msys" || "$OSTYPE" == "win32" || "$OSTYPE" == "cygwin" ]]; then
    echo "   📦 Testing Windows publish..."
    if dotnet publish FPVTracksideCore/FPVTracksideCore.csproj \
        --configuration Release \
        --runtime win-x64 \
        --no-restore \
        --verbosity quiet \
        --output ./test-output/win-x64; then
        echo "   ✅ Windows publish successful"
        rm -rf ./test-output/win-x64
    else
        echo "   ❌ Windows publish failed"
    fi
fi

# Cleanup
rm -rf ./test-output

echo ""
echo "🎉 Build test completed! The project should build correctly in CI/CD." 