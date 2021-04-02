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
    public class Program
    {
        static async Task Main(string[] args)
        {
            await Host.CreateDefaultBuilder(args)
                .ConfigureHostConfiguration(configHost => {
                    // Configuration files and settings
                    configHost.AddJsonFile("appsettings.json", optional: true, reloadOnChange: true);
                    configHost.AddEnvironmentVariables();
                    configHost.AddCommandLine(args);
                }).ConfigureServices((hostContext, services) => {
                    // Always include the console logs
                    services.AddLogging(builder =>
                    {
                        // Excessive logging limits
                        builder.SetMinimumLevel(LogLevel.Trace);
                        builder.AddConsole();
                    });

                    // Setup the actual function we will be running
                    services.AddHostedService<ProcessHandler>();
                }).RunConsoleAsync();
        }
    }
}
