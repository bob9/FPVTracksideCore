using Composition;
using Composition.Input;
using Composition.Layers;
using Composition.Nodes;
using ImageServer;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using RaceLib;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Tools;
using UI.Nodes;

namespace UI.Video
{
    public class VideoSourceEditor : ObjectEditorNode<VideoConfig>
    {
        public VideoManager VideoManager { get; private set; }
        public EventManager EventManager { get; private set; }

        protected Node preview;

        public override bool GroupItems { get { return true; } }

        private ChannelVideoMapperNode mapperNode;
        private object locker;

        private Node physicalLayoutContainer;
        private Node physicalLayout;
        
        // Guard to prevent multiple simultaneous RepairVideoPreview calls
        private bool isRepairingVideoPreview = false;

        public Profile Profile { get; private set; }    

        public static VideoSourceEditor GetVideoSourceEditor(EventManager em, Profile profile)
        {
            VideoManager videoManager = new VideoManager(ApplicationProfileSettings.Instance.EventStorageLocation, profile);

            videoManager.LoadDevices();
            videoManager.MaintainConnections = true;
            videoManager.AutoPause = false;

            return new VideoSourceEditor(videoManager, em, profile);
        }

        private VideoSourceEditor(VideoManager videoManager, EventManager em, Profile profile)
        {
            locker = new object();
            Profile = profile;

            VideoManager = videoManager;
            VideoManager.OnStart += VideoManager_OnStart;
            EventManager = em;

            heading.Text = "Video Input Settings";
            cancelButton.Visible = true;
            trackChanges = true;
            CanReOrder = false;

            // Only show configured cameras in the left panel
            Console.WriteLine("DEBUG: Loading configured cameras for left panel");
            Console.WriteLine($"DEBUG: Found {videoManager.VideoConfigs.Count} configured cameras");
            
            // Set objects to only the configured cameras (saved configurations)
            SetObjects(videoManager.VideoConfigs, true);

            InitMapperNode(Selected);

            RelativeBounds = new RectangleF(0, 0, 1, 0.97f);
            Scale(0.6f, 1.0f);
        }

        private void VideoManager_OnStart(FrameSource obj)
        {
            if (Selected == obj.VideoConfig)
            {
                InitMapperNode(obj.VideoConfig);
            }
        }

        public override void Dispose()
        {
            if (VideoManager != null)
            {
                VideoManager.Dispose();
                VideoManager = null;
            }

            base.Dispose();
        }

        protected override void AddOnClick(MouseInputEvent mie)
        {
            MouseMenu mouseMenu = new MouseMenu(this);
            mouseMenu.TopToBottom = false;

            // Note: We removed the LoadDevices() call here as it was causing constant frame source recreation
            // Instead, we rely on periodic updates and the enhanced equality checking below

            VideoConfig[] vcs = VideoManager.GetAvailableVideoSources().OrderBy(vc => vc.DeviceName).ToArray();
            Console.WriteLine($"DEBUG: Add button clicked - found {vcs.Length} total available cameras");
            
            foreach (VideoConfig source in vcs)
            {
                // For USB cameras, check by DeviceName rather than full Equals() to handle ffmpegId changes
                bool alreadyConfigured = Objects.Any(r => 
                    r.Equals(source) || 
                    (r.DeviceName.Equals(source.DeviceName, StringComparison.OrdinalIgnoreCase)));
                
                Console.WriteLine($"DEBUG: Checking camera: {source.DeviceName} (ffmpegId: {source.ffmpegId}) - alreadyConfigured: {alreadyConfigured}");
                if (alreadyConfigured)
                {
                    Console.WriteLine($"DEBUG: Camera already configured, not showing in Add menu: {source.DeviceName}");
                    Console.WriteLine($"DEBUG: Configured cameras: {string.Join(", ", Objects.Select(o => $"{o.DeviceName}(id:{o.ffmpegId})"))}");
                }
                    
                if (!alreadyConfigured)
                {
                    string sourceAsString = source.ToString();
                    if (!string.IsNullOrWhiteSpace(sourceAsString))
                    {
                        if (VideoManager.ValidDevice(source))
                        {
                            Console.WriteLine($"DEBUG: Adding camera to Add menu: {sourceAsString}");
                            mouseMenu.AddItem(sourceAsString, () => { AddNew(source); });
                        }
                        else
                        {
                            Console.WriteLine($"DEBUG: Camera not valid for Add menu: {sourceAsString}");
                            mouseMenu.AddDisabledItem(sourceAsString);
                        }
                    }
                }
            }

            mouseMenu.AddItem("File", AddVideoFile);
            //mouseMenu.AddItem("RTSP URL", AddURL);
            
            // Add camera permission request for macOS
            if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.OSX))
            {
                mouseMenu.AddBlank();
                mouseMenu.AddItem("Request Camera Permission", RequestCameraPermission);
            }
            
            mouseMenu.Show(addButton);
        }

        private void AddVideoFile()
        {
            string filename = PlatformTools.OpenFileDialog("Open WMV / JPG", "Video or Image files|*.wmv;*.jpg");
            if (!string.IsNullOrWhiteSpace(filename))
            {
                VideoConfig vs = new VideoConfig();

                System.IO.FileInfo fi = new System.IO.FileInfo(filename);
                vs.DeviceName = fi.Name;
                vs.FilePath = fi.FullName;
                AddNew(vs);
            }
        }

        private void AddURL()
        {
            TextPopupNode tn = new TextPopupNode("RTSP Stream", "URL", "");
            tn.OnOK += (url) =>
            {
                Uri uri = new Uri(url);
                VideoConfig vs = new VideoConfig();
                vs.DeviceName = uri.Host;
                vs.URL = uri.AbsoluteUri;
                AddNew(vs);
            };
            GetLayer<PopupLayer>().Popup(tn);
        }

        private async void RequestCameraPermission()
        {
            if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.OSX))
            {
                try
                {
                    var currentStatus = Tools.MacCameraPermissions.GetCameraPermissionStatus();
                    
                    string statusMessage = currentStatus switch
                    {
                        Tools.MacCameraPermissions.CameraPermissionStatus.Authorized => "✅ Camera permission is already granted.",
                        Tools.MacCameraPermissions.CameraPermissionStatus.Denied => "❌ Camera permission was denied. Please grant access in System Preferences > Security & Privacy > Camera.",
                        Tools.MacCameraPermissions.CameraPermissionStatus.Restricted => "⚠️ Camera access is restricted by system policy.",
                        Tools.MacCameraPermissions.CameraPermissionStatus.NotDetermined => "📷 Camera permission not yet requested.",
                        _ => "❓ Unknown camera permission status."
                    };

                    if (currentStatus == Tools.MacCameraPermissions.CameraPermissionStatus.Authorized)
                    {
                        // Already authorized, just show status and refresh
                        GetLayer<PopupLayer>().PopupMessage(statusMessage);
                        
                        // Refresh video sources to show newly detected cameras
                        RefreshVideoSources();
                    }
                    else if (currentStatus == Tools.MacCameraPermissions.CameraPermissionStatus.NotDetermined)
                    {
                        // Request permission
                        bool granted = await Tools.MacCameraPermissions.EnsureCameraPermissionAsync();
                        
                        // Show result
                        string resultMessage = granted 
                            ? "✅ Camera permission granted! USB cameras should now be detected."
                            : "❌ Camera permission denied. USB cameras will not be detected.";
                            
                        GetLayer<PopupLayer>().PopupMessage(resultMessage);
                        
                        if (granted)
                        {
                            // Refresh video sources to show newly detected cameras
                            RefreshVideoSources();
                        }
                    }
                    else
                    {
                        // Denied or restricted
                        string helpMessage = statusMessage + "\n\n" +
                            "To grant camera access:\n" +
                            "1. Open System Preferences\n" +
                            "2. Go to Security & Privacy\n" +
                            "3. Click the Camera tab\n" +
                            "4. Check the box next to FPVTrackside";
                        
                        GetLayer<PopupLayer>().PopupMessage(helpMessage);
                    }
                }
                catch (Exception ex)
                {
                    GetLayer<PopupLayer>().PopupMessage($"❌ Error checking camera permission: {ex.Message}");
                }
            }
        }

        private void RefreshVideoSources()
        {
            try
            {
                // Reload video manager to detect newly available cameras
                VideoManager.LoadDevices();
                
                // Update the list of available video sources for the Add button
                var newSources = VideoManager.GetAvailableVideoSources().ToList();
                
                // Log the refresh for debugging
                Console.WriteLine($"Refreshed video sources - found {newSources.Count} available cameras for Add button");
                foreach (var source in newSources)
                {
                    Console.WriteLine($"  - {source.DeviceName}");
                }
                
                // Note: We don't modify the left panel here - it should only show configured cameras
                // The Add button will automatically show the updated list of available cameras
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error refreshing video sources: {ex.Message}");
            }
        }

        protected override PropertyNode<VideoConfig> CreatePropertyNode(VideoConfig obj, PropertyInfo pi)
        {
            Console.WriteLine($"DEBUG: CreatePropertyNode called for property: {pi.Name} on camera: {obj.DeviceName}");
            
            if (pi.Name == "VideoMode")
            {
                Console.WriteLine($"DEBUG: Creating ModePropertyNode for VideoMode");
                return new ModePropertyNode(this, obj, pi, ButtonBackground, TextColor, ButtonHover);
            }

            if (pi.Name == "FlipMirrored")
            {
                Console.WriteLine($"DEBUG: Creating EnumPropertyNode for FlipMirrored");
                var enumNode = new EnumPropertyNode<VideoConfig>(obj, pi, ButtonBackground, TextColor, ButtonHover);
                Console.WriteLine($"DEBUG: EnumPropertyNode created for FlipMirrored, Options count: {enumNode.Options?.Count ?? 0}");
                if (enumNode.Options != null)
                {
                    foreach (var option in enumNode.Options)
                    {
                        Console.WriteLine($"DEBUG: FlipMirrored option: {option}");
                    }
                }
                return enumNode;
            }

            if (pi.Name == "AnyUSBPort")
            {
                if (obj.DeviceName == "OBS-Camera")
                {
                    return null;
                }
            }

            if (pi.Name == "AudioDevice")
            {
                return null;
                return new AudioDevicePropertyNode(VideoManager, obj, pi, ButtonBackground, TextColor, ButtonHover);
            }

            if (pi.Name == "Channels")
            {
                return new VideoChannelAssigner(obj, pi, TextColor);
            }

            if (pi.Name == "RecordResolution")
            {
                int[] resolutions = new int[] { 240, 360, 480, 720, 1080, 2160 };
                ListPropertyNode<VideoConfig> listPropertyNode = new ListPropertyNode<VideoConfig>(obj, pi, ButtonBackground, TextColor, ButtonHover, resolutions);
                return listPropertyNode;
            }

            if (pi.Name == "RecordFrameRate")
            {
                int[] frameRates = new int[] { 15, 24, 25, 30, 50, 60 };
                ListPropertyNode<VideoConfig> listPropertyNode = new ListPropertyNode<VideoConfig>(obj, pi, ButtonBackground, TextColor, ButtonHover, frameRates);
                return listPropertyNode;
            }

            if (pi.Name == "NeedsGMFBridge")
            {
                if (obj.NeedsGMFBridge)
                {
                    GMFBridgePropertyNode buttonPropertyNode = new GMFBridgePropertyNode(PlatformTools, obj, pi, ButtonBackground, TextColor, ButtonHover);
                    return buttonPropertyNode;
                }
                return null;
            }

            if (pi.Name == "Splits")
            {
                return new SplitsPropertyNode(obj, pi, ButtonBackground, TextColor, ButtonHover);
            }

            PropertyNode<VideoConfig> propertyNode = base.CreatePropertyNode(obj, pi);
            CheckVisible(propertyNode, obj);

            return propertyNode;
        }

        public override void SetObjects(IEnumerable<VideoConfig> toEdit, bool addRemove = false, bool cancelButton = true)
        {
            if (preview == null)
            {
                preview = new ColorNode(Theme.Current.Editor.Foreground.XNA);
                right.AddChild(preview);
            }

            if (physicalLayoutContainer == null)
            {
                physicalLayoutContainer = new AspectNode(16 / 9.0f);
                physicalLayoutContainer.Visible = false;
                ColorNode background = new ColorNode(Theme.Current.Editor.Foreground.XNA);
                physicalLayoutContainer.AddChild(background);

                right.AddChild(physicalLayoutContainer);

                physicalLayout = new ColorNode(Theme.Current.Editor.Text.XNA);
                physicalLayout.Scale(0.3f);
                physicalLayoutContainer.AddChild(physicalLayout);
            }


            base.SetObjects(toEdit, addRemove, cancelButton);

            preview.RelativeBounds = new RectangleF(objectProperties.RelativeBounds.X, objectProperties.RelativeBounds.Y, objectProperties.RelativeBounds.Width, 0.46f);

            objectProperties.Translate(0, preview.RelativeBounds.Height);
            objectProperties.AddSize(0, -preview.RelativeBounds.Height);

            float top = 0.002f;

            physicalLayoutContainer.RelativeBounds = new RectangleF(1.1f, preview.RelativeBounds.Y, 0.3f, preview.RelativeBounds.Height);
        }

        protected override void DoSetSelected(VideoConfig obj)
        {
            base.DoSetSelected(obj);

            if (obj != null)
            {
                preview.Visible = true;
                
                // Update camera information from hardware query
                UpdateCameraInformation(obj);
                
                // Ensure preview is properly initialized for the selected camera
                EnsurePreviewInitialized(obj);
                
                // Auto-populate modes for the selected camera
                Console.WriteLine($"DEBUG: Auto-populating modes for selected camera: {obj.DeviceName}");
                var modePropertyNode = PropertyNodes.OfType<ModePropertyNode>().FirstOrDefault();
                if (modePropertyNode != null)
                {
                    Console.WriteLine($"DEBUG: Found ModePropertyNode, clearing existing modes and querying new ones");
                    
                    // Clear existing modes first to ensure fresh data
                    modePropertyNode.ClearModes();
                    
                    // Force mode population for the selected camera
                    VideoManager.GetModes(obj, false, (result) =>
                    {
                        Console.WriteLine($"DEBUG: Modes received for {obj.DeviceName}: {result.Modes?.Count() ?? 0} modes");
                        if (result.Modes != null && result.Modes.Any())
                        {
                            modePropertyNode.AcceptModes(result);
                            Console.WriteLine($"DEBUG: Successfully populated modes for {obj.DeviceName}");
                        }
                        else
                        {
                            Console.WriteLine($"DEBUG: No modes returned for {obj.DeviceName}, using fallback");
                            // Force a second attempt with forceAll = true
                            VideoManager.GetModes(obj, true, (fallbackResult) =>
                            {
                                Console.WriteLine($"DEBUG: Fallback modes received for {obj.DeviceName}: {fallbackResult.Modes?.Count() ?? 0} modes");
                                modePropertyNode.AcceptModes(fallbackResult);
                            });
                        }
                    });
                }
                else
                {
                    Console.WriteLine($"DEBUG: No ModePropertyNode found for camera: {obj.DeviceName}");
                }

                RepairVideoPreview();
            }
        }

        private VideoConfig FindMatchingVideoSource(IEnumerable<VideoConfig> availableSources, string cameraDeviceName)
        {
            // First try exact match
            var exactMatch = availableSources.FirstOrDefault(s => 
                string.Equals(s.DeviceName, cameraDeviceName, StringComparison.OrdinalIgnoreCase));
            if (exactMatch != null)
            {
                Console.WriteLine($"DEBUG: Found exact match for '{cameraDeviceName}' -> '{exactMatch.DeviceName}'");
                return exactMatch;
            }
            
            // For USB cameras, try flexible matching since FFmpeg reports full VID:PID but config may have short name
            // Example: Config="USB Camera VID", Available="USB Camera VID:1133 PID:2249"
            foreach (var source in availableSources)
            {
                // Check if available name starts with camera name (handles VID:PID extensions)
                if (source.DeviceName.StartsWith(cameraDeviceName, StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine($"DEBUG: Found prefix match for '{cameraDeviceName}' -> '{source.DeviceName}'");
                    return source;
                }
                
                // Check if camera name starts with available name (reverse case)
                if (cameraDeviceName.StartsWith(source.DeviceName, StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine($"DEBUG: Found reverse prefix match for '{cameraDeviceName}' -> '{source.DeviceName}'");
                    return source;
                }
                
                // For USB cameras, try matching VID if both contain "USB Camera"
                if (cameraDeviceName.Contains("USB Camera") && source.DeviceName.Contains("USB Camera"))
                {
                    // Extract VID numbers from both names
                    var cameraVid = ExtractVidFromName(cameraDeviceName);
                    var sourceVid = ExtractVidFromName(source.DeviceName);
                    
                    if (!string.IsNullOrEmpty(cameraVid) && !string.IsNullOrEmpty(sourceVid))
                    {
                        if (cameraVid.Equals(sourceVid, StringComparison.OrdinalIgnoreCase))
                        {
                            Console.WriteLine($"DEBUG: Found VID match for '{cameraDeviceName}' -> '{source.DeviceName}' (VID: {cameraVid})");
                            return source;
                        }
                    }
                    else if (string.IsNullOrEmpty(cameraVid) && string.IsNullOrEmpty(sourceVid))
                    {
                        // Both are generic "USB Camera" - match them
                        Console.WriteLine($"DEBUG: Found generic USB Camera match for '{cameraDeviceName}' -> '{source.DeviceName}'");
                        return source;
                    }
                }
            }
            
            Console.WriteLine($"DEBUG: No matching video source found for camera: '{cameraDeviceName}'");
            return null;
        }
        
        private string ExtractVidFromName(string deviceName)
        {
            // Extract VID from names like "USB Camera VID:1133 PID:2249" or "USB Camera VID"
            var match = System.Text.RegularExpressions.Regex.Match(deviceName, @"VID(?::(\d+))?", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
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

        // New method to update camera information from hardware queries
        private void UpdateCameraInformation(VideoConfig camera)
        {
            try
            {
                Console.WriteLine($"DEBUG: Updating camera information for: {camera.DeviceName}");
                
                // Get current available devices from hardware query
                var availableSources = VideoManager.GetAvailableVideoSources().ToArray();
                
                // Find matching device by name using flexible matching
                var matchingSource = FindMatchingVideoSource(availableSources, camera.DeviceName);
                
                if (matchingSource != null)
                {
                    // Update ffmpegId if it has changed
                    if (matchingSource.ffmpegId != camera.ffmpegId)
                    {
                        Console.WriteLine($"DEBUG: Updating ffmpegId for {camera.DeviceName}: {camera.ffmpegId} -> {matchingSource.ffmpegId}");
                        camera.ffmpegId = matchingSource.ffmpegId;
                    }
                    
                    // Update other device information if available
                    if (matchingSource.FrameWork != camera.FrameWork)
                    {
                        Console.WriteLine($"DEBUG: Updating FrameWork for {camera.DeviceName}: {camera.FrameWork} -> {matchingSource.FrameWork}");
                        camera.FrameWork = matchingSource.FrameWork;
                    }
                    
                    Console.WriteLine($"DEBUG: Camera information updated successfully for: {camera.DeviceName}");
                }
                else
                {
                    Console.WriteLine($"DEBUG: WARNING - No matching hardware device found for: {camera.DeviceName}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"DEBUG: Error updating camera information for {camera.DeviceName}: {ex.Message}");
            }
        }

        private void EnsurePreviewInitialized(VideoConfig camera)
        {
            Console.WriteLine($"DEBUG: Ensuring preview is initialized for camera: {camera.DeviceName}");
            if (preview != null)
            {
                preview.Visible = true;
                // Force a layout update to ensure the preview is properly displayed
                RequestLayout();
                
                // Add a small delay to ensure the UI has time to update
                Task.Delay(100).ContinueWith(_ =>
                {
                    Console.WriteLine($"DEBUG: Preview initialization completed for camera: {camera.DeviceName}");
                });
            }
        }

        public override void ClearSelected()
        {
            base.ClearSelected();
            InitMapperNode(null);
            preview.Visible = false;
        }

        private void InitMapperNode(VideoConfig videoConfig)
        {
            Console.WriteLine($"DEBUG: InitMapperNode called for camera: {videoConfig?.DeviceName ?? "null"}");
            
            lock (locker)
            {
                Console.WriteLine($"DEBUG: Disposing existing mapper node");
                mapperNode?.Dispose();
                mapperNode = null;

                if (videoConfig == null)
                {
                    Console.WriteLine($"DEBUG: No video config provided, returning");
                    return;
                }

                if (VideoManager != null)
                {
                    Console.WriteLine($"DEBUG: Creating new ChannelVideoMapperNode for camera: {videoConfig.DeviceName}");
                    mapperNode = new ChannelVideoMapperNode(Profile, VideoManager, EventManager, videoConfig, Objects);
                    mapperNode.OnChange += MapperNode_OnChange;
                    
                    Console.WriteLine($"DEBUG: Adding mapper node to preview");
                    preview.AddChild(mapperNode);

                    // Check if FrameSource is already available
                    var frameSource = VideoManager.GetFrameSource(videoConfig);
                    if (frameSource != null && frameSource.Connected)
                    {
                        Console.WriteLine($"DEBUG: FrameSource is ready, calling MakeTable() immediately");
                        mapperNode.MakeTable();
                    }
                    else
                    {
                        Console.WriteLine($"DEBUG: FrameSource not ready yet, will call MakeTable() when available");
                        // Subscribe to the OnStart event to call MakeTable when FrameSource becomes available
                        VideoManager.OnStart += (fs) =>
                        {
                            if (fs.VideoConfig == videoConfig)
                            {
                                Console.WriteLine($"DEBUG: FrameSource became available for {videoConfig.DeviceName}, calling MakeTable()");
                                if (mapperNode != null)
                                {
                                    mapperNode.MakeTable();
                                }
                            }
                        };
                    }

                    Console.WriteLine($"DEBUG: Requesting layout update");
                    RequestLayout();
                    
                    Console.WriteLine($"DEBUG: InitMapperNode completed for camera: {videoConfig.DeviceName}");
                }
                else
                {
                    Console.WriteLine($"DEBUG: VideoManager is null, cannot create mapper node");
                }
            }
        }

        private void MapperNode_OnChange()
        {
            SplitsPropertyNode spn = PropertyNodes.OfType<SplitsPropertyNode>().FirstOrDefault();
            if (spn != null)
            {
                spn.UpdateFromObject();
            }
        }

        private void CheckVisible(PropertyNode<VideoConfig> propertyNode, VideoConfig obj)
        {
            if (propertyNode == null)
                return;

            string name = propertyNode.PropertyInfo.Name;
            if (name.Contains("Splits") && name != "Splits" && obj.Splits != Splits.Custom)
            {
                propertyNode.Visible = false;
            }
        }

        private void RepairVideoPreview()
        {
            Console.WriteLine($"DEBUG: RepairVideoPreview called for camera: {Selected?.DeviceName ?? "null"} (ffmpegId: {Selected?.ffmpegId})");
            
            // Prevent multiple simultaneous repair operations
            if (isRepairingVideoPreview)
            {
                Console.WriteLine($"DEBUG: RepairVideoPreview already in progress, skipping for: {Selected?.DeviceName}");
                return;
            }
            
            if (VideoManager != null && Selected != null)
            {
                isRepairingVideoPreview = true;
                // Store the current device info before recreation
                string originalDeviceName = Selected.DeviceName;
                string originalFfmpegId = Selected.ffmpegId;
                
                Console.WriteLine($"DEBUG: Stored device info - Name: {originalDeviceName}, ffmpegId: {originalFfmpegId}");
                
                // First, stop and remove the existing frame source to ensure clean restart
                Console.WriteLine($"DEBUG: Stopping existing frame source for camera: {Selected.DeviceName}");
                VideoManager.RemoveFrameSource(Selected);
                
                // Small delay to ensure the old stream is fully stopped
                System.Threading.Tasks.Task.Delay(200).ContinueWith(t =>
                {
                    // Ensure we're still on the UI thread
                    if (this.Parent != null)
                    {
                        // Verify device ID is still correct before recreation
                        Console.WriteLine($"DEBUG: Verifying device ID before recreation for: {originalDeviceName}");
                        
                        // Get current available devices and update ffmpegId if needed
                        var availableSources = VideoManager.GetAvailableVideoSources().ToArray();
                        var matchingSource = FindMatchingVideoSource(availableSources, originalDeviceName);
                        
                        if (matchingSource != null)
                        {
                            // Update ffmpegId if it has changed
                            if (matchingSource.ffmpegId != Selected.ffmpegId)
                            {
                                Console.WriteLine($"DEBUG: Updating ffmpegId for {Selected.DeviceName}: {Selected.ffmpegId} -> {matchingSource.ffmpegId}");
                                Selected.ffmpegId = matchingSource.ffmpegId;
                            }
                            Console.WriteLine($"DEBUG: Device {Selected.DeviceName} verified successfully");
                        }
                        else
                        {
                            Console.WriteLine($"DEBUG: WARNING - No matching source found for {originalDeviceName}");
                        }
                        
                        Console.WriteLine($"DEBUG: Creating new frame source for camera: {Selected.DeviceName} (ffmpegId: {Selected.ffmpegId})");
                        VideoManager.CreateFrameSource(new VideoConfig[] { Selected }, (fs) =>
                        {
                            Console.WriteLine($"DEBUG: Frame source created for camera: {Selected.DeviceName}");
                            
                            // Final verification that we got the right camera
                            if (Selected.DeviceName != originalDeviceName)
                            {
                                Console.WriteLine($"DEBUG: WARNING - Camera switched during recreation! Expected: {originalDeviceName}, Got: {Selected.DeviceName}");
                            }
                            else
                            {
                                Console.WriteLine($"DEBUG: SUCCESS - Camera recreation completed correctly for: {Selected.DeviceName}");
                            }
                            
                            if (mapperNode != null)
                            {
                                mapperNode.MakeTable();
                            }
                            
                            // Force refresh the preview to ensure it shows the new camera
                            Console.WriteLine($"DEBUG: Forcing preview refresh for camera: {Selected.DeviceName}");
                            if (preview != null)
                            {
                                preview.Visible = true;
                                // Force a layout update to ensure the preview is properly displayed
                                RequestLayout();
                            }
                            
                            // Clear the repair flag now that we're done
                            isRepairingVideoPreview = false;
                        });

                        InitMapperNode(Selected);
                    }
                });
            }
            else
            {
                // Clear the flag if we exit early
                isRepairingVideoPreview = false;
            }
        }

        protected override void Remove(MouseInputEvent mie)
        {
            VideoConfig videoConfig = Selected;
            base.Remove(mie);

            VideoManager.RemoveFrameSource(videoConfig);
        }

        protected override void ChildValueChanged(Change newChange)
        {
            foreach (var propertyNode in PropertyNodes)
            {
                CheckVisible(propertyNode, Selected);
            }

            if (newChange.PropertyInfo.Name == "VideoMode" || newChange.PropertyInfo.Name == "Flipped" || newChange.PropertyInfo.Name == "RecordVideoForReplays")
            {
                RepairVideoPreview();
            }

            if (newChange.PropertyInfo.Name == "Splits" && Selected != null)
            {
                Selected.VideoBounds = mapperNode.CreateChannelBounds(Selected).ToArray();
            }

            InitMapperNode(Selected);

            base.ChildValueChanged(newChange);
        }

        public override bool OnMouseInput(MouseInputEvent mouseInputEvent)
        {
            bool physicalVisible = false;

            if (preview.Contains(mouseInputEvent.Position) && mapperNode != null)
            {
                if (mapperNode.ChannelVideoInfos.Count() > 9)
                {
                    ChannelVideoMapNode match = mapperNode.ChannelVideoMapNodes.FirstOrDefault(r => r.Contains(mouseInputEvent.Position));
                    if (match != null)
                    {
                        physicalLayout.RelativeBounds = match.ChannelVideoInfo.ScaledRelativeSourceBounds;
                        physicalLayout.RequestLayout();
                        physicalVisible = true;
                    }
                }
            }

            physicalLayoutContainer.Visible = physicalVisible;


            return base.OnMouseInput(mouseInputEvent);
        }

        protected override void AddNew(VideoConfig obj)
        {
            Console.WriteLine($"DEBUG: AddNew called for camera: {obj.DeviceName}");
            
            // Add the new camera to the objects list
            base.AddNew(obj);
            
            // Select the newly added camera
            SetSelected(obj);
            
            // Force a layout update to ensure the preview is properly initialized
            RequestLayout();
            
            // Add a small delay to allow the UI to update before triggering the preview
            System.Threading.Tasks.Task.Delay(100).ContinueWith(t =>
            {
                // Ensure we're still on the UI thread
                if (this.Parent != null)
                {
                    Console.WriteLine($"DEBUG: Triggering delayed preview initialization for camera: {obj.DeviceName}");
                    
                    // Ensure preview is properly initialized for the selected camera
                    EnsurePreviewInitialized(obj);
                    
                    // Force complete refresh for the newly added camera
                    Console.WriteLine($"DEBUG: Forcing complete refresh for camera: {obj.DeviceName}");
                    
                    // Call RepairVideoPreview to ensure frame source is created
                    RepairVideoPreview();
                    
                    // Explicitly call InitMapperNode to ensure the preview is connected
                    Console.WriteLine($"DEBUG: Explicitly calling InitMapperNode for camera: {obj.DeviceName}");
                    InitMapperNode(obj);
                    
                    // Force another layout update
                    RequestLayout();
                }
            });
            
            Console.WriteLine($"DEBUG: AddNew completed for camera: {obj.DeviceName}");
        }

        private class AudioDevicePropertyNode : ListPropertyNode<VideoConfig>
        {
            private VideoManager vm;

            public AudioDevicePropertyNode(VideoManager vm, VideoConfig obj, PropertyInfo pi, Color textBackground, Color textColor, Color hover) 
                : base(obj, pi, textBackground, textColor, hover, null)
            {
                this.vm = vm;
            }

            public override bool OnMouseInput(MouseInputEvent mouseInputEvent)
            {
                if (mouseInputEvent.Button == MouseButtons.Left && mouseInputEvent.ButtonState == ButtonStates.Released)
                {
                    List<string> audioDevices = new List<string>();
                    audioDevices.Add("None");
                    audioDevices.AddRange(vm.GetAvailableAudioSources());
                    SetOptions(audioDevices);
                }

                return base.OnMouseInput(mouseInputEvent);
            }
        }

        private class ModePropertyNode : ListPropertyNode<VideoConfig>
        {
            private VideoSourceEditor vse;

            private Mode[] modes;

            private bool rebootRequired;

            public ModePropertyNode(VideoSourceEditor vse, VideoConfig obj, PropertyInfo pi, Color background, Color textColor, Color hoverColor)
                : base(obj, pi, background, textColor, hoverColor)
            {
                this.vse = vse;
                modes = new Mode[0];

                rebootRequired = false;
                
                // Automatically populate modes when a camera is added
                if (vse.VideoManager != null)
                {
                    Console.WriteLine($"DEBUG: Auto-populating modes for camera: {obj.DeviceName}");
                    vse.VideoManager.GetModes(obj, false, AcceptModes);
                }
            }

            public void ClearModes()
            {
                Console.WriteLine($"DEBUG: ClearModes called for camera: {Object.DeviceName}");
                modes = new Mode[0];
                Options = null;
                rebootRequired = false;
            }

            public void AcceptModes(VideoManager.ModesResult result)
            {
                Console.WriteLine($"DEBUG: AcceptModes called with {result.Modes?.Count() ?? 0} modes for camera: {Object.DeviceName}");
                
                try
                {
                    modes = TrimModes(result.Modes).ToArray();
                    Console.WriteLine($"DEBUG: After trimming, {modes.Length} modes available for camera: {Object.DeviceName}");
                    
                    if (result.RebootRequired)
                    {
                        GetLayer<PopupLayer>().PopupMessage("Please reboot capture device: " + Object.DeviceName);
                        rebootRequired = true;
                    }
                    else
                    {
                        rebootRequired = false;
                    }

                    SetOptions(modes);
                    // Don't automatically show the menu - only show when user clicks
                    // ShowMouseMenu(); // REMOVED THIS LINE
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"DEBUG: Exception in AcceptModes for camera {Object.DeviceName}: {ex.Message}");
                    Console.WriteLine($"DEBUG: Stack trace: {ex.StackTrace}");
                }
            }

            public override bool OnMouseInput(MouseInputEvent mouseInputEvent)
            {
                if (mouseInputEvent.Button == MouseButtons.Left && mouseInputEvent.ButtonState == ButtonStates.Released)
                {
                    bool forceAllModes = Keyboard.GetState().IsKeyDown(Keys.LeftControl) || Keyboard.GetState().IsKeyDown(Keys.RightControl);
                    if (forceAllModes)
                    {
                        // Get ALL the modes form the device
                        vse.VideoManager.GetModes(Object, true, AcceptModes);
                    }
                    else if (rebootRequired || !modes.Any())
                    {
                        // Get the normal modes form the device
                        vse.VideoManager.GetModes(Object, false, AcceptModes);
                    }
                    else
                    {
                        // just use the cached ones..
                        SetOptions(modes);
                        ShowMouseMenu();
                        return true;
                    }

                    return true;
                }
                return base.OnMouseInput(mouseInputEvent);
            }

            private void SetOptions(Mode[] ms)
            {
                Console.WriteLine($"DEBUG: SetOptions called with {ms.Length} modes for camera: {Object.DeviceName}");
                IEnumerable<Mode> ordered = ms.OrderByDescending(m => m.FrameWork)
                                                     .ThenByDescending(m => m.Width)
                                                     .ThenByDescending(m => m.Height)
                                                     .ThenByDescending(m => m.FrameRate)
                                                     .ThenByDescending(m => m.Format);

                if (ms.Any())
                {
                    Options = ordered.OfType<object>().ToList();
                    Console.WriteLine($"DEBUG: Set {Options.Count} options for camera: {Object.DeviceName}");
                }
                else
                {
                    Console.WriteLine($"DEBUG: No options to set for camera: {Object.DeviceName}");
                }
            }

            private IEnumerable<Mode> TrimModes(IEnumerable<Mode> modes)
            {
                bool allItems = Keyboard.GetState().IsKeyDown(Keys.LeftControl);
                if (!allItems)
                {
                    // Seriously, under 15 fps is dumb
                    modes = modes.Where(m => m.FrameRate >= 15);
                }

                var grouped = modes.GroupBy(m => new Tuple<FrameWork, int, int, float, string>(m.FrameWork, m.Width, m.Height, m.FrameRate, allItems? m.Format : "")).OrderByDescending(t => t.Key.Item1);

                foreach (var group in grouped)
                {
                    if (allItems)
                    {
                        foreach (var g in group)
                        {
                            if (g != null)
                            {
                                yield return g;
                            }
                        }
                    }
                    else
                    {
                        var mode = vse.VideoManager.PickMode(Object, group);
                        if (mode != null)
                        {
                            yield return mode;
                        }
                    }
                }
            }

            

            public override string ValueToString(object value)
            {
                Mode mode = value as Mode;
                if (mode == null) return "";

                return mode.ToString();
            }
        }

        class VideoChannelAssigner : NamedPropertyNode<VideoConfig>
        {
            public VideoChannelAssigner(VideoConfig obj, PropertyInfo pi, Color textColor) : base(obj, pi, textColor)
            {
                ColorNode cn = new ColorNode(Color.Gray);
                AddChild(cn);
            }

            public override void Layout(RectangleF parentBounds)
            {
                parentBounds.Height *= 4;
                base.Layout(parentBounds);
            }
        }
    }

    public class ChannelVideoMapperNode : Node
    {
        private Node main;

        private TableNode table;

        private VideoManager videoManager;

        public ChannelVideoInfo[] ChannelVideoInfos { get; private set; }

        public List<ChannelVideoMapNode> ChannelVideoMapNodes { get; private set; }

        private VideoConfig videoConfig;
        private VideoConfig[] others;

        private Channel[] eventChannels;

        public event Action OnChange;

        public ChannelVideoMapperNode(Profile profile, VideoManager videoManager, EventManager eventManager, VideoConfig videoConfig, IEnumerable<VideoConfig> others)
        {
            ChannelVideoMapNodes = new List<ChannelVideoMapNode>();

            this.others = others.ToArray();
            this.videoManager = videoManager;
            this.videoConfig = videoConfig;

            if (eventManager != null && eventManager.Channels != null)
            {
                eventChannels = eventManager.Channels;
            }
            else
            {
                eventChannels = Channel.Read(profile);
            }

            // Always order by frequency.
            eventChannels = eventChannels.OrderBy(r => r.Frequency).ToArray();

            main = new Node();
            AddChild(main);

            table = new TableNode(1, 1);
            table.RelativeBounds = new RectangleF(0, 0, 1, 0.96f);
            main.AddChild(table);

            TextNode channelInstructions = new TextNode("Click on the video feed to change channel / camera assignments / edit settings", Theme.Current.Editor.Text.XNA);
            channelInstructions.RelativeBounds = new RectangleF(0, table.RelativeBounds.Bottom, 1, 1 - table.RelativeBounds.Bottom);
            main.AddChild(channelInstructions);

            CreateChannelVideoInfos();
            MakeTable();
        }

        private void CreateChannelVideoInfos()
        {
            ChannelVideoInfos = videoManager.CreateChannelVideoInfos(others).Where(cc => cc.FrameSource.VideoConfig == videoConfig).ToArray();

            foreach (ChannelVideoInfo cvi in ChannelVideoInfos)
            {
                Channel channel = cvi.Channel;

                if ((channel == null || channel == Channel.None) && cvi.VideoBounds.SourceType == SourceTypes.FPVFeed)
                {
                    IEnumerable<Channel> otherDevicesInUse = others.SelectMany(vs => vs.VideoBounds).Select(vb => vb.GetChannel()).Where(ca => ca != null);
                    IEnumerable<Channel> thisDeviceInUse = ChannelVideoInfos.Select(c => c.Channel).Where(ca => ca != null);

                    IEnumerable<Channel> inUse = otherDevicesInUse.Concat(thisDeviceInUse).Distinct();
                    Channel next = eventChannels.Where(d => !inUse.Contains(d)).FirstOrDefault();
                    if (next != null)
                    {
                        channel = next;
                    }
                    else
                    {
                        channel = Channel.None;
                    }
                    cvi.Channel = channel;
                    cvi.VideoBounds.Channel = channel.ToStringShort();
                }
            }
        }

        public void MakeTable()
        {
            lock (table)
            {
                if (table != null)
                {
                    table.ClearDisposeChildren();
                }
                lock (ChannelVideoMapNodes)
                {
                    ChannelVideoMapNodes.Clear();
                }

                int columns = (int)Math.Ceiling(Math.Sqrt(ChannelVideoInfos.Length));
                int rows = (int)Math.Ceiling(ChannelVideoInfos.Length / (float)columns);
                table.SetSize(rows, columns);

                Color transparentForeground = new Color(Theme.Current.Editor.Foreground.XNA, 0.5f);

                int count = Math.Min(ChannelVideoInfos.Length, table.CellCount);
                for (int i = 0; i < count; i++)
                {
                    ChannelVideoInfo channelVideoInfo = ChannelVideoInfos[i];
                    Node cell = table.GetCell(i);

                    if (cell != null && channelVideoInfo.FrameSource != null)
                    {
                        ChannelVideoMapNode cvmn = new ChannelVideoMapNode(channelVideoInfo);
                        cvmn.OnClick += (m, c) => { ShowChangeMenu(m, c.ChannelVideoInfo); };
                        cell.AddChild(cvmn);

                        lock (ChannelVideoMapNodes)
                        {
                            ChannelVideoMapNodes.Add(cvmn);
                        }
                    }
                }
                RequestLayout();
            }

            OnChange?.Invoke();
        }

        public override void Draw(Drawer id, float parentAlpha)
        {
            base.Draw(id, parentAlpha);

            lock (ChannelVideoMapNodes)
            {
                foreach (ChannelVideoMapNode fn in ChannelVideoMapNodes)
                {
                    fn.FrameNode.NeedsAspectRatioUpdate = true;
                }
            }
        }

        private void ShowChangeMenu(MouseInputEvent mie, ChannelVideoInfo channelVideoInfo)
        {
            MouseMenu mouseMenu = new MouseMenu(this);
            mouseMenu.TopToBottom = true;

            if (channelVideoInfo.VideoBounds.SourceType != SourceTypes.FPVFeed)
            {
                mouseMenu.AddItem("Edit Cam Settings", () =>
                {
                    VideoBoundsEditor editor = new VideoBoundsEditor(channelVideoInfo.VideoBounds);
                    GetLayer<PopupLayer>().Popup(editor);
                });
                mouseMenu.AddBlank();
            }

            MouseMenu channelMenu = mouseMenu.AddSubmenu("Channel Assigment");

            channelMenu.AddItem("No Channel", () => { AssignChannel(channelVideoInfo, Channel.None); });


            if (eventChannels != null)
            {
                channelMenu.AddItem("Auto-Assign", () => { LinearChannelAssignment(eventChannels, false); });
                channelMenu.AddItem("Auto-Assign (Share frequencies)", () => { LinearChannelAssignment(eventChannels, true); });
                channelMenu.AddSubmenu("Event Channels", (c) => { AssignChannel(channelVideoInfo, c); }, eventChannels);
            }

            foreach (Band band in Channel.GetBands())
            {
                IEnumerable<Channel> cs = Channel.AllChannels.Where(c => c.Band == band).OrderBy(c => c.Number);
                channelMenu.AddSubmenu(band.ToString(), (c) => { AssignChannel(channelVideoInfo, c); }, cs.ToArray());
            }

            MouseMenu cameraMenu = mouseMenu.AddSubmenu("Camera Assignment");

            foreach (SourceTypes sourceType in Enum.GetValues(typeof(SourceTypes)))
            {
                if (sourceType == SourceTypes.FPVFeed)
                    continue;

                cameraMenu.AddItem(sourceType.ToString() +  " Camera", () =>
                {
                    SetSourceType(channelVideoInfo, sourceType);
                });
            }
            
            mouseMenu.AddBlank();

            MouseMenu splitMenu = mouseMenu.AddSubmenu("Split");
            foreach (Splits split in Enum.GetValues(typeof(Splits)))
            {
                if (split == Splits.Custom || split == Splits.SingleChannel)
                    continue;

                splitMenu.AddItem("Split " + split.ToHumanString(), () =>
                {
                    Split(channelVideoInfo, split);
                });
            }

            MouseMenu cropMenu = mouseMenu.AddSubmenu("Crop");
            cropMenu.AddItem("Crop 4:3", () => { Crop(channelVideoInfo, 4, 3); });
            cropMenu.AddItem("Crop 16:9", () => { Crop(channelVideoInfo, 16, 9); });


            mouseMenu.AddItem("Duplicate", () => { Duplicate(channelVideoInfo, Channel.None); });
            mouseMenu.AddItem("Duplicate Source", () => { DuplicateSource(); });

            if (eventChannels != null)
            {
                Channel c = channelVideoInfo.Channel;

                Channel channel = eventChannels.GetOthersInChannelGroup(c).FirstOrDefault();
                if (channel != null)
                {
                    mouseMenu.AddItem("Duplicate to " + channel.UIDisplayName, () => { Duplicate(channelVideoInfo, channel); });
                }
            }

            mouseMenu.AddBlank();
            mouseMenu.AddItem("Remove", () => { RemoveView(channelVideoInfo); });
            mouseMenu.AddItem("Reset All", Reset);

            mouseMenu.Show(mie);

        }

        public void LinearChannelAssignment(IEnumerable<Channel> channels, bool shareChannelGroups)
        {
            if (channels != null)
            {
                if (shareChannelGroups)
                {
                    for (int j = 0; j < ChannelVideoInfos.Length; j++)
                    {
                        ChannelVideoInfos[j].Channel = Channel.None;
                    }


                    int i = 0;
                    foreach(Channel[] grouped in channels.GetChannelGroups())
                    {
                        if (i >= ChannelVideoInfos.Length)
                            break;

                        ChannelVideoInfo cvi = ChannelVideoInfos[i];
                        Channel first = grouped.FirstOrDefault();
                        AssignChannel(cvi, grouped.FirstOrDefault());

                        foreach (Channel channel in grouped.Where(r => r != first))
                        {
                            Duplicate(cvi, channel);
                        }
                        i++;
                    }

                    MakeTable();
                }
                else
                {
                    Channel[] ordered = channels.OrderBy(c => c.Band.GetBandType()).ThenBy(c => c.Frequency).ToArray();

                    int max = Math.Min(ChannelVideoInfos.Length, ordered.Length);
                    for (int i = 0; i < max; i++)
                    {
                        AssignChannel(ChannelVideoInfos[i], ordered[i]);
                    }
                }
            }
        }

        private void Crop(ChannelVideoInfo channelVideoInfo, int width, int height)
        {
            float scale = height / (float)width;

            RectangleF f = channelVideoInfo.VideoBounds.RelativeSourceBounds;
            channelVideoInfo.VideoBounds.RelativeSourceBounds = f.Scale(scale, 1);

            CreateChannelVideoInfos();
            MakeTable();
        }

        private void RemoveView(ChannelVideoInfo channelVideoInfo)
        {
            videoConfig.Splits = Splits.Custom;

            List<VideoBounds> videoBoundsList = videoConfig.VideoBounds.ToList();

            videoConfig.VideoBounds = videoBoundsList.Where(r => r != channelVideoInfo.VideoBounds).ToArray();

            CreateChannelVideoInfos();
            MakeTable();
        }

        private void Duplicate(ChannelVideoInfo channelVideoInfo, Channel channel)
        {
            videoConfig.Splits = Splits.Custom;

            VideoBounds clone = channelVideoInfo.VideoBounds.Clone();
            clone.Channel = channel.ToStringShort();

            List<VideoBounds> videoBoundsList = videoConfig.VideoBounds.ToList();

            int index = videoBoundsList.IndexOf(channelVideoInfo.VideoBounds);
            if (index >= 0)
            {
                videoBoundsList.Insert(index + 1, clone);
            }

            videoConfig.VideoBounds = videoBoundsList.ToArray();

            CreateChannelVideoInfos();
            MakeTable();
        }

        private void DuplicateSource()
        {
            videoConfig.Splits = Splits.Custom;

            List<VideoBounds> videoBoundsList = videoConfig.VideoBounds.ToList();

            VideoBounds source = new VideoBounds();
            source.RelativeSourceBounds = new RectangleF(0, 0, 1, 1);
            videoBoundsList.Add(source);

            videoConfig.VideoBounds = videoBoundsList.ToArray();

            CreateChannelVideoInfos();
            MakeTable();
        }

        private void Reset()
        {
            videoConfig.VideoBounds = CreateChannelBounds(Splits.SingleChannel, new RectangleF(0, 0, 1, 1)).ToArray();
            CreateChannelVideoInfos();
            MakeTable();
        }

        private void Split(ChannelVideoInfo channelVideoInfo, Splits split)
        {
            bool firstSplit = channelVideoInfo.VideoBounds.RelativeSourceBounds.Width == 1 && channelVideoInfo.VideoBounds.RelativeSourceBounds.Height == 1;

            videoConfig.Splits = Splits.Custom;

            List<VideoBounds> cbs = new List<VideoBounds>();

            foreach (VideoBounds vb in videoConfig.VideoBounds)
            {
                if (vb == channelVideoInfo.VideoBounds)
                {
                    var newBoudns = CreateChannelBounds(split, channelVideoInfo.VideoBounds.RelativeSourceBounds);
                    cbs.AddRange(newBoudns);
                }
                else
                {
                    cbs.Add(vb);
                }
            }

            videoConfig.VideoBounds = cbs.ToArray();

            CreateChannelVideoInfos();

            if (firstSplit)
            {
                LinearChannelAssignment(eventChannels, false);
            }

            MakeTable();
        }

        private void SetSourceType(ChannelVideoInfo channelVideoInfo, SourceTypes sourceType)
        {
            channelVideoInfo.VideoBounds.SourceType = sourceType;

            if (string.IsNullOrEmpty(channelVideoInfo.VideoBounds.OverlayText))
            {
                channelVideoInfo.VideoBounds.OverlayText = sourceType.ToString();
            }

            if (sourceType != SourceTypes.FPVFeed)
            {
                channelVideoInfo.Channel = Channel.None;
                channelVideoInfo.VideoBounds.Channel = channelVideoInfo.Channel.ToStringShort();

                VideoBoundsEditor editor = new VideoBoundsEditor(channelVideoInfo.VideoBounds);
                GetLayer<PopupLayer>().Popup(editor);
            }

            RemoveDuplicateChannels();
            MakeTable();
        }

        private void RemoveChannel(Channel c)
        {
            if (c == Channel.None)
                return;

            foreach (VideoBounds vb in videoConfig.VideoBounds)
            {
                if (vb.Channel == c.ToStringShort())
                {
                    vb.Channel = Channel.None.ToStringShort();
                }
            }
        }

        private void AssignChannel(ChannelVideoInfo channelVideoInfo, Channel c)
        {
            SetSourceType(channelVideoInfo, SourceTypes.FPVFeed);

            RemoveChannel(c);

            channelVideoInfo.Channel = c;
            channelVideoInfo.VideoBounds.Channel = c.ToStringShort();

            MakeTable();
        }

        private void RemoveDuplicateChannels()
        {
            List<string> channelIds = new List<string>();
            foreach (VideoBounds vb in videoConfig.VideoBounds)
            {
                if (vb.Channel == Channel.None.ToStringShort())
                    continue;

                if (channelIds.Contains(vb.Channel))
                {
                    vb.Channel = Channel.None.ToStringShort();
                }
                else
                {
                    channelIds.Add(vb.Channel);
                }
            }
        }

        public IEnumerable<VideoBounds> CreateChannelBounds(VideoConfig videoConfig)
        {
            return CreateChannelBounds(videoConfig.Splits, RectangleF.Centered(1, 1));
        }

        public IEnumerable<VideoBounds> CreateChannelBounds(Splits splits, RectangleF bounds)
        {
            int horz, vertz;
            splits.GetSplits(out horz, out vertz);
            return CreateChannelBounds(horz, vertz, bounds);
        }

        public IEnumerable<VideoBounds> CreateChannelBounds(int horizontalSplits, int verticalSplits, RectangleF bounds)
        {
            float widthF = horizontalSplits;
            float heightF = verticalSplits;

            for (int y = 0; y < verticalSplits; y++)
            {
                for (int x = 0; x < horizontalSplits; x++)
                {
                    RectangleF relativeBounds = new RectangleF(x / widthF, y / heightF, 1 / widthF, 1 / heightF);

                    RectangleF insideParent = new RectangleF();
                    insideParent.X = bounds.X + bounds.Width * relativeBounds.X;
                    insideParent.Y = bounds.Y + bounds.Height * relativeBounds.Y;
                    insideParent.Width = bounds.Width * relativeBounds.Width;
                    insideParent.Height = bounds.Height * relativeBounds.Height;

                    VideoBounds cvi = new VideoBounds() { Channel = Channel.None.ToStringShort(), RelativeSourceBounds = insideParent };
                    yield return cvi;
                }
            }
        }
    }

    public class ChannelVideoMapNode : Node
    {
        public event Action<MouseInputEvent, ChannelVideoMapNode> OnClick;

        public FrameNode FrameNode { get; private set; }
        public ChannelVideoInfo ChannelVideoInfo { get; private set; }

        public HoverNode HoverNode { get; private set; }

        public ChannelVideoMapNode(ChannelVideoInfo channelVideoInfo)
        {
            ChannelVideoInfo = channelVideoInfo;

            SetData();
        }

        private void SetData()
        {
            ClearDisposeChildren();

            VideoConfig videoConfig = ChannelVideoInfo.FrameSource.VideoConfig;

            FrameNode = new FrameNode(ChannelVideoInfo.FrameSource);
            FrameNode.RelativeSourceBounds = ChannelVideoInfo.ScaledRelativeSourceBounds;
            FrameNode.KeepAspectRatio = true;
            AddChild(FrameNode);

            HoverNode = new HoverNode(Theme.Current.Hover.XNA);
            FrameNode.AddChild(HoverNode);

            ButtonNode change = new ButtonNode();
            AddChild(change);
            change.OnClick += (m) =>
            {
                OnClick(m, this);
            };

            AbsoluteHeightNode absoluteHeightNode = new AbsoluteHeightNode(30);
            absoluteHeightNode.Alignment = RectangleAlignment.Center;
            FrameNode.AddChild(absoluteHeightNode);

            string text;
            switch (ChannelVideoInfo.VideoBounds.SourceType)
            {
                default:
                    text = ChannelVideoInfo.VideoBounds.SourceType.ToString().CamelCaseToHuman();
                    break;

                case SourceTypes.FPVFeed:
                    text = ChannelVideoInfo.Channel.ToStringShort();
                    break;
            }

            TextNode textNode = new TextNode(text, Theme.Current.Editor.Text.XNA);
            textNode.Style.Border = true;
            absoluteHeightNode.AddChild(textNode);
        }

        public override bool OnMouseInput(MouseInputEvent mouseInputEvent)
        {
            if (mouseInputEvent.Button == MouseButtons.Left && mouseInputEvent.ButtonState == ButtonStates.Pressed)
            {
                GetLayer<DragLayer>()?.RegisterDrag(this, mouseInputEvent);
            }

            return base.OnMouseInput(mouseInputEvent);
        }

        public override bool OnDrop(MouseInputEvent finalInputEvent, Node node)
        {
            ChannelVideoMapNode other = node as ChannelVideoMapNode;
            if (other != null)
            {
                // Cache existing settings..
                Channel thisChannel = ChannelVideoInfo.Channel;
                string thisVBChannel = ChannelVideoInfo.VideoBounds.Channel;
                SourceTypes thisSourceType = ChannelVideoInfo.VideoBounds.SourceType;
                string thisOverlayText = ChannelVideoInfo.VideoBounds.OverlayText;
                OverlayAlignment thisOverlayAlignment = ChannelVideoInfo.VideoBounds.OverlayAlignment;
                bool thisShowInGrid = ChannelVideoInfo.VideoBounds.ShowInGrid;

                // Set the new ones
                ChannelVideoInfo.Channel = other.ChannelVideoInfo.Channel;
                ChannelVideoInfo.VideoBounds.Channel = other.ChannelVideoInfo.VideoBounds.Channel;
                ChannelVideoInfo.VideoBounds.SourceType = other.ChannelVideoInfo.VideoBounds.SourceType;
                ChannelVideoInfo.VideoBounds.OverlayText = other.ChannelVideoInfo.VideoBounds.OverlayText;
                ChannelVideoInfo.VideoBounds.OverlayAlignment = other.ChannelVideoInfo.VideoBounds.OverlayAlignment;
                ChannelVideoInfo.VideoBounds.ShowInGrid = other.ChannelVideoInfo.VideoBounds.ShowInGrid;

                // set the old ones..
                other.ChannelVideoInfo.Channel = thisChannel;
                other.ChannelVideoInfo.VideoBounds.Channel = thisVBChannel;
                other.ChannelVideoInfo.VideoBounds.SourceType = thisSourceType;
                other.ChannelVideoInfo.VideoBounds.OverlayText = thisOverlayText;
                other.ChannelVideoInfo.VideoBounds.OverlayAlignment = thisOverlayAlignment;
                other.ChannelVideoInfo.VideoBounds.ShowInGrid = thisShowInGrid;

                SetData();
                other.SetData();

                return true;
            }

            return base.OnDrop(finalInputEvent, node);
        }

    }

    public class VideoBoundsEditor : ObjectEditorNode<VideoBounds>
    {
        public VideoBoundsEditor(VideoBounds toEdit)
            : base(toEdit, false, true, false)
        {
            heading.Text = "Camera Display Editor";
            Scale(0.5f, 0.5f);
            SetButtonsHeight(0.1f);
        }
    }

    public class GMFBridgePropertyNode : ButtonPropertyNode<VideoConfig>
    {
        private PlatformTools platformTools1;


        public GMFBridgePropertyNode(PlatformTools platformTools, VideoConfig obj, PropertyInfo pi, Color backgroundColor, Color textColor, Color hoverColor)
            : base(obj, pi, backgroundColor, textColor, hoverColor, "Please click here, then run GMFBridge Installer & restart FPVTrackside", StartInstaller)
        {
            platformTools1 = platformTools;
            Visible = false;
            UpdateFromObject();
        }

        public static void StartInstaller()
        {
            try
            {
                string filename = Path.Combine(Directory.GetCurrentDirectory(), "GMFBridge.msi");
                string argument = "/i \"" + filename + "\"";
                System.Diagnostics.Process.Start("msiexec.exe", argument);
            }
            catch
            {

            }
        }

        public override void UpdateFromObject()
        {
            if (platformTools1 != null)
            {
                bool installed = platformTools1.Check("GMFBridge");

                bool vis = !installed;

                if (vis != Visible)
                {
                    Visible = vis;
                    RequestLayout();
                    RequestRedraw();
                }
            }

            base.UpdateFromObject();
        }
    }

    public class SplitsPropertyNode : EnumPropertyNode<VideoConfig>
    {
        public SplitsPropertyNode(VideoConfig obj, PropertyInfo pi, Color background, Color textColor, Color hover)
            : base(obj, pi, background, textColor, hover)
        {
        }

        public override string ValueToString(object value)
        {
            if (value is Splits)
            {
                Splits split = (Splits)value;
                return split.ToHumanString();
            }
            return base.ValueToString(value);
        }
    }
}
