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
    public class ATCSConfiguration {
        public string processName {get;set;}
        public string windowTitle {get;set;}
        public string blobName {get;set;}
    }

    public class ProcessHandler : IHostedService
    {
        private readonly ILogger _logger;
        private readonly IConfiguration _configuration;
        private readonly IHostApplicationLifetime _appLifetime;

        private readonly string _ATCSConfigurationName = "ATCSConfiguration";
        private List<ATCSConfiguration> _ATCSConfigurations;

        public ProcessHandler(ILogger<ProcessHandler> logger, IConfiguration configuration, IHostApplicationLifetime appLifetime)
        {
            this._logger = logger;
            this._configuration = configuration;
            this._appLifetime = appLifetime;
            this._ATCSConfigurations = null;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            this._ATCSConfigurations = this._configuration.GetSection(_ATCSConfigurationName).Get<List<ATCSConfiguration>>();

            if (this._ATCSConfigurations == null || this._ATCSConfigurations.Count == 0)
                throw new Exception($"No {this._ATCSConfigurationName} configuration found.");

            var first = this._ATCSConfigurations.First();

            // Need to get all of our processes
            var processes = Process.GetProcessesByName(first.processName).Where(a => a.MainWindowHandle.ToString() != "0").ToList();
            
            // Did we get results
            if (processes.Count == 0) {
                Console.WriteLine($"Unable to identify '{first.processName}' process.");
                return Task.CompletedTask;
            }

            // Find the pointer(s) for this window
            var ptrs = WindowFilter.FindWindowsWithText(first.windowTitle, true).ToList();

            // If we have more than one, we need to fail out here
            if (ptrs.Count > 1) {
                Console.WriteLine($"Found multiple windows for '{first.windowTitle}', unable to proceed.");
            } else if (ptrs.Count == 0) {
                Console.WriteLine($"Found no windows for '{first.windowTitle}', unable to proceed.");
            } else {
                try {
                    // Capture this and save it
                    var img = CaptureWindow(ptrs[0]);

                    if (img == null)
                        throw new Exception("Received zero bytes for screenshot.");
                    
                    // Save it
                    SaveImage(img, $"{first.blobName}.png", ImageFormat.Png);
                } catch (Exception e) {
                    Console.WriteLine($"Exception thrown while capturing the window for '{first.windowTitle}': {e.Message}");
                }
            }

            this._appLifetime.StopApplication();

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
