using UI.Nodes;
using ImageServer;
using Microsoft.Xna.Framework.Graphics;
using RaceLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Tools;
using System.Threading;
using System.IO;
using Microsoft.Xna.Framework.Media;
using Composition;
using System.Text.RegularExpressions;
using UI;
using FfmpegMediaPlatform;
using static UI.Video.VideoManager;

namespace UI.Video
{
    public class VideoManager : IDisposable
    {
        public List<VideoConfig> VideoConfigs { get; private set; }

        private List<FrameSource> frameSources;
        public IEnumerable<FrameSource> FrameSources { get { return frameSources; } }

        public int ConnectedCount
        {
            get
            {
                lock (frameSources)
                {
                    return frameSources.Where(d => d.Connected).Count();
                }
            }
        }

        public bool Connected
        {
            get
            {
                lock (frameSources)
                {
                    return frameSources.All(d => d.Connected);
                }
            }
        }

        public bool MaintainConnections { get; set; }

        private Thread videoDeviceManagerThread;
        private bool runWorker;
        private List<Action> todo;

        private List<ICaptureFrameSource> recording;
        private Race race;

        private List<FrameSource> needsInitialize;
        private List<IDisposable> needsDispose;
        
        // Add device access tracking and cooldown management
        private Dictionary<string, DateTime> deviceLastFailTime = new Dictionary<string, DateTime>();
        private Dictionary<string, FrameSource> deviceActiveFrameSources = new Dictionary<string, FrameSource>();
        private readonly TimeSpan deviceRetryDelay = TimeSpan.FromSeconds(5); // 5 second cooldown between attempts

        public bool RunningDevices { get { return runWorker; } }

        public bool AutoPause { get; set; }
        public bool NeedsInit
        {
            get
            {
                return needsInitialize.Count > 0;
            }
        }

        public int DeviceCount { get { return VideoConfigs.Count; } }

        public delegate void FrameSourceDelegate(FrameSource frameSource);
        public delegate void FrameSourcesDelegate(IEnumerable<FrameSource> frameSources);

        public event FrameSourceDelegate OnStart;

        private AutoResetEvent mutex;

        public DirectoryInfo EventDirectory { get; private set; }

        public Profile Profile { get; private set; }

        public bool Finalising
        {
            get
            {
                lock (frameSources)
                {
                    return frameSources.OfType<ICaptureFrameSource>().Any(r => r.Finalising);
                }
            }
        }

        public event Action OnFinishedFinalizing;

        public VideoManager(string eventDirectory, Profile profile)
        {
            Profile = profile;
            EventDirectory = new DirectoryInfo(eventDirectory);

            todo = new List<Action>();
            mutex = new AutoResetEvent(false);
            Logger.VideoLog.LogCall(this);

            recording = new List<ICaptureFrameSource>();
            needsInitialize = new List<FrameSource>();
            needsDispose = new List<IDisposable>();

            frameSources = new List<FrameSource>();
            VideoConfigs = new List<VideoConfig>();
            AutoPause = false;
        }

        private VideoFrameWork GetFramework(FrameWork frameWork)
        {
            return VideoFrameWorks.Available.FirstOrDefault(f => f.FrameWork == frameWork);
        }

        public void LoadCreateDevices(FrameSourcesDelegate frameSources)
        {
            LoadDevices();
            CreateFrameSource(VideoConfigs, frameSources);
        }


        public void LoadDevices()
        {
            Clear();

            MaintainConnections = true;

            VideoConfigs.Clear();
            VideoConfigs.AddRange(VideoConfig.Read(Profile));

            // Update ffmpegId values for macOS cameras based on actual detected devices
            UpdateFfmpegIdsForMacCameras();

            StartThread();
        }

        private void UpdateFfmpegIdsForMacCameras()
        {
            try
            {
                Logger.VideoLog.Log(this, "Updating ffmpegId values for macOS cameras using direct FFmpeg call");
                Console.WriteLine("DEBUG: Updating ffmpegId values for macOS cameras using direct FFmpeg call");
                
                // Call FFmpeg directly to get the actual camera list
                string ffmpegCommand = "-f avfoundation -list_devices true -i dummy";
                var ffmpegFramework = new FfmpegMediaPlatform.FfmpegMediaFramework();
                var ffmpegOutput = ffmpegFramework.GetFfmpegText(ffmpegCommand).ToList();
                
                Console.WriteLine($"DEBUG: Raw FFmpeg camera list output:");
                foreach (var line in ffmpegOutput)
                {
                    Console.WriteLine($"DEBUG: {line}");
                }
                
                // Parse the FFmpeg output to extract camera names and their ffmpegIds
                var detectedCameras = ParseFfmpegCameraList(ffmpegOutput);
                
                Console.WriteLine($"DEBUG: Parsed {detectedCameras.Count} cameras from FFmpeg output:");
                foreach (var camera in detectedCameras)
                {
                    Console.WriteLine($"DEBUG: Camera ID {camera.ffmpegId}: {camera.DeviceName}");
                }
                
                // Loop through each detected camera and update the configuration
                bool anyUpdates = false;
                foreach (var detectedCamera in detectedCameras)
                {
                    // Look up the camera configuration using flexible name matching
                    var matchingConfig = FindMatchingConfig(VideoConfigs, detectedCamera.DeviceName);
                    if (matchingConfig != null)
                    {
                        if (matchingConfig.ffmpegId != detectedCamera.ffmpegId)
                        {
                            Logger.VideoLog.Log(this, $"VideoManager: Updated ffmpegId for {matchingConfig.DeviceName}: {matchingConfig.ffmpegId} -> {detectedCamera.ffmpegId}");
                            Console.WriteLine($"DEBUG: Updated ffmpegId for {matchingConfig.DeviceName}: {matchingConfig.ffmpegId} -> {detectedCamera.ffmpegId}");
                            matchingConfig.ffmpegId = detectedCamera.ffmpegId;
                            anyUpdates = true;
                        }
                        else
                        {
                            Console.WriteLine($"DEBUG: Camera {matchingConfig.DeviceName} already has correct ffmpegId: {matchingConfig.ffmpegId}");
                        }
                    }
                    else
                    {
                        Console.WriteLine($"DEBUG: No configuration found for detected camera: {detectedCamera.DeviceName} (ID: {detectedCamera.ffmpegId})");
                    }
                }

                if (anyUpdates)
                {
                    Logger.VideoLog.Log(this, "VideoManager: Saving updated camera configurations with corrected ffmpegId values");
                    Console.WriteLine("DEBUG: Saving updated camera configurations with corrected ffmpegId values");
                    WriteCurrentDeviceConfig();
                }
                else
                {
                    Logger.VideoLog.Log(this, "VideoManager: No updates needed - all cameras already have correct IDs");
                    Console.WriteLine("DEBUG: No updates needed - all cameras already have correct IDs");
                }
            }
            catch (Exception ex)
            {
                Logger.VideoLog.LogException(this, ex);
                Console.WriteLine($"DEBUG: Exception in UpdateFfmpegIdsForMacCameras: {ex.Message}");
            }
        }
        
        private VideoConfig FindMatchingConfig(IEnumerable<VideoConfig> configs, string detectedDeviceName)
        {
            // First try exact match
            var exactMatch = configs.FirstOrDefault(c => c.DeviceName == detectedDeviceName);
            if (exactMatch != null)
            {
                Console.WriteLine($"DEBUG: Found exact match for '{detectedDeviceName}' -> '{exactMatch.DeviceName}'");
                return exactMatch;
            }
            
            // For USB cameras, try flexible matching since FFmpeg reports full VID:PID but config may have short name
            // Example: Config="USB Camera VID", Detected="USB Camera VID:1133 PID:2249"
            foreach (var config in configs)
            {
                // Check if detected name starts with config name (handles VID:PID extensions)
                if (detectedDeviceName.StartsWith(config.DeviceName, StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine($"DEBUG: Found prefix match for '{detectedDeviceName}' -> '{config.DeviceName}'");
                    return config;
                }
                
                // Check if config name starts with detected name (reverse case)
                if (config.DeviceName.StartsWith(detectedDeviceName, StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine($"DEBUG: Found reverse prefix match for '{detectedDeviceName}' -> '{config.DeviceName}'");
                    return config;
                }
                
                // For USB cameras, try matching just the base "USB Camera" part
                if (detectedDeviceName.Contains("USB Camera") && config.DeviceName.Contains("USB Camera"))
                {
                    // Extract the VID part if present in both
                    var detectedVid = ExtractVidFromDeviceName(detectedDeviceName);
                    var configVid = ExtractVidFromDeviceName(config.DeviceName);
                    
                    if (!string.IsNullOrEmpty(detectedVid) && !string.IsNullOrEmpty(configVid))
                    {
                        if (detectedVid.Equals(configVid, StringComparison.OrdinalIgnoreCase))
                        {
                            Console.WriteLine($"DEBUG: Found VID match for '{detectedDeviceName}' -> '{config.DeviceName}' (VID: {detectedVid})");
                            return config;
                        }
                    }
                    else if (string.IsNullOrEmpty(detectedVid) && string.IsNullOrEmpty(configVid))
                    {
                        // Both are generic "USB Camera" - match them
                        Console.WriteLine($"DEBUG: Found generic USB Camera match for '{detectedDeviceName}' -> '{config.DeviceName}'");
                        return config;
                    }
                }
            }
            
            Console.WriteLine($"DEBUG: No matching config found for detected device: '{detectedDeviceName}'");
            return null;
        }
        
        private string ExtractVidFromDeviceName(string deviceName)
        {
            // Extract VID from names like "USB Camera VID:1133 PID:2249" or "USB Camera VID"
            var match = System.Text.RegularExpressions.Regex.Match(deviceName, @"VID(?::(\d+))?", RegexOptions.IgnoreCase);
            if (match.Success && match.Groups.Count > 1 && !string.IsNullOrEmpty(match.Groups[1].Value))
            {
                return match.Groups[1].Value; // Return the VID number
            }
            else if (deviceName.Contains("VID", StringComparison.OrdinalIgnoreCase))
            {
                return "VID"; // Generic VID indicator
            }
            return null;
        }

        private List<VideoConfig> ParseFfmpegCameraList(List<string> ffmpegOutput)
        {
            var cameras = new List<VideoConfig>();
            
            try
            {
                foreach (string line in ffmpegOutput)
                {
                    // Look for lines like: "[0] FaceTime HD Camera" or "[1] USB Camera VID:1133 PID:2249"
                    var match = System.Text.RegularExpressions.Regex.Match(line, @"\[(\d+)\]\s+(.+)");
                    if (match.Success)
                    {
                        string ffmpegId = match.Groups[1].Value;
                        string deviceName = match.Groups[2].Value.Trim();
                        
                        // Create a temporary VideoConfig to store the detected camera info
                        var camera = new VideoConfig
                        {
                            ffmpegId = ffmpegId,
                            DeviceName = deviceName,
                            VideoMode = new Mode { Width = 640, Height = 480, FrameRate = 30 } // Default values
                        };
                        
                        cameras.Add(camera);
                        Console.WriteLine($"DEBUG: Parsed camera: ID={ffmpegId}, Name={deviceName}");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"DEBUG: Exception parsing FFmpeg camera list: {ex.Message}");
            }
            
            return cameras;
        }

        private bool UpdateCameraResolution(VideoConfig videoConfig)
        {
            try
            {
                Logger.VideoLog.Log(this, $"Querying camera capabilities for {videoConfig.DeviceName} (ffmpegId: {videoConfig.ffmpegId})");
                
                // Create a temporary frame source to query camera capabilities
                var frameWork = VideoFrameWorks.Available.FirstOrDefault(f => f.FrameWork == FrameWork.ffmpeg);
                if (frameWork == null)
                {
                    Logger.VideoLog.Log(this, "No ffmpeg framework available for camera capability query");
                    return false;
                }
                
                var tempFrameSource = frameWork.CreateFrameSource(videoConfig);
                if (tempFrameSource == null)
                {
                    Logger.VideoLog.Log(this, $"Failed to create temporary frame source for {videoConfig.DeviceName}");
                    return false;
                }
                
                try
                {
                    // Query available modes
                    var availableModes = tempFrameSource.GetModes().ToList();
                    Logger.VideoLog.Log(this, $"Found {availableModes.Count} available modes for {videoConfig.DeviceName}");
                    
                    foreach (var mode in availableModes)
                    {
                        Logger.VideoLog.Log(this, $"  Mode: {mode.Width}x{mode.Height}@{mode.FrameRate}fps");
                    }
                    
                    if (availableModes.Any())
                    {
                        // Find the best available mode (prefer 1080p, then 720p, then highest resolution)
                        Mode bestMode = null;
                        
                        // First try to find 1080p
                        bestMode = availableModes.FirstOrDefault(m => m.Width == 1920 && m.Height == 1080);
                        
                        // If no 1080p, try 720p
                        if (bestMode == null)
                        {
                            bestMode = availableModes.FirstOrDefault(m => m.Width == 1280 && m.Height == 720);
                        }
                        
                        // If no 720p, take the highest resolution available
                        if (bestMode == null)
                        {
                            bestMode = availableModes.OrderByDescending(m => m.Width * m.Height).First();
                        }
                        
                        if (bestMode != null)
                        {
                            // Check if the current mode is different from the best available mode
                            var currentMode = videoConfig.VideoMode;
                            if (currentMode.Width != bestMode.Width || 
                                currentMode.Height != bestMode.Height || 
                                currentMode.FrameRate != bestMode.FrameRate)
                            {
                                Logger.VideoLog.Log(this, $"Updating resolution for {videoConfig.DeviceName}: {currentMode.Width}x{currentMode.Height}@{currentMode.FrameRate}fps -> {bestMode.Width}x{bestMode.Height}@{bestMode.FrameRate}fps");
                                
                                videoConfig.VideoMode.Width = bestMode.Width;
                                videoConfig.VideoMode.Height = bestMode.Height;
                                videoConfig.VideoMode.FrameRate = bestMode.FrameRate;
                                videoConfig.VideoMode.Format = bestMode.Format;
                                videoConfig.VideoMode.FrameWork = bestMode.FrameWork;
                                
                                return true;
                            }
                            else
                            {
                                Logger.VideoLog.Log(this, $"Resolution for {videoConfig.DeviceName} is already optimal: {bestMode.Width}x{bestMode.Height}@{bestMode.FrameRate}fps");
                            }
                        }
                    }
                    else
                    {
                        Logger.VideoLog.Log(this, $"No available modes found for {videoConfig.DeviceName}");
                    }
                }
                finally
                {
                    // Clean up temporary frame source
                    try
                    {
                        tempFrameSource.Dispose();
                    }
                    catch (Exception ex)
                    {
                        Logger.VideoLog.LogException(this, ex);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.VideoLog.LogException(this, ex);
                Logger.VideoLog.Log(this, $"Failed to update resolution for {videoConfig.DeviceName}");
            }
            
            return false;
        }

        /// <summary>
        /// Updates the ffmpegId for a specific camera when it's changed in the UI
        /// </summary>
        public void UpdateCameraFfmpegId(VideoConfig videoConfig, string newDeviceName)
        {
            try
            {
                Logger.VideoLog.Log(this, $"Updating camera ffmpegId for device change: {videoConfig.DeviceName} -> {newDeviceName}");
                
                // Get all available video sources
                var availableSources = GetAvailableVideoSources().ToList();
                
                // Find the new device
                var newDevice = availableSources.FirstOrDefault(s => s.DeviceName.Equals(newDeviceName, StringComparison.OrdinalIgnoreCase));
                
                if (newDevice != null)
                {
                    string oldFfmpegId = videoConfig.ffmpegId;
                    string oldDeviceName = videoConfig.DeviceName;
                    
                    // Update the device name and ffmpegId
                    videoConfig.DeviceName = newDevice.DeviceName;
                    videoConfig.ffmpegId = newDevice.ffmpegId;
                    
                    Logger.VideoLog.Log(this, $"Updated camera: {oldDeviceName} (ffmpegId: {oldFfmpegId}) -> {videoConfig.DeviceName} (ffmpegId: {videoConfig.ffmpegId})");
                    
                    // Update resolution for the new camera
                    var updatedResolution = UpdateCameraResolution(videoConfig);
                    if (updatedResolution)
                    {
                        Logger.VideoLog.Log(this, $"Updated resolution for {videoConfig.DeviceName} to match new camera capabilities");
                    }
                    
                    // Save the updated configuration
                    WriteCurrentDeviceConfig();
                    
                    // Recreate frame sources to use the new camera
                    CreateFrameSource(VideoConfigs, (newFrameSources) => 
                    {
                        frameSources.Clear();
                        frameSources.AddRange(newFrameSources);
                    });
                }
                else
                {
                    Logger.VideoLog.Log(this, $"Device '{newDeviceName}' not found in available sources");
                }
            }
            catch (Exception ex)
            {
                Logger.VideoLog.LogException(this, ex);
                Logger.VideoLog.Log(this, $"Failed to update camera ffmpegId for {videoConfig.DeviceName}");
            }
        }

        /// <summary>
        /// Gets a list of available camera names for UI selection
        /// </summary>
        public List<string> GetAvailableCameraNames()
        {
            try
            {
                var availableSources = GetAvailableVideoSources().ToList();
                return availableSources.Select(s => s.DeviceName).ToList();
            }
            catch (Exception ex)
            {
                Logger.VideoLog.LogException(this, ex);
                return new List<string>();
            }
        }

        public void StartThread()
        {
            if (videoDeviceManagerThread == null)
            {
                runWorker = true;
                videoDeviceManagerThread = new Thread(WorkerThread);
                videoDeviceManagerThread.Name = "Video Device Manager";
                videoDeviceManagerThread.Start();
            }
        }

        public void WriteCurrentDeviceConfig()
        {
            WriteDeviceConfig(Profile, VideoConfigs);
        }

        public static void WriteDeviceConfig(Profile profile, IEnumerable<VideoConfig> vcs)
        {
            VideoConfig.Write(profile,vcs.ToArray());
        }

        public void Dispose()
        {
            Clear();
            StopDevices();
        }

        private void DoOnWorkerThread(Action a)
        {
            lock (todo)
            {
                todo.Add(a);
            }
            mutex.Set();
        }

        private void DisposeOnWorkerThread(IDisposable disposable)
        {
            lock (needsDispose)
            {
                needsDispose.Add(disposable);
            }
            mutex.Set();
        }

        public void StopDevices()
        {
            Clear();

            runWorker = false;
            mutex.Set();

            if (videoDeviceManagerThread != null)
            {
                if (!videoDeviceManagerThread.Join(30000))
                {
                    try
                    {
#pragma warning disable SYSLIB0006 // Type or member is obsolete
                        videoDeviceManagerThread.Abort();
#pragma warning restore SYSLIB0006 // Type or member is obsolete
                    }
                    catch
                    {
                    }
                }
                videoDeviceManagerThread.Join();
                videoDeviceManagerThread = null;
            }
        }

        public void Clear()
        {
            Logger.VideoLog.LogCall(this);
            lock (frameSources)
            {
                foreach (var source in frameSources)
                {
                    DisposeOnWorkerThread(source);
                }
                frameSources.Clear();
                
                // Clear device tracking
                deviceActiveFrameSources.Clear();
                deviceLastFailTime.Clear();
            }

            mutex.Set();
        }

        public IEnumerable<VideoConfig> GetAvailableVideoSources()
        {
            List<VideoConfig> configs = new List<VideoConfig>();

            foreach (VideoFrameWork videoFramework in VideoFrameWorks.Available)
            {
                foreach (VideoConfig videoConfig in videoFramework.GetVideoConfigs())
                {
                    VideoConfig fromAnotherFramework = GetMatch(configs.Where(r => r.DeviceName == videoConfig.DeviceName), videoConfig.MediaFoundationPath, videoConfig.DirectShowPath);
                    if (fromAnotherFramework != null)
                    {
                        if (fromAnotherFramework.DirectShowPath == null)
                            fromAnotherFramework.DirectShowPath = videoConfig.DirectShowPath;

                        if (fromAnotherFramework.MediaFoundationPath == null)
                            fromAnotherFramework.MediaFoundationPath = videoConfig.MediaFoundationPath;
                    }
                    else
                    {
                        configs.Add(videoConfig);
                    }
                }
            }

            // Set any usbports
            foreach (VideoConfig vc in configs)
            {
                if (configs.Where(other => other.DeviceName == vc.DeviceName).Count() > 1)
                {
                    vc.AnyUSBPort = false;
                }
                else
                {
                    vc.AnyUSBPort = true;
                }
            }

            return configs;
        }

        public IEnumerable<string> GetAvailableAudioSources()
        {
            foreach (VideoFrameWork videoFramework in VideoFrameWorks.Available)
            {
                foreach (string audioSource in videoFramework.GetAudioSources())
                {
                    yield return audioSource;
                }
            }
        }

        private VideoConfig GetMatch(IEnumerable<VideoConfig> videoConfigs, params string[] paths)
        {
            if (paths.Any())
            {
                Regex regex = new Regex("(#[A-z0-9_&#]*)");

                foreach (string path in paths)
                {
                    if (string.IsNullOrEmpty(path))
                        continue;

                    Match match = regex.Match(path);
                    if (match.Success)
                    {
                        string common = match.Groups[1].Value;
                        return videoConfigs.Where(v => v.PathContains(common)).FirstOrDefault();
                    }
                }
            }
            return null;
        }

        public bool GetStatus(VideoConfig videoConfig, out bool connected, out bool recording, out int height)
        {
            connected = false;
            recording = false;
            height = 0;

            FrameSource frameSource = GetFrameSource(videoConfig);
            if (frameSource != null)
            {
                connected = frameSource.Connected;
                recording = frameSource.Recording;
                height = frameSource.FrameHeight;
                return true;
            }

            return false;
        }

        //public IEnumerable<VideoConfig> GetUnavailableVideoSources()
        //{
        //    foreach (DsDevice ds in DirectShowHelper.VideoCaptureDevices)
        //    {
        //        VideoConfig videoConfig = new VideoConfig() { DeviceName = ds.Name, DirectShowPath = ds.DevicePath };
        //        ds.Dispose();

        //        if (!ValidDevice(videoConfig))
        //        {
        //            yield return videoConfig;
        //        }
        //    }
        //}

        public bool ValidDevice(VideoConfig vc)
        {
            string[] whitelist = new string[]
            {
                 // OBS Virtual Camera (the old plugin one)
                "@device:sw:{860BB310-5D01-11D0-BD3B-00A0C911CE86}\\{27B05C2D-93DC-474A-A5DA-9BBA34CB2A9C}",
                "@device:sw:{860BB310-5D01-11D0-BD3B-00A0C911CE86}\\{27B05C2D-93DC-474A-A5DA-9BBA34CB2A9D}",
                "@device:sw:{860BB310-5D01-11D0-BD3B-00A0C911CE86}\\{27B05C2D-93DC-474A-A5DA-9BBA34CB2A9E}",
                "@device:sw:{860BB310-5D01-11D0-BD3B-00A0C911CE86}\\{27B05C2D-93DC-474A-A5DA-9BBA34CB2A9F}"
            };

            string[] blacklist = new string[]
            {
            };

            if (whitelist.Contains(vc.DirectShowPath))
            {
                return true;
            }

            if (blacklist.Contains(vc.DirectShowPath))
            {
                return false;
            }

            return true;
        }

        public IEnumerable<FrameSource> GetFrameSources()
        {
            foreach (VideoConfig vs in VideoConfigs)
            {
                FrameSource source = GetFrameSource(vs);
                if (source != null)
                {
                    yield return source;
                }
            }
        }

        public FrameSource GetFrameSource(VideoConfig vs)
        {
            lock (frameSources)
            {
                return frameSources.FirstOrDefault(dsss => dsss.VideoConfig == vs);
            }
        }

        private FrameSource CreateFrameSource(VideoConfig videoConfig)
        {
            if (videoConfig.DeviceName == "Static Image")
            {
                return new StaticFrameSource(videoConfig);
            }

            FrameSource source = null;
            string deviceKey = GetDeviceKey(videoConfig);
            
            Logger.VideoLog.Log(this, $"CreateFrameSource called for {videoConfig.DeviceName}, deviceKey: {deviceKey}");
            
            lock (frameSources)
            {
                Logger.VideoLog.Log(this, $"Current frameSources count: {frameSources.Count}, deviceActiveFrameSources count: {deviceActiveFrameSources.Count}");
                
                // Check if we already have a frame source for this device to prevent duplicates
                var existingSource = frameSources.FirstOrDefault(fs => fs.VideoConfig.Equals(videoConfig));
                if (existingSource != null)
                {
                    Logger.VideoLog.Log(this, $"Frame source already exists for {videoConfig.DeviceName}, returning existing source");
                    return existingSource;
                }
                
                // Check if another frame source is actively using this device
                if (deviceActiveFrameSources.ContainsKey(deviceKey))
                {
                    var activeSource = deviceActiveFrameSources[deviceKey];
                    Logger.VideoLog.Log(this, $"Found active source for deviceKey {deviceKey}, connected: {activeSource?.Connected}, in frameSources: {frameSources.Contains(activeSource)}");
                    if (frameSources.Contains(activeSource) && activeSource.Connected)
                    {
                        Logger.VideoLog.Log(this, $"Device {videoConfig.DeviceName} is already in use by another frame source, skipping creation");
                        return null;
                    }
                    else
                    {
                        // Remove stale reference
                        Logger.VideoLog.Log(this, $"Removing stale reference for deviceKey {deviceKey}");
                        deviceActiveFrameSources.Remove(deviceKey);
                    }
                }
                
                // Check cooldown period for failed devices
                if (deviceLastFailTime.ContainsKey(deviceKey))
                {
                    var timeSinceFailure = DateTime.Now - deviceLastFailTime[deviceKey];
                    Logger.VideoLog.Log(this, $"Device {videoConfig.DeviceName} last failed {timeSinceFailure.TotalSeconds:F1}s ago");
                    if (timeSinceFailure < deviceRetryDelay)
                    {
                        var remainingTime = deviceRetryDelay - timeSinceFailure;
                        Logger.VideoLog.Log(this, $"Device {videoConfig.DeviceName} is in cooldown period, {remainingTime.TotalSeconds:F0}s remaining");
                        return null;
                    }
                    else
                    {
                        // Cooldown period has expired, remove the entry
                        Logger.VideoLog.Log(this, $"Cooldown period expired for {videoConfig.DeviceName}");
                        deviceLastFailTime.Remove(deviceKey);
                    }
                }
                
                Logger.VideoLog.Log(this, $"Device conflict checks passed for {videoConfig.DeviceName}, proceeding with creation");

                if (videoConfig.FilePath != null)
                {
                    try
                    {
                        if (videoConfig.FilePath.EndsWith("jpg") || videoConfig.FilePath.EndsWith("png"))
                        {
                            source = new StaticFrameSource(videoConfig);
                        }
                        else
                        {
                            VideoFrameWork mediaFoundation = VideoFrameWorks.GetFramework(FrameWork.MediaFoundation);
                            if (mediaFoundation != null)
                            {
                                source = mediaFoundation.CreateFrameSource(videoConfig);
                            }
                        }

                        if (source == null)
                        {
                            throw new Exception("Invalid video/image format");
                        }
                    }
                    catch (Exception e)
                    {
                        // Failed to read the file
                        Logger.VideoLog.LogException(this, e);
                        return null;
                    }
                }
                else
                {
                    try
                    {
                        foreach (VideoFrameWork frameWork in VideoFrameWorks.Available)
                        {
                            if (videoConfig.FrameWork == frameWork.FrameWork)
                            {
                                Logger.VideoLog.Log(this, $"Attempting to create frame source for {videoConfig.DeviceName} using {frameWork.FrameWork}");
                                source = frameWork.CreateFrameSource(videoConfig);
                                break; // Exit loop once we find the matching framework
                            }
                        }
                        
                        if (source == null)
                        {
                            Logger.VideoLog.Log(this, $"No suitable framework found for {videoConfig.DeviceName} with framework {videoConfig.FrameWork}");
                        }
                    }
                    catch (System.Runtime.InteropServices.COMException e)
                    {
                        // Failed to load the camera..
                        Logger.VideoLog.LogException(this, e);
                        return null;
                    }
                    catch (Exception e)
                    {
                        // Generic exception handling for other camera creation failures
                        Logger.VideoLog.LogException(this, e);
                        return null;
                    }
                }

                if (source != null)
                {
                    frameSources.Add(source);
                    deviceActiveFrameSources[deviceKey] = source; // Track active device usage
                    Logger.VideoLog.Log(this, $"Successfully created frame source for {videoConfig.DeviceName}");
                }
                else
                {
                    deviceLastFailTime[deviceKey] = DateTime.Now; // Record failure time for cooldown
                    Logger.VideoLog.Log(this, $"Failed to create frame source for {videoConfig.DeviceName}, applying cooldown");
                }
            }

            if (source != null)
            {
                Initialize(source);
            }

            return source;
        }

        public void RemoveFrameSource(VideoConfig videoConfig)
        {
            string deviceKey = GetDeviceKey(videoConfig);
            
            lock (frameSources)
            {
                FrameSource[] toRemove = frameSources.Where(fs => fs.VideoConfig == videoConfig).ToArray();

                foreach (FrameSource source in toRemove)
                {
                    DisposeOnWorkerThread(source);
                    frameSources.Remove(source);
                    
                    // Clean up device tracking
                    if (deviceActiveFrameSources.ContainsKey(deviceKey) && deviceActiveFrameSources[deviceKey] == source)
                    {
                        deviceActiveFrameSources.Remove(deviceKey);
                    }
                }
            }
        }
        
        private string GetDeviceKey(VideoConfig videoConfig)
        {
            // Create a unique key for device tracking that combines device name and path
            string key;
            if (!string.IsNullOrEmpty(videoConfig.FilePath))
            {
                key = $"file:{videoConfig.FilePath}";
            }
            else if (!string.IsNullOrEmpty(videoConfig.ffmpegId))
            {
                key = $"ffmpeg:{videoConfig.ffmpegId}";
            }
            else if (!string.IsNullOrEmpty(videoConfig.DirectShowPath))
            {
                key = $"dshow:{videoConfig.DirectShowPath}";
            }
            else
            {
                key = $"device:{videoConfig.DeviceName}";
            }
            
            Logger.VideoLog.Log(this, $"GetDeviceKey for {videoConfig.DeviceName}: ffmpegId='{videoConfig.ffmpegId}', DirectShowPath='{videoConfig.DirectShowPath}', FilePath='{videoConfig.FilePath}' -> key='{key}'");
            return key;
        }

        public bool HasReplay(Race currentRace)
        {
            if (currentRace != null)
            {
                return GetRecordings(currentRace).Any();
            }
            return false;
        }

        public IEnumerable<ChannelVideoInfo> CreateChannelVideoInfos()
        {
            return CreateChannelVideoInfos(VideoConfigs);
        }

        public IEnumerable<ChannelVideoInfo> CreateChannelVideoInfos(IEnumerable<VideoConfig> videoSources)
        {
            List<ChannelVideoInfo> channelVideoInfos = new List<ChannelVideoInfo>();
            foreach (VideoConfig videoConfig in videoSources)
            {
                foreach (VideoBounds videoBounds in videoConfig.VideoBounds)
                {
                    FrameSource source = null;
                    try
                    {
                        source = GetFrameSource(videoConfig);
                    }
                    catch (System.Runtime.InteropServices.COMException e)
                    {
                        // Failed to load the camera..
                        Logger.VideoLog.LogException(this, e);
                    }
                    if (source != null)
                    {
                        Channel channel = videoBounds.GetChannel();
                        if (channel == null)
                        {
                            channel = Channel.None;
                        }

                        ChannelVideoInfo cvi = new ChannelVideoInfo(videoBounds, channel, source);
                        channelVideoInfos.Add(cvi);
                    }
                }
            }

            return channelVideoInfos;
        }


        public void StartRecording(Race race)
        {
            this.race = race;
            lock (recording)
            {
                foreach (ICaptureFrameSource source in frameSources.OfType<ICaptureFrameSource>().Where(r => r.VideoConfig.RecordVideoForReplays))
                {
                    // if all feeds on this source are FPV, only record if they're visible..
                    if (source.VideoConfig.VideoBounds.All(r => r.SourceType == SourceTypes.FPVFeed))
                    {
                        if (source.IsVisible)
                        {
                            recording.Add(source);
                        }
                    }
                    else
                    {
                        // record
                        recording.Add(source);
                    }
                }
            }
            mutex.Set();
        }

        public Mode PickMode(VideoConfig videoConfig, IEnumerable<Mode> modes)
        {
            VideoFrameWork videoFrameWork = GetFramework(videoConfig.FrameWork);
            if (videoFrameWork != null)
            {
                return videoFrameWork.PickMode(modes);
            }
            return modes.FirstOrDefault();
        }

        public void StopRecording()
        {
            lock (recording)
            {
                recording.Clear();
            }
            mutex.Set();
        }

        private string GetRecordingFilename(Race race, FrameSource source)
        {
            int index = frameSources.IndexOf(source);
            return Path.Combine(EventDirectory.FullName, race.ID.ToString(), index.ToString());
        }

        public void LoadRecordings(Race race, FrameSourcesDelegate frameSourcesDelegate)
        {
            MaintainConnections = false;

            VideoConfigs.Clear();
            VideoConfigs.AddRange(GetRecordings(race));
            CreateFrameSource(VideoConfigs, frameSourcesDelegate);
            StartThread();
        }

        public IEnumerable<VideoConfig> GetRecordings(Race race)
        {
            DirectoryInfo raceDirectory = new DirectoryInfo(Path.Combine(EventDirectory.FullName, race.ID.ToString()));
            if (raceDirectory.Exists)
            {
                foreach (FileInfo file in raceDirectory.GetFiles("*.recordinfo.xml"))
                {
                    RecodingInfo videoInfo = null;

                    try
                    {
                        videoInfo = IOTools.ReadSingle<RecodingInfo>(raceDirectory.FullName, file.Name);
                    }
                    catch (Exception ex)
                    {
                        Logger.VideoLog.LogException(this, ex);
                    }

                    if (videoInfo != null)
                    {
                        if (File.Exists(videoInfo.FilePath))
                        {
                            yield return videoInfo.GetVideoConfig();
                        }
                    }
                }
            }
        }

        public void CreateFrameSource(IEnumerable<VideoConfig> videoConfigs, FrameSourcesDelegate frameSourcesDelegate)
        {
            Logger.VideoLog.Log(this, $"CreateFrameSource(IEnumerable) called with {videoConfigs.Count()} configs");
            
            // Smart config comparison - only clear/recreate if configs have actually changed
            bool configsChanged = false;
            var newConfigs = videoConfigs.ToList();
            var currentConfigs = VideoConfigs.ToList();
            
            if (newConfigs.Count != currentConfigs.Count)
            {
                configsChanged = true;
                Logger.VideoLog.Log(this, $"Config count changed: {currentConfigs.Count} -> {newConfigs.Count}");
            }
            else
            {
                // Compare individual configs
                for (int i = 0; i < newConfigs.Count; i++)
                {
                    var newConfig = newConfigs[i];
                    var currentConfig = currentConfigs[i];
                    
                    if (!ConfigsEqual(newConfig, currentConfig))
                    {
                        configsChanged = true;
                        Logger.VideoLog.Log(this, $"Config changed for device: {newConfig.DeviceName}");
                        break;
                    }
                }
            }
            
            if (!configsChanged)
            {
                Logger.VideoLog.Log(this, "Video configurations unchanged, checking existing frame sources");
                
                // Configs haven't changed - check if existing frame sources are still functional
                List<FrameSource> functionalSources = new List<FrameSource>();
                bool needRecreation = false;
                
                lock (frameSources)
                {
                    foreach (var videoConfig in videoConfigs)
                    {
                        var existingSource = frameSources.FirstOrDefault(fs => fs.VideoConfig.Equals(videoConfig));
                        if (existingSource != null && existingSource.Connected)
                        {
                            Logger.VideoLog.Log(this, $"Reusing existing connected frame source for {videoConfig.DeviceName}");
                            functionalSources.Add(existingSource);
                        }
                        else
                        {
                            if (existingSource != null)
                            {
                                Logger.VideoLog.Log(this, $"Existing frame source for {videoConfig.DeviceName} is disconnected, will recreate");
                            }
                            else
                            {
                                Logger.VideoLog.Log(this, $"No existing frame source found for {videoConfig.DeviceName}, will create");
                            }
                            needRecreation = true;
                        }
                    }
                }
                
                // If all existing sources are functional, use them
                if (!needRecreation)
                {
                    Logger.VideoLog.Log(this, "All existing frame sources are functional, reusing them");
                    DoOnWorkerThread(() =>
                    {
                        if (frameSourcesDelegate != null)
                        {
                            frameSourcesDelegate(functionalSources);
                        }
                    });
                    return;
                }
                else
                {
                    Logger.VideoLog.Log(this, "Some frame sources need recreation, proceeding with full recreation");
                }
            }
            
            Logger.VideoLog.Log(this, "Video configurations have changed, recreating frame sources");
            
            // Only clear if we actually need to recreate sources
            List<FrameSource> list = new List<FrameSource>();
            foreach (var videoConfig in videoConfigs)
            {
                Logger.VideoLog.Log(this, $"Processing VideoConfig: {videoConfig.DeviceName}");
                
                // Check if we can reuse an existing frame source for this exact config
                FrameSource existingSource = null;
                lock (frameSources)
                {
                    existingSource = frameSources.FirstOrDefault(fs => ConfigsEqual(fs.VideoConfig, videoConfig));
                }
                
                if (existingSource != null && existingSource.Connected)
                {
                    Logger.VideoLog.Log(this, $"Reusing existing connected frame source for {videoConfig.DeviceName}");
                    list.Add(existingSource);
                }
                else
                {
                    // Remove old frame source for this device if it exists
                    RemoveFrameSource(videoConfig);
                    
                    FrameSource fs = CreateFrameSource(videoConfig);
                    if (fs != null)
                    {
                        list.Add(fs);
                    }
                    else
                    {
                        Logger.VideoLog.Log(this, $"Frame source creation returned null for {videoConfig.DeviceName} (likely due to device conflict or cooldown)");
                    }
                }
            }
            
            // Remove any frame sources that are no longer in the new config
            lock (frameSources)
            {
                var sourcesToRemove = frameSources.Where(fs => !videoConfigs.Any(vc => ConfigsEqual(fs.VideoConfig, vc))).ToArray();
                foreach (var sourceToRemove in sourcesToRemove)
                {
                    Logger.VideoLog.Log(this, $"Removing frame source no longer in config: {sourceToRemove.VideoConfig.DeviceName}");
                    DisposeOnWorkerThread(sourceToRemove);
                    frameSources.Remove(sourceToRemove);
                    
                    string deviceKey = GetDeviceKey(sourceToRemove.VideoConfig);
                    if (deviceActiveFrameSources.ContainsKey(deviceKey) && deviceActiveFrameSources[deviceKey] == sourceToRemove)
                    {
                        deviceActiveFrameSources.Remove(deviceKey);
                    }
                }
            }

            DoOnWorkerThread(() =>
            {
                if (frameSourcesDelegate != null)
                {
                    frameSourcesDelegate(list);
                }
            });
        }
        
        // Helper method to compare video configs for equality
        private bool ConfigsEqual(VideoConfig config1, VideoConfig config2)
        {
            if (config1 == null && config2 == null) return true;
            if (config1 == null || config2 == null) return false;
            
            return config1.DeviceName == config2.DeviceName &&
                   config1.ffmpegId == config2.ffmpegId &&
                   config1.DirectShowPath == config2.DirectShowPath &&
                   config1.FilePath == config2.FilePath &&
                   config1.FrameWork == config2.FrameWork &&
                   config1.RecordVideoForReplays == config2.RecordVideoForReplays &&
                   config1.RecordResolution == config2.RecordResolution &&
                   config1.RecordFrameRate == config2.RecordFrameRate &&
                   VideoBoundsEqual(config1.VideoBounds, config2.VideoBounds);
        }
        
        private bool VideoBoundsEqual(IEnumerable<VideoBounds> bounds1, IEnumerable<VideoBounds> bounds2)
        {
            if (bounds1 == null && bounds2 == null) return true;
            if (bounds1 == null || bounds2 == null) return false;
            
            var list1 = bounds1.ToList();
            var list2 = bounds2.ToList();
            
            if (list1.Count != list2.Count) return false;
            
            for (int i = 0; i < list1.Count; i++)
            {
                var vb1 = list1[i];
                var vb2 = list2[i];
                
                if (vb1.SourceType != vb2.SourceType ||
                    vb1.Channel != vb2.Channel ||
                    vb1.ShowInGrid != vb2.ShowInGrid ||
                    vb1.Crop != vb2.Crop ||
                    vb1.OverlayText != vb2.OverlayText ||
                    vb1.OverlayAlignment != vb2.OverlayAlignment ||
                    Math.Abs(vb1.RelativeSourceBounds.X - vb2.RelativeSourceBounds.X) > 0.001f ||
                    Math.Abs(vb1.RelativeSourceBounds.Y - vb2.RelativeSourceBounds.Y) > 0.001f ||
                    Math.Abs(vb1.RelativeSourceBounds.Width - vb2.RelativeSourceBounds.Width) > 0.001f ||
                    Math.Abs(vb1.RelativeSourceBounds.Height - vb2.RelativeSourceBounds.Height) > 0.001f)
                {
                    return false;
                }
            }
            
            return true;
        }

        public void Initialize(FrameSource frameSource)
        {
            lock (needsInitialize)
            {
                if (!needsInitialize.Contains(frameSource))
                {
                    needsInitialize.Add(frameSource);
                }
            }

            mutex.Set();
        }

        public void CheckFileCount()
        {
            try
            {
                int maxCount = ApplicationProfileSettings.Instance.VideosToKeep;
                EventDirectory.Refresh();

                FileInfo[] files = AllEventAllVideoFiles().ToArray();

                int toDelete = files.Count() - maxCount;

                if (toDelete > 0)
                {
                    IEnumerable<FileInfo> delete = files.OrderBy(r => r.LastWriteTime).Take(toDelete);
                    foreach (FileInfo file in delete)
                    {
                        file.Delete();

                        FileInfo xmlconfig = new FileInfo(file.FullName.Replace(".wmv", ".recordinfo.xml"));
                        if (xmlconfig.Exists)
                        {
                            xmlconfig.Delete();
                        }
                    }
                }
            }
            catch
            {
            }
        }

        private IEnumerable<FileInfo> AllEventAllVideoFiles()
        {
            if (EventDirectory == null)
                yield break;

            DirectoryInfo parentDirectory = EventDirectory.Parent;

            foreach (DirectoryInfo eventDir in parentDirectory.EnumerateDirectories())
            {
                foreach (DirectoryInfo raceDir in eventDir.EnumerateDirectories())
                {
                    foreach (FileInfo fileInfo in raceDir.GetFiles("*.wmv"))
                    {
                        yield return fileInfo;
                    }

                    foreach (FileInfo fileInfo in raceDir.GetFiles("*.mp4"))
                    {
                        yield return fileInfo;
                    }
                }
            }
        }

        public void UpdateAutoPause()
        {
            if (!AutoPause)
                return;

            lock (frameSources)
            {
                foreach (FrameSource frameSource in frameSources)
                {
                    //apply the previous frames visiblity
                    if (frameSource.IsVisible != frameSource.DrawnThisGraphicsFrame)
                    {
                        frameSource.IsVisible = frameSource.DrawnThisGraphicsFrame;
                        mutex.Set();
                    }

                    // Clears the draw flag
                    frameSource.DrawnThisGraphicsFrame = false;
                }
            }
        }

        private void WorkerThread()
        {
            bool someFinalising = false;

            List<FrameSource> needsVideoInfoWrite = new List<FrameSource>();
            while (runWorker)
            {
                try
                {
                    // Wait for a set on the mutex or just every X ms
                    if (!mutex.WaitOne(someFinalising ? 500 : 4000))
                    {
                        
                    }

                    if (!runWorker)
                        break;

                    if (someFinalising)
                    {
                        if (!Finalising)
                        {
                            someFinalising = false;
                            OnFinishedFinalizing();
                        }
                    }

                    bool doCountClean = false;
                    lock (recording)
                    {
                        try
                        {
                            foreach (ICaptureFrameSource source in recording)
                            {
                                if (!source.Recording)
                                {
                                    FrameSource frameSource = source as FrameSource;
                                    if (frameSource != null)
                                    {
                                        if (frameSource.State == FrameSource.States.Paused)
                                        {
                                            frameSource.Unpause();
                                        }
                                    }
                                    
                                    string filename = GetRecordingFilename(race, (FrameSource)source);
                                    source.StartRecording(filename);
                                    doCountClean = true;

                                    needsVideoInfoWrite.Add((FrameSource)source);
                                }

                                source.RecordNextFrameTime = true;
                            }

                            if (!recording.Any())
                            {
                                ICaptureFrameSource[] stopRecording;

                                lock (frameSources)
                                {
                                    stopRecording = frameSources.OfType<ICaptureFrameSource>().Where(r => !r.ManualRecording).ToArray();
                                }

                                foreach (ICaptureFrameSource source in stopRecording)
                                {
                                    if (source.Recording)
                                    {
                                        source.StopRecording();
                                        someFinalising = true;
                                    }

                                    needsVideoInfoWrite.Add((FrameSource)source);
                                }
                            }
                        }
                        catch (Exception e)
                        {
                            Logger.VideoLog.LogException(this, e);
                        }
                    }

                    if (needsVideoInfoWrite.Any())
                    {
                        ICaptureFrameSource[] sources = needsVideoInfoWrite.OfType<ICaptureFrameSource>().Where(r => r.VideoConfig.RecordVideoForReplays).ToArray();
                        foreach (ICaptureFrameSource source in sources)
                        {
                            try
                            {
                                if (source.FrameTimes != null && source.FrameTimes.Any())
                                {
                                    RecodingInfo vi = new RecodingInfo(source);

                                    FileInfo fileinfo = new FileInfo(vi.FilePath.Replace(".wmv", "") + ".recordinfo.xml");
                                    IOTools.Write(fileinfo.Directory.FullName, fileinfo.Name, vi);
                                    needsVideoInfoWrite.Remove((FrameSource)source);
                                }
                            }
                            catch (Exception e)
                            {
                                Logger.VideoLog.LogException(this, e);
                            }
                        }
                    }

                    FrameSource[] initalizeNow = new FrameSource[0];
                    
                    lock (needsInitialize)
                    {
                        lock (frameSources)
                        {
                            if (MaintainConnections)
                            {
                                // Only add disconnected frame sources that aren't in cooldown
                                var disconnectedSources = frameSources.Where(r => !r.Connected);
                                foreach (var source in disconnectedSources)
                                {
                                    string deviceKey = GetDeviceKey(source.VideoConfig);
                                    
                                    // Check if device is in cooldown period
                                    if (deviceLastFailTime.ContainsKey(deviceKey))
                                    {
                                        var timeSinceFailure = DateTime.Now - deviceLastFailTime[deviceKey];
                                        if (timeSinceFailure < deviceRetryDelay)
                                        {
                                            // Still in cooldown, skip this source
                                            continue;
                                        }
                                        else
                                        {
                                            // Cooldown expired, remove the entry
                                            deviceLastFailTime.Remove(deviceKey);
                                        }
                                    }
                                    
                                    needsInitialize.Add(source);
                                }
                            }
                        }

                        initalizeNow = needsInitialize.Distinct().ToArray();
                        needsInitialize.Clear();
                    }

                    foreach (FrameSource frameSource in initalizeNow)
                    {
                        try
                        {
                            if (frameSource.State != FrameSource.States.Stopped)
                            {
                                frameSource.Stop();
                            }

                            frameSource.CleanUp();

                            bool result = false;
                            bool shouldStart = false;
                            lock (frameSources)
                            {
                                shouldStart = frameSources.Contains(frameSource);
                            }

                            if (shouldStart)
                            {
                                result = frameSource.Start();
                                
                                if (!result)
                                {
                                    // Record failure time for cooldown
                                    string deviceKey = GetDeviceKey(frameSource.VideoConfig);
                                    deviceLastFailTime[deviceKey] = DateTime.Now;
                                    Logger.VideoLog.Log(this, $"Frame source start failed for {frameSource.VideoConfig.DeviceName}, applying cooldown");
                                }
                            }

                            if (result)
                            {
                                OnStart?.Invoke(frameSource);
                            }
                        }
                        catch (Exception e)
                        {
                            Logger.VideoLog.LogException(this, e);
                        }
                    }

                    if (AutoPause && !initalizeNow.Any())
                    {
                        FrameSource[] toPause;
                        FrameSource[] toResume;
                        lock (frameSources)
                        {
                            toPause = frameSources.Where(r => !r.IsVisible && r.State == FrameSource.States.Running && r.VideoConfig.Pauseable && !r.Recording).ToArray();
                            toResume = frameSources.Where(r => r.IsVisible && r.State == FrameSource.States.Paused).ToArray();
                        }

                        foreach (FrameSource fs in toPause)
                        {
                            fs.Pause();
                        }

                        foreach (FrameSource fs in toResume)
                        {
                            fs.Unpause();
                        }
                    }

                    if (doCountClean)
                    {
                        CheckFileCount();
                    }

                    // Run any clean up tasks..
                    WorkerThreadCleanupTasks();

                }
                catch (Exception e)
                {
                    Logger.VideoLog.LogException(this, e);
                }
            }

            try
            {
                // Remove any zombies sitting around..
                Clear();

                // Run the clean up tasks one last time...
                WorkerThreadCleanupTasks();
            }
            catch (Exception e)
            {
                Logger.VideoLog.LogException(this, e);
            }
        }

        private void WorkerThreadCleanupTasks()
        {
            lock (needsDispose)
            {
                // We want to do the join in parallel, so stop all teh worker threads in these first.
                foreach (TextureFrameSource textureFrameSource in needsDispose.OfType<TextureFrameSource>())
                {
                    textureFrameSource.StopProcessing();
                }

                foreach (IDisposable fs in needsDispose)
                {
                    fs.Dispose();
                }
                needsDispose.Clear();
            }

            lock (todo)
            {
                try
                {
                    foreach (var action in todo)
                    {
                        action();
                    }
                    todo.Clear();
                }
                catch (Exception e)
                {
                    Logger.VideoLog.LogException(this, e);
                }
            }
        }

        public void GetModes(VideoConfig vs, bool forceAll, Action<ModesResult> callback)
        {
            DoOnWorkerThread(() =>
            {
                ModesResult result = new ModesResult();
                result.Modes = new Mode[0];
                result.RebootRequired = false;

                try
                {
                    List<Mode> modes = new List<Mode>();
                    IHasModes frameSource = GetFrameSource(vs) as IHasModes;
                    if (frameSource != null && !forceAll)
                    {
                        modes.AddRange(frameSource.GetModes());
                    }
                    else
                    {
                        // Clear the video mode so it's not a problem getting new modes if the current one doesnt work?
                        VideoConfig clone = vs.Clone();
                        clone.VideoMode = new Mode();

                        foreach (VideoFrameWork frameWork in VideoFrameWorks.Available)
                        {
                            if (clone.FrameWork == frameWork.FrameWork)
                            {
                                // Create a temporary instance just to get the modes...
                                using (FrameSource source = frameWork.CreateFrameSource(clone))
                                {
                                    modes.AddRange(source.GetModes());
                                    if (source.RebootRequired)
                                    {
                                        result.RebootRequired = true;
                                    }
                                }
                            }
                        }
                    }

                    result.Modes = modes.Distinct().ToArray();
                    callback(result);
                }
                catch
                {
                    callback(result);
                }
            });
        }

        public struct ModesResult
        {
            public Mode[] Modes { get; set; }
            public bool RebootRequired { get; set; }
        }
    }

    public static class VideoManagerFactory
    {
        private static string directory;
        private static Profile profile;

        public static void Init(string di, Profile p)
        {
            directory = di;
            profile = p;
        }

        public static VideoManager CreateVideoManager()
        {
            return new VideoManager(directory, profile);
        }
    }
}
