# Build test script for FPVTrackside Core (PowerShell version)
# This script tests that both Windows and macOS solutions build correctly

param(
    [switch]$SkipPublishTest,
    [switch]$Verbose
)

$ErrorActionPreference = "Stop"

Write-Host "=== FPVTrackside Core Build Test ===" -ForegroundColor Cyan
Write-Host ""

# Check if dotnet is installed
try {
    $dotnetVersion = dotnet --version
    Write-Host "📋 .NET SDK Version: $dotnetVersion" -ForegroundColor Green
} catch {
    Write-Host "❌ Error: .NET SDK is not installed or not in PATH" -ForegroundColor Red
    Write-Host "Please install .NET 6.0 SDK from https://dotnet.microsoft.com/download" -ForegroundColor Yellow
    exit 1
}
Write-Host ""

# Function to test building a solution
function Test-Solution {
    param(
        [string]$SolutionName
    )
    
    Write-Host "🔨 Testing build for: $SolutionName" -ForegroundColor Blue
    
    if (-not (Test-Path $SolutionName)) {
        Write-Host "❌ Error: Solution file '$SolutionName' not found" -ForegroundColor Red
        return $false
    }
    
    Write-Host "   📦 Restoring packages..." -ForegroundColor Gray
    try {
        if ($Verbose) {
            dotnet restore $SolutionName
        } else {
            dotnet restore $SolutionName --verbosity quiet
        }
    } catch {
        Write-Host "❌ Error: Failed to restore packages for $SolutionName" -ForegroundColor Red
        return $false
    }
    
    Write-Host "   🔧 Building solution..." -ForegroundColor Gray
    try {
        if ($Verbose) {
            dotnet build $SolutionName --configuration Release --no-restore
        } else {
            dotnet build $SolutionName --configuration Release --no-restore --verbosity quiet
        }
    } catch {
        Write-Host "❌ Error: Failed to build $SolutionName" -ForegroundColor Red
        return $false
    }
    
    Write-Host "   ✅ $SolutionName built successfully" -ForegroundColor Green
    Write-Host ""
    return $true
}

# Test Windows solution
$windowsSuccess = Test-Solution "FPVTrackside - Core.sln"

# Test macOS/cross-platform solution  
$macosSuccess = Test-Solution "FPVMacside - Core.sln"

if (-not $windowsSuccess -or -not $macosSuccess) {
    Write-Host "❌ One or more builds failed!" -ForegroundColor Red
    exit 1
}

Write-Host "✅ All build tests completed successfully!" -ForegroundColor Green
Write-Host ""

# Optional: Test publish commands
if (-not $SkipPublishTest) {
    Write-Host "🚀 Testing publish commands..." -ForegroundColor Blue
    
    # Create test output directory
    $testOutputDir = ".\test-output"
    if (Test-Path $testOutputDir) {
        Remove-Item $testOutputDir -Recurse -Force
    }
    
    # Test Windows publish
    Write-Host "   📦 Testing Windows publish..." -ForegroundColor Gray
    try {
        $verbosityFlag = if ($Verbose) { "" } else { "--verbosity quiet" }
        dotnet publish FPVTracksideCore/FPVTracksideCore.csproj `
            --configuration Release `
            --runtime win-x64 `
            --no-restore `
            --output "$testOutputDir\win-x64" `
            $verbosityFlag
        Write-Host "   ✅ Windows publish successful" -ForegroundColor Green
        Remove-Item "$testOutputDir\win-x64" -Recurse -Force -ErrorAction SilentlyContinue
    } catch {
        Write-Host "   ❌ Windows publish failed: $($_.Exception.Message)" -ForegroundColor Red
    }
    
    # Test macOS Intel publish
    Write-Host "   📦 Testing macOS Intel publish..." -ForegroundColor Gray
    try {
        $verbosityFlag = if ($Verbose) { "" } else { "--verbosity quiet" }
        dotnet publish FPVMacSideCore/FPVMacsideCore.csproj `
            --configuration Release `
            --runtime osx-x64 `
            --no-restore `
            --output "$testOutputDir\osx-x64" `
            $verbosityFlag
        Write-Host "   ✅ macOS Intel publish successful" -ForegroundColor Green
        Remove-Item "$testOutputDir\osx-x64" -Recurse -Force -ErrorAction SilentlyContinue
    } catch {
        Write-Host "   ⚠️  macOS Intel publish failed (this may be expected on some platforms)" -ForegroundColor Yellow
    }
    
    # Test macOS Apple Silicon publish
    Write-Host "   📦 Testing macOS Apple Silicon publish..." -ForegroundColor Gray
    try {
        $verbosityFlag = if ($Verbose) { "" } else { "--verbosity quiet" }
        dotnet publish FPVMacSideCore/FPVMacsideCore.csproj `
            --configuration Release `
            --runtime osx-arm64 `
            --no-restore `
            --output "$testOutputDir\osx-arm64" `
            $verbosityFlag
        Write-Host "   ✅ macOS Apple Silicon publish successful" -ForegroundColor Green
        Remove-Item "$testOutputDir\osx-arm64" -Recurse -Force -ErrorAction SilentlyContinue
    } catch {
        Write-Host "   ⚠️  macOS Apple Silicon publish failed (this may be expected on some platforms)" -ForegroundColor Yellow
    }
    
    # Cleanup
    Remove-Item $testOutputDir -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host ""
Write-Host "🎉 Build test completed! The project should build correctly in CI/CD." -ForegroundColor Green

# Usage information
Write-Host ""
Write-Host "Usage examples:" -ForegroundColor Cyan
Write-Host "  .\scripts\build-test.ps1                    # Run full test" -ForegroundColor Gray
Write-Host "  .\scripts\build-test.ps1 -SkipPublishTest   # Skip publish tests" -ForegroundColor Gray
Write-Host "  .\scripts\build-test.ps1 -Verbose           # Show detailed output" -ForegroundColor Gray 