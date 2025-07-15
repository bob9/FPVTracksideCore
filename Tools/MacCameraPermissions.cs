using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Tools
{
    /// <summary>
    /// Static utility class for handling macOS camera permissions
    /// </summary>
    public static class MacCameraPermissions
    {
        // Camera permission status enum
        public enum CameraPermissionStatus
        {
            NotDetermined = 0,
            Restricted = 1,
            Denied = 2,
            Authorized = 3
        }

        /// <summary>
        /// Checks the current camera permission status
        /// </summary>
        public static CameraPermissionStatus GetCameraPermissionStatus()
        {
            try
            {
                // Only run on macOS
                if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                {
                    return CameraPermissionStatus.Authorized; // Non-macOS platforms don't need permission
                }

                // Get AVCaptureDevice class
                var captureDeviceClass = objc_getClass("AVCaptureDevice");
                
                // Get the video media type (AVMediaTypeVideo)
                var avMediaTypeVideo = GetAVMediaTypeVideo();
                
                // Call authorizationStatusForMediaType:
                var authStatus = objc_msgSend(captureDeviceClass, sel_registerName("authorizationStatusForMediaType:"), avMediaTypeVideo);
                
                objc_msgSend(avMediaTypeVideo, sel_registerName("release"));
                
                return (CameraPermissionStatus)(int)authStatus;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error checking camera permission: {ex.Message}");
                return CameraPermissionStatus.NotDetermined;
            }
        }

        /// <summary>
        /// Requests camera permission asynchronously
        /// </summary>
        public static Task<bool> RequestCameraPermissionAsync()
        {
            var tcs = new TaskCompletionSource<bool>();
            
            try
            {
                // Only run on macOS
                if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                {
                    tcs.SetResult(true); // Non-macOS platforms don't need permission
                    return tcs.Task;
                }

                // Check if we're on the main thread
                if (IsMainThread())
                {
                    RequestCameraPermissionOnMainThread(tcs);
                }
                else
                {
                    // Use SynchronizationContext to dispatch to main thread
                    var context = SynchronizationContext.Current ?? new SynchronizationContext();
                    context.Post(_ => RequestCameraPermissionOnMainThread(tcs), null);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error requesting camera permission: {ex.Message}");
                tcs.SetResult(false);
            }
            
            return tcs.Task;
        }

        private static void RequestCameraPermissionOnMainThread(TaskCompletionSource<bool> tcs)
        {
            try
            {
                // Get AVCaptureDevice class
                var captureDeviceClass = objc_getClass("AVCaptureDevice");
                
                // Get the video media type (AVMediaTypeVideo)
                var avMediaTypeVideo = GetAVMediaTypeVideo();
                
                // Create a block for the completion handler
                // For simplicity, we'll use a polling approach since block creation is complex
                var authStatus = objc_msgSend(captureDeviceClass, sel_registerName("authorizationStatusForMediaType:"), avMediaTypeVideo);
                
                if ((int)authStatus == 0) // NotDetermined
                {
                    // Call requestAccessForMediaType:completionHandler:
                    // Since creating blocks is complex, we'll request and then poll for the result
                    objc_msgSend(captureDeviceClass, sel_registerName("requestAccessForMediaType:completionHandler:"), 
                               avMediaTypeVideo, IntPtr.Zero);
                    
                    // Poll for result change (not ideal but works for this case)
                    Task.Run(async () =>
                    {
                        for (int i = 0; i < 50; i++) // Wait up to 5 seconds
                        {
                            await Task.Delay(100);
                            var newStatus = GetCameraPermissionStatus();
                            if (newStatus != CameraPermissionStatus.NotDetermined)
                            {
                                tcs.SetResult(newStatus == CameraPermissionStatus.Authorized);
                                objc_msgSend(avMediaTypeVideo, sel_registerName("release"));
                                return;
                            }
                        }
                        // Timeout
                        tcs.SetResult(false);
                        objc_msgSend(avMediaTypeVideo, sel_registerName("release"));
                    });
                }
                else
                {
                    // Permission already determined
                    bool granted = (int)authStatus == 3; // Authorized
                    tcs.SetResult(granted);
                    objc_msgSend(avMediaTypeVideo, sel_registerName("release"));
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error in RequestCameraPermissionOnMainThread: {ex.Message}");
                tcs.SetResult(false);
            }
        }

        /// <summary>
        /// Ensures camera permission is granted before accessing cameras
        /// </summary>
        public static async Task<bool> EnsureCameraPermissionAsync()
        {
            // Only run on macOS
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                return true; // Non-macOS platforms don't need permission
            }

            var currentStatus = GetCameraPermissionStatus();
            Console.WriteLine($"Current camera permission status: {currentStatus}");
            
            switch (currentStatus)
            {
                case CameraPermissionStatus.Authorized:
                    return true;
                    
                case CameraPermissionStatus.NotDetermined:
                    Console.WriteLine("Requesting camera permission...");
                    bool granted = await RequestCameraPermissionAsync();
                    Console.WriteLine($"Camera permission granted: {granted}");
                    return granted;
                    
                case CameraPermissionStatus.Denied:
                case CameraPermissionStatus.Restricted:
                    Console.WriteLine("Camera permission denied. Please grant camera access in System Preferences > Security & Privacy > Camera");
                    return false;
                    
                default:
                    return false;
            }
        }

        private static bool IsMainThread()
        {
            try
            {
                return objc_msgSend(objc_getClass("NSThread"), sel_registerName("isMainThread")) == (IntPtr)1;
            }
            catch
            {
                return true; // Assume main thread if we can't check
            }
        }

        private static IntPtr GetAVMediaTypeVideo()
        {
            // AVMediaTypeVideo is defined as the FourCharCode 'vide' in AVFoundation
            // This corresponds to the NSString constant AVMediaTypeVideo
            return CreateNSString("vide");
        }

        #region macOS Native Interop

        [DllImport("/System/Library/Frameworks/Foundation.framework/Foundation")]
        private static extern IntPtr objc_getClass(string className);

        [DllImport("/System/Library/Frameworks/Foundation.framework/Foundation")]
        private static extern IntPtr sel_registerName(string selectorName);

        [DllImport("/System/Library/Frameworks/Foundation.framework/Foundation")]
        private static extern IntPtr objc_msgSend(IntPtr receiver, IntPtr selector);

        [DllImport("/System/Library/Frameworks/Foundation.framework/Foundation")]
        private static extern IntPtr objc_msgSend(IntPtr receiver, IntPtr selector, IntPtr arg1);

        [DllImport("/System/Library/Frameworks/Foundation.framework/Foundation")]
        private static extern IntPtr objc_msgSend(IntPtr receiver, IntPtr selector, string arg1);

        [DllImport("/System/Library/Frameworks/Foundation.framework/Foundation")]
        private static extern IntPtr objc_msgSend(IntPtr receiver, IntPtr selector, IntPtr arg1, IntPtr arg2);

        private static IntPtr CreateNSString(string str)
        {
            var nsStringClass = objc_getClass("NSString");
            var allocSelector = sel_registerName("alloc");
            var initSelector = sel_registerName("initWithUTF8String:");
            
            var nsString = objc_msgSend(nsStringClass, allocSelector);
            return objc_msgSend(nsString, initSelector, str);
        }

        #endregion
    }
} 