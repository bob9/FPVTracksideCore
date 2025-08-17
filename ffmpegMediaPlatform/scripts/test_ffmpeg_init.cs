using System;
using System.IO;
using FFmpeg.AutoGen;

namespace FfmpegTesting
{
    class TestFFmpegInit
    {
        static int Main(string[] args)
        {
            try
            {
                Console.WriteLine("=== FFmpeg Initialization Test ===");
                Console.WriteLine($"Architecture: {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}");
                Console.WriteLine($"OS: {System.Runtime.InteropServices.RuntimeInformation.OSDescription}");
                
                // Test if we can find the libraries
                var libPath = args.Length > 0 ? args[0] : "/Users/glenn.pringle/Documents/trackside_deploy/FPVTracksideCore/FPVMacSideCore/bin/Debug/net6.0/ffmpeg-libs/macos";
                Console.WriteLine($"Testing library path: {libPath}");
                
                if (!Directory.Exists(libPath))
                {
                    Console.WriteLine("ERROR: Library directory not found");
                    return 1;
                }
                
                var files = Directory.GetFiles(libPath, "*.dylib");
                Console.WriteLine($"Found {files.Length} dylib files");
                
                // Set FFmpeg root path
                ffmpeg.RootPath = libPath;
                Console.WriteLine($"Set ffmpeg.RootPath = {ffmpeg.RootPath}");
                
                // Test basic functions
                Console.WriteLine("Testing av_version_info...");
                var version = ffmpeg.av_version_info();
                Console.WriteLine($"SUCCESS: FFmpeg version = {version}");
                
                Console.WriteLine("Testing version functions...");
                var codecVersion = ffmpeg.avcodec_version();
                var formatVersion = ffmpeg.avformat_version();
                Console.WriteLine($"SUCCESS: Codec version = {codecVersion}, Format version = {formatVersion}");
                
                Console.WriteLine("Testing av_find_input_format...");
                unsafe
                {
                    var avfFormat = ffmpeg.av_find_input_format("avfoundation");
                    Console.WriteLine($"SUCCESS: AVFoundation format = {(avfFormat != null ? "FOUND" : "NOT FOUND")}");
                }
                
                Console.WriteLine("=== All tests passed! ===");
                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"ERROR: {ex.GetType().Name}: {ex.Message}");
                Console.WriteLine($"Stack trace: {ex.StackTrace}");
                return 1;
            }
        }
    }
}