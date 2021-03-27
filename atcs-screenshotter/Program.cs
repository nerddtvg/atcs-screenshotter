﻿using System;
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
    public class Program
    {
        protected static string processName = "atcsmon";

        protected static Dictionary<string, string> windowTitles = new Dictionary<string, string>() {
            { "Terminal Railroad Association", "trra" }
        };

        static async Task Main(string[] args)
        {
            // This is a basic Console application that will run and send the email
            // It exits immediately and execution is scheduled by the settings.job file
            await Host.CreateDefaultBuilder(args)
                .ConfigureHostConfiguration(configHost => {
                    // We should set this up four levels to the web app settings
                    // $(PublishDir)App_Data/Jobs/triggered/Security
                    var dir = new System.IO.DirectoryInfo(System.IO.Directory.GetCurrentDirectory());
                    for(var i=0; i<4 && dir != null; i++)
                        dir = dir.Parent;

                    // If we got to a parent folder
                    if (dir != null)
                        configHost.SetBasePath(dir.FullName);

                    // Configuration files and settings
                    configHost.AddJsonFile("appsettings.json", optional: true, reloadOnChange: true);
                    configHost.AddEnvironmentVariables();
                    configHost.AddCommandLine(args);
                }).ConfigureServices((hostContext, services) => {
                    // Always include the console logs
                    services.AddLogging(builder =>
                    {
                        builder.AddConsole();
                    });

                    // Setup the actual function we will be running
                    services.AddHostedService<SecurityWebjob>();
                }).RunConsoleAsync();
        }

        static void Main(string[] args)
        {
            // Need to get all of our processes
            var processes = Process.GetProcessesByName(processName).Where(a => a.MainWindowHandle.ToString() != "0").ToList();
            
            // Did we get results
            if (processes.Count == 0) {
                Console.WriteLine($"Unable to identify '{processName}' process.");
                return;
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
