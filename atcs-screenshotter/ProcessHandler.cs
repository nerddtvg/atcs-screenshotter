using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

// Console logging
using Microsoft.Extensions.Logging;

// Tasks
using System.Threading.Tasks;

// CancellationToken
using System.Threading;

// Dependency Injection
// https://docs.microsoft.com/en-us/azure/azure-functions/functions-dotnet-dependency-injection
using Microsoft.Extensions.DependencyInjection;

// Configuration file (if any)
using Microsoft.Extensions.Configuration;

// IHostEnvironment
using Microsoft.Extensions.Hosting;

// DLL Import
using System.Runtime.InteropServices;

// StringBuilder
using System.Text;

// MemoryStream
using System.IO;

// Capturing the window
using System.Drawing;
using System.Drawing.Imaging;
using System.Windows.Forms;

// Processes
using System.Diagnostics;

using PInvoke;

namespace atcs_screenshotter
{
    public class ProcessHandler : IHostedService
    {
        protected static string processName = "atcsmon";

        protected static Dictionary<string, string> windowTitles = new Dictionary<string, string>() {
            { "Terminal Railroad Association", "trra" }
        };

        private readonly ILogger _logger;
        private readonly IConfiguration _configuration;

        public ProcessHandler(ILogger<ProcessHandler> logger, IConfiguration configuration)
        {
            this._logger = logger;
            this._configuration = configuration;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            // Need to get all of our processes
            var processes = Process.GetProcessesByName(processName).Where(a => a.MainWindowHandle.ToString() != "0").ToList();
            
            // Did we get results
            if (processes.Count == 0) {
                Console.WriteLine($"Unable to identify '{processName}' process.");
                return Task.CompletedTask;
            }

            foreach(var title in windowTitles) {
                // Find the pointer(s) for this window
                var ptrs = WindowFilter.FindWindowsWithText(title.Key, true).ToList();

                // If we have more than one, we need to fail out here
                if (ptrs.Count > 1) {
                    Console.WriteLine($"Found multiple windows for '{title.Key}', unable to proceed.");
                } else if (ptrs.Count == 0) {
                    Console.WriteLine($"Found no windows for '{title.Key}', unable to proceed.");
                } else {
                    try {
                        // Capture this and save it
                        var img = CaptureWindow(ptrs[0]);

                        if (img == null)
                            throw new Exception("Received zero bytes for screenshot.");
                        
                        // Save it
                        SaveImage(img, $"{title.Value}.png", ImageFormat.Png);
                    } catch (Exception e) {
                        Console.WriteLine($"Exception thrown while capturing the window for '{title.Key}': {e.Message}");
                    }
                }
            }

            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        static void SaveImage(byte[] imgBytes, string filename, ImageFormat format) {
            using (var ms = new MemoryStream(imgBytes)) {
                using (var img = Image.FromStream(ms)) {
                    img.Save(filename, format);
                }
            }
        }

        static byte[] CaptureWindow(IntPtr handle) {
            RECT rct;

            // Use GetClientRect to get inside the window border
            if (!User32.GetClientRect(handle, out rct)) {
                throw new Exception("Unable to get window size.");
            }

            // Determine the width
            int width = rct.right - rct.left;
            int height = rct.bottom - rct.top;

            using(Bitmap bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb)) {
                using(Graphics memoryGraphics = Graphics.FromImage(bmp)) {
                    IntPtr dc = memoryGraphics.GetHdc();

                    // Grab the screenshot
                    if (!User32.PrintWindow(handle, dc, User32.PrintWindowFlags.PW_CLIENTONLY)) {
                        throw new Exception("Unable to capture the window screenshot.");
                    }

                    memoryGraphics.ReleaseHdc(dc);

                    // Convert to an image
                    using (var ms = new MemoryStream()) {
                        // Convert to a byte[]
                        // We must specify a format here (not RawImage) because it is a MemoryBmp which cannot be converted directly
                        bmp.Save(ms, ImageFormat.Png);

                        // Return the data
                        return ms.ToArray();
                    }
                }
            }
        }
    }
}
