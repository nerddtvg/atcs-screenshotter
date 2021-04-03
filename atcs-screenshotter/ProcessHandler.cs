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

// Azure Storage
using Azure.Storage;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Sas;

namespace atcs_screenshotter
{
    public class ATCSConfiguration {
        public string id {get;set;}
        public string processName {get;set;}
        public string windowTitle {get;set;}
        public string blobName {get;set;}
    }

    public class AzureStorageConfiguration {
        // Storage Account Name
        public string accountName {get;set;}
        // Storage Account Suffix, if required
        public string suffix {get;set;}
        // Connection String to use
        public string connectionString {get;set;}
        // Access Key to use if no connection string
        public string accessKey {get;set;}
        // Container name [REQUIRED]
        public string containerName {get;set;}
    }

    struct TimerState {
        public ATCSConfiguration configuration;
        public CancellationToken cancellationToken;
    }

    public class ProcessHandler : BackgroundService
    {
        private readonly ILogger _logger;
        private readonly IConfiguration _configuration;
        private readonly IHostApplicationLifetime _appLifetime;

        private List<System.Threading.Timer> _timer;

        private readonly string _ATCSConfigurationName = "ATCSConfiguration";
        private List<ATCSConfiguration> _ATCSConfigurations;

        // How often in milliseconds we should wait
        private int _frequency = 5000;

        // Image type handling, these should match at all times
        private readonly ImageFormat _ImageFormat = ImageFormat.Png;
        private readonly string _ImageMime = "image/png";

        // Azure Storage uploads
        private bool _enableUpload = false;
        private AzureStorageConfiguration _AzureStorageConfiguration;
        private readonly string _defaultStorageSuffix = "core.windows.net";
        private BlobServiceClient _blobServiceClient;
        private BlobContainerClient _blobContainerClient;
        private string _containerName = "atcs";

        public ProcessHandler(ILogger<ProcessHandler> logger, IConfiguration configuration, IHostApplicationLifetime appLifetime)
        {
            this._logger = logger;
            this._configuration = configuration;
            this._appLifetime = appLifetime;
            this._ATCSConfigurations = null;

            // Setup Azure Storage
            ParseStorageConfiguration();
        }

        internal void ParseStorageConfiguration() {
            // This is used to see if we have valid Azure configurations and if so, enable uploads
            this._AzureStorageConfiguration = this._configuration.GetSection(nameof(AzureStorageConfiguration)).Get<AzureStorageConfiguration>();

            // If we have nothing, skip
            if (this._AzureStorageConfiguration == null) {
                this._logger.LogWarning($"No {nameof(AzureStorageConfiguration)} provided for uploads. Uploads disabled.");
                return;
            }

            // If we don't have a container name, move on
            if (string.IsNullOrEmpty(this._AzureStorageConfiguration.containerName)) {
                this._logger.LogError(new ArgumentNullException(nameof(AzureStorageConfiguration) + ":" + nameof(this._AzureStorageConfiguration.containerName)), "No Container Name specified in the storage configuration.");
                return;
            }

            try {
                // If we have a connection string, run with that
                if (!string.IsNullOrEmpty(this._AzureStorageConfiguration.connectionString)) {
                    // A connection string should create the client directly
                    this._blobServiceClient = new BlobServiceClient(this._AzureStorageConfiguration.connectionString);
                } else if (!string.IsNullOrEmpty(this._AzureStorageConfiguration.accountName) && !string.IsNullOrEmpty(this._AzureStorageConfiguration.accessKey)) {
                    var credential = new StorageSharedKeyCredential(this._AzureStorageConfiguration.accountName, this._AzureStorageConfiguration.accessKey);

                    // Check for the suffix
                    var suffix = !string.IsNullOrEmpty(this._AzureStorageConfiguration.suffix) ? this._AzureStorageConfiguration.suffix : this._defaultStorageSuffix;

                    // Make sure we trim off the beginning period if it exists
                    if (suffix.Substring(0, 1) == ".")
                        suffix = suffix.Substring(1);

                    // Build the URI
                    var blobUri = new UriBuilder();
                    blobUri.Scheme = "https";
                    blobUri.Host = $"{this._AzureStorageConfiguration.accountName}.blob.{suffix}";

                    // Create the client
                    this._blobServiceClient = new BlobServiceClient(blobUri.Uri, credential);
                } else {
                    this._logger.LogError(new ArgumentNullException(nameof(AzureStorageConfiguration)), "No Connection String or no valid combination of Account Name and Access Key provided in the storage configuration.");
                    return;
                }

                // Test the account
                this._blobServiceClient.GetProperties();
            } catch (Exception e) {
                this._logger.LogError(e, "Unable to create the BlobServiceClient");
                return;
            }

            // Now we need to create the BlobContainerClient
            try {
                this._blobContainerClient = this._blobServiceClient.GetBlobContainerClient(this._AzureStorageConfiguration.containerName);

                // Check that our container exists
                this._blobContainerClient.CreateIfNotExists();

                // Double check that worked and didn't throw an error
                if (!this._blobContainerClient.Exists())
                    throw new Exception("Unable to create the container but no Azure error thrown.");
            } catch (Exception e) {
                this._logger.LogError(e, "Unable to create the BlobContainerClient or could not create the container.");
                return;
            }

            this._enableUpload = true;
        }

        private bool IsValidConfiguration(ATCSConfiguration config) =>
            (!string.IsNullOrWhiteSpace(config.id) && !string.IsNullOrWhiteSpace(config.processName) && !string.IsNullOrWhiteSpace(config.windowTitle) && !string.IsNullOrWhiteSpace(config.blobName));

        protected override Task ExecuteAsync(CancellationToken cancellationToken)
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

        public override Task StopAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Timed Hosted Service is stopping.");

            _timer?.ForEach(a => a.Change(Timeout.Infinite, 0));

            return base.StopAsync(stoppingToken);
        }

        public override void Dispose()
        {
            _timer?.ForEach(a => a.Dispose());
            base.Dispose();
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
                        
                        // Upload it to Azure
                        if (this._enableUpload) {
                            using (var ms = new MemoryStream(img)) {
                                var blobClient = this._blobContainerClient.GetBlobClient($"{configuration.blobName}.png");
                                blobClient.Upload(ms, true);

                                var headers = new BlobHttpHeaders();
                                headers.ContentDisposition = "inline";
                                headers.ContentType = this._ImageMime;
                                blobClient.SetHttpHeaders(headers);

                                var sasBuilder = new BlobSasBuilder(BlobSasPermissions.Read, DateTimeOffset.UtcNow.AddDays(1)) {
                                    BlobContainerName = this._containerName,
                                    BlobName = blobClient.Name,
                                    ContentDisposition = new System.Net.Http.Headers.ContentDispositionHeaderValue("inline").ToString(),
                                    StartsOn = DateTime.UtcNow.AddDays(-1)
                                };

                                this._logger.LogDebug(blobClient.GenerateSasUri(sasBuilder).ToString());
                            }
                        } else {
                            this._logger.LogDebug("Upload skipped due to configuration.");
                        }
                        
                        // Save it
                        SaveImage(img, $"{configuration.blobName}.png", this._ImageFormat);
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
