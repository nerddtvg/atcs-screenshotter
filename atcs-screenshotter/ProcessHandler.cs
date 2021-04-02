using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

// Console logging
using Microsoft.Extensions.Logging;

// Tasks
using System.Threading.Tasks;

// ConcurrentBag
using System.Collections.Concurrent;

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
        public string id {get;set;}
        public string processName {get;set;}
        public string windowTitle {get;set;}
        public string blobName {get;set;}
    }

    struct TimerState {
        public ATCSConfiguration configuration;
        public CancellationToken cancellationToken;
    }

    public class ProcessHandler : IHostedService, IDisposable
    {
        private readonly ILogger _logger;
        private readonly IConfiguration _configuration;
        private readonly IHostApplicationLifetime _appLifetime;

        private List<System.Threading.Timer> _timer;

        private readonly string _ATCSConfigurationName = "ATCSConfiguration";
        private List<ATCSConfiguration> _ATCSConfigurations;

        // How often in milliseconds we should wait
        private int _frequency = 5000;

        public ProcessHandler(ILogger<ProcessHandler> logger, IConfiguration configuration, IHostApplicationLifetime appLifetime)
        {
            this._logger = logger;
            this._configuration = configuration;
            this._appLifetime = appLifetime;
            this._ATCSConfigurations = null;
        }

        private bool IsValidConfiguration(ATCSConfiguration config) =>
            (!string.IsNullOrWhiteSpace(config.id) && !string.IsNullOrWhiteSpace(config.processName) && !string.IsNullOrWhiteSpace(config.windowTitle) && !string.IsNullOrWhiteSpace(config.blobName));

        public Task StartAsync(CancellationToken cancellationToken)
        {
            // Get our configuration information
            this._ATCSConfigurations = this._configuration.GetSection(this._ATCSConfigurationName).Get<List<ATCSConfiguration>>();

            // Remove any bad configurations
            if (this._ATCSConfigurations != null) {
                var remove = this._ATCSConfigurations.Where(a => !IsValidConfiguration(a)).ToList();
                remove.ForEach(a => {
                    this._logger.LogWarning($"Invalid {this._ATCSConfigurationName} configuration: {System.Text.Json.JsonSerializer.Serialize(a, typeof(ATCSConfiguration))}");
                    this._ATCSConfigurations.Remove(a);
                });
            }

            // Make sure we have good configurations
            if (this._ATCSConfigurations == null || this._ATCSConfigurations.Count == 0)
                throw new Exception($"No {this._ATCSConfigurationName} configuration found.");

            // Log this information
            this._logger.LogInformation($"Valid {this._ATCSConfigurationName} section found, count: {this._ATCSConfigurations.Count}");
            this._logger.LogDebug($"Valid Configurations: {System.Text.Json.JsonSerializer.Serialize(this._ATCSConfigurations, typeof(List<ATCSConfiguration>))}");
            
            // We need to start tasks for each of the processes we have
            this._timer = new List<System.Threading.Timer>();

            // Go through and start the tasks
            this._ATCSConfigurations.ForEach(a => {
                var state = new TimerState() { configuration = a, cancellationToken = cancellationToken };
                this._timer.Add(new System.Threading.Timer(CaptureProcess, state, TimeSpan.Zero, TimeSpan.FromMilliseconds(this._frequency)));
            });

            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Timed Hosted Service is stopping.");

            _timer?.ForEach(a => a.Change(Timeout.Infinite, 0));

            return Task.CompletedTask;
        }

        public void Dispose()
        {
            _timer?.ForEach(a => a.Dispose());
        }

        private void CaptureProcess(object state)
        {
            var configuration = ((TimerState) state).configuration;
            var cancellationToken = ((TimerState) state).cancellationToken;

            if (!cancellationToken.IsCancellationRequested) {
                // Need to get all of our processes
                this._logger.LogDebug($"Searching for process '{configuration.processName}' with window title '{configuration.windowTitle}' for configuration '{configuration.id}'");

                // Find the pointer(s) for this window
                var ptrs = WindowFilter.FindWindowsWithText(configuration.windowTitle, true).ToList();

                // If we have more than one, we need to fail out here
                if (ptrs.Count > 1) {
                    this._logger.LogWarning($"Found multiple windows for '{configuration.id}', unable to proceed.");
                } else if (ptrs.Count == 0) {
                    this._logger.LogWarning($"Found no windows for '{configuration.id}', unable to proceed.");
                } else {
                    try {
                        // Capture this and save it
                        this._logger.LogDebug($"Capturing window with handle '{ptrs[0].ToString()} for configuration '{configuration.id}'.");
                        var img = CaptureWindow(ptrs[0]);

                        if (img == null)
                            throw new Exception("Received zero bytes for screenshot.");
                        
                        // Save it
                        SaveImage(img, $"{configuration.blobName}.png", ImageFormat.Png);
                        this._logger.LogDebug($"File '{configuration.blobName}.png' saved for configuration '{configuration.id}'.");
                    } catch (Exception e) {
                        this._logger.LogError(e, $"Exception thrown while capturing the window for '{configuration.windowTitle}': {e.Message}");
                        return;
                    }
                }
            }
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
