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
        public bool saveFile {get;set;} = true;

        // Should we automatically start the ATCS Monitor application?
        public bool autoStart {get;set;} = false;
        public string profile {get;set;}

        // These are used by our program and not for configurations
        public Process _process {get;set;}
        public int _attempts {get;set;} = 0;
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

        // Due to the expanding time it takes to run each sequence, we need to enable locking so we don't accidentally overlap
        private Dictionary<string, object> _locks = new Dictionary<string, object>();

        // The actual configurations of what screenshots to capture
        private List<ATCSConfiguration> _ATCSConfigurations;

        // How often in milliseconds we should wait
        private int _frequency = 5000;
        private int _minFrequency = 1000;

        // Process/Path information
        private bool _canLaunch = false;
        private string _ATCSPath;
        private string _ATCSExecutable;
        private string _ATCSFullPath;
        // How long do we wait after starting the process to search for a window?
        private int _processLaunchTime = 5000;
        // How many times will we attempt to launch this process?
        private int _maxProcessAttempts = 5;

        // Image type handling, these should match at all times
        private readonly ImageFormat _ImageFormat = ImageFormat.Png;
        private readonly string _ImageMime = "image/png";
        private string _ImageExt;

        // Azure Storage uploads
        private bool _enableUpload = false;
        private AzureStorageConfiguration _AzureStorageConfiguration;
        private readonly string _defaultStorageSuffix = "core.windows.net";
        private BlobServiceClient _blobServiceClient;
        private BlobContainerClient _blobContainerClient;

        // Maximum wait time to upload files
        // Keeps things moving if it stalls out
        internal int _maxUploadTime = 5000;

        public ProcessHandler(ILogger<ProcessHandler> logger, IConfiguration configuration, IHostApplicationLifetime appLifetime)
        {
            this._logger = logger;
            this._configuration = configuration;
            this._appLifetime = appLifetime;
            this._ATCSConfigurations = null;

            // Do we have a frequency?
            try {
                // Get the value
                var freqName = nameof(this._frequency).Replace("_", "");
                var tempFrequency = this._configuration.GetValue<int>(freqName, this._frequency);

                // Did we go below the threshold
                if (tempFrequency <= this._minFrequency) {
                    this._logger.LogWarning(new ArgumentOutOfRangeException(freqName), $"Setting '{freqName}' is set too low, minimum is '{this._minFrequency}");
                    tempFrequency = this._minFrequency;
                }
                
                // Set the value
                this._frequency = tempFrequency;

                this._logger.LogDebug($"Screenshot frequency: {this._frequency} ms");
            } catch {}

            // Configure our image extension
            this._ImageExt = this._ImageFormat.ToString().ToLower();
            if (!string.IsNullOrEmpty(this._ImageExt))
                this._ImageExt = $".{this._ImageExt}";
            
            // Determine if we can actually launch these or not
            CheckATCSInstallation();

            // Setup Azure Storage
            ParseStorageConfiguration();
        }

        internal void CheckATCSInstallation() {
            var pathName = nameof(this._ATCSPath).Replace("_", "");
            var execName = nameof(this._ATCSExecutable).Replace("_", "");

            this._ATCSPath = this._configuration.GetValue<string>(pathName);
            this._ATCSExecutable = this._configuration.GetValue<string>(execName);

            if (string.IsNullOrEmpty(this._ATCSPath)) {
                this._logger.LogWarning($"No {pathName} provided for auto start. Auto starts disabled.");
                return;
            }

            if (string.IsNullOrEmpty(this._ATCSExecutable)) {
                this._logger.LogWarning($"No {execName} provided for auto start. Auto starts disabled.");
                return;
            }

            // Check that this is a valid path
            try {
                this._ATCSFullPath = System.IO.Path.Combine(this._ATCSPath, this._ATCSExecutable);

                if (!System.IO.File.Exists(this._ATCSFullPath))
                    throw new FileNotFoundException("Unable to find the ATCS executable.", this._ATCSFullPath);
            } catch (Exception e) {
                this._logger.LogError(e, $"Unable to validate the given path for auto start. Auto starts disabled.");
                return;
            }

            this._canLaunch = true;
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

        private bool IsValidConfiguration(ATCSConfiguration config) {
            var mainCheck = 
            (
                !string.IsNullOrWhiteSpace(config.id)
                &&
                !string.IsNullOrWhiteSpace(config.processName)
                &&
                !string.IsNullOrWhiteSpace(config.windowTitle)
                &&
                !string.IsNullOrWhiteSpace(config.blobName)
            );

            // The main checks need to pass first
            if (!mainCheck) return false;

            if (config.autoStart == true) {
                // Make sure we have a profile and the file exists
                if (string.IsNullOrWhiteSpace(config.profile)) {
                    this._logger.LogError($"Configuration '{config.id}' has empty or is missing '{nameof(config.profile)}' required for auto start");
                    return false;
                }

                var path = System.IO.Path.Combine(new System.IO.FileInfo(this._ATCSFullPath).Directory.FullName, config.profile);

                if (!System.IO.File.Exists(path)) {
                    var msg = $"Unable to find the profile '{config.profile}' for '{config.id}'.";
                    this._logger.LogError(new FileNotFoundException(msg, path), msg);
                    return false;
                }
            }

            return true;
        }

        protected override Task ExecuteAsync(CancellationToken cancellationToken)
        {
            // Get our configuration information
            this._ATCSConfigurations = this._configuration.GetSection(nameof(ATCSConfiguration)).Get<List<ATCSConfiguration>>();

            // Remove any bad configurations
            if (this._ATCSConfigurations != null) {
                var remove = this._ATCSConfigurations.Where(a => !IsValidConfiguration(a)).ToList();
                remove.ForEach(a => {
                    this._logger.LogWarning($"Invalid {nameof(ATCSConfiguration)} configuration: {System.Text.Json.JsonSerializer.Serialize(a, typeof(ATCSConfiguration))}");
                    this._ATCSConfigurations.Remove(a);
                });

                // Check if we have any that have duplicate ids or blobNames
                var ids = this._ATCSConfigurations
                    .Select(a => a.id)
                    .GroupBy(a => a)
                    .Where(a => a.Count() > 1)
                    .Select(a => a.Key)
                    .ToList();
                
                remove = this._ATCSConfigurations.Where(a => ids.Contains(a.id)).ToList();
                remove.ForEach(a => {
                    this._logger.LogWarning($"Duplicate {nameof(ATCSConfiguration)} configuration '{nameof(ATCSConfiguration.id)}': {System.Text.Json.JsonSerializer.Serialize(a, typeof(ATCSConfiguration))}");
                    this._ATCSConfigurations.Remove(a);
                });

                // blobNames?
                var blobNames = this._ATCSConfigurations
                    .Select(a => a.blobName)
                    .GroupBy(a => a)
                    .Where(a => a.Count() > 1)
                    .Select(a => a.Key)
                    .ToList();
                
                remove = this._ATCSConfigurations.Where(a => blobNames.Contains(a.blobName)).ToList();
                remove.ForEach(a => {
                    this._logger.LogWarning($"Duplicate {nameof(ATCSConfiguration)} configuration '{nameof(ATCSConfiguration.blobName)}': {System.Text.Json.JsonSerializer.Serialize(a, typeof(ATCSConfiguration))}");
                    this._ATCSConfigurations.Remove(a);
                });
            }

            // Make sure we have good configurations
            if (this._ATCSConfigurations == null || this._ATCSConfigurations.Count == 0)
                throw new Exception($"No {nameof(ATCSConfiguration)} configuration found.");

            // Log this information
            this._logger.LogInformation($"Valid {nameof(ATCSConfiguration)} section found, count: {this._ATCSConfigurations.Count}");
            this._logger.LogDebug($"Valid Configurations: {System.Text.Json.JsonSerializer.Serialize(this._ATCSConfigurations, typeof(List<ATCSConfiguration>))}");
            
            // We need to start tasks for each of the processes we have
            this._timer = new List<System.Threading.Timer>();

            // Go through and start the tasks
            this._ATCSConfigurations.ForEach(a => {
                // Create the lock object
                this._locks.Add(a.id, new object());

                var state = new TimerState() { configuration = a, cancellationToken = cancellationToken };
                this._timer.Add(new System.Threading.Timer(CaptureProcess, state, TimeSpan.Zero, TimeSpan.FromMilliseconds(this._frequency)));
            });

            return Task.CompletedTask;
        }

        public override Task StopAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Timed Hosted Service is stopping.");

            _timer?.ForEach(a => a.Change(Timeout.Infinite, 0));

            // Kill off any child processes we launched
            this._ATCSConfigurations.ForEach(a => {
                if (a._process != null) {
                    if (!a._process.HasExited)
                        a._process.Kill();

                    a._process.Dispose();
                }
            });

            return base.StopAsync(stoppingToken);
        }

        public override void Dispose()
        {
            _timer?.ForEach(a => a.Dispose());
            this._ATCSConfigurations.ForEach(a => {
                if (a._process != null) a._process.Dispose();
            });

            base.Dispose();
        }

        private async void CaptureProcess(object state)
        {
            var configuration = ((TimerState) state).configuration;
            var cancellationToken = ((TimerState) state).cancellationToken;

            if (!cancellationToken.IsCancellationRequested && Monitor.TryEnter(this._locks[configuration.id])) {
                try {
                    // Need to get all of our processes
                    this._logger.LogDebug($"Searching for process '{configuration.processName}' with window title '{configuration.windowTitle}' for configuration '{configuration.id}'");

                    // Find the pointer(s) for this window
                    var ptrs = WindowFilter.FindWindowsWithText(configuration.windowTitle, true).ToList();

                    // We will attempt to auto start if we have been provided the right information
                    if (ptrs.Count == 0 && configuration.autoStart && this._canLaunch) {
                        // Attempt to launch the application and wait 10 seconds to continue

                        // If we have already launched this application, do we have a problem?
                        if (configuration._process != null) {
                            if (configuration._process.HasExited) {
                                if (configuration._attempts >= this._maxProcessAttempts) {
                                    this._logger.LogError($"Auto started process for configuration '{configuration.id}' has exited prematurely and failed too many times. Disabling auto start.");
                                    configuration.autoStart = false;
                                    return;
                                }

                                this._logger.LogError($"Auto started process for configuration '{configuration.id}' has exited prematurely. Will re-attempt.");
                            } else {
                                // So we have launched a process but we don't have a window, this is a failure
                                configuration._process.Kill();

                                if (configuration._attempts >= this._maxProcessAttempts) {
                                    this._logger.LogError($"Auto started process for configuration '{configuration.id}' was launched but no corresponding window found. Process '{configuration._process.Id}' killed. Disabling auto start");
                                    configuration.autoStart = false;
                                    return;
                                }

                                this._logger.LogError($"Auto started process for configuration '{configuration.id}' was launched but no corresponding window found. Process '{configuration._process.Id}' killed. Will re-attempt.");
                            }

                            configuration._process.Dispose();
                        }

                        // The process expects just the profile file name that exists in its own directory
                        try {
                            // Increment our counter
                            configuration._attempts++;

                            this._logger.LogDebug($"Launching process '{this._ATCSFullPath}' with profile '{configuration.profile}' for configuration '{configuration.id}' (Attempt {configuration._attempts} of {this._maxProcessAttempts})");
                            configuration._process = Process.Start(this._ATCSFullPath, new List<string>() { configuration.profile });

                            // If we have a null response, it failed to start
                            if (configuration._process == null)
                                throw new Exception("Error auto starting the process but no exception provided.");

                            await Task.Delay(this._processLaunchTime);

                            // Attempt to find the windows again
                            ptrs = WindowFilter.FindWindowsWithText(configuration.windowTitle, true).ToList();

                            // We don't check agian because we will do it below. We will also give this one more timer interval before failing it out with the above code
                        } catch (Exception e) {
                            this._logger.LogError(e, $"Unable to auto start the process for configuration '{configuration.id}', auto start disabled.");
                            configuration.autoStart = false;
                        }
                    }

                    // If we have more than one, we need to fail out here
                    if (ptrs.Count > 1) {
                        this._logger.LogWarning($"Found multiple windows for '{configuration.id}', unable to proceed.");
                    } else if (ptrs.Count == 0) {
                        this._logger.LogWarning($"Found no windows for '{configuration.id}', unable to proceed.");
                    } else {
                        try {
                            // Capture this and save it
                            this._logger.LogDebug($"Capturing window with handle '{ptrs[0].ToString()}' for configuration '{configuration.id}'.");
                            var img = CaptureWindow(ptrs[0]);

                            if (img == null)
                                throw new Exception("Received zero bytes for screenshot.");
                            
                            // Determine the filename
                            // Keep these separate for now, blob may change down the road
                            var blobPath = $"{configuration.blobName}{this._ImageExt}".Trim();
                            var filePath = $"{configuration.blobName}{this._ImageExt}".Trim();
                            
                            // Upload it to Azure
                            if (this._enableUpload) {
                                using (var ms = new MemoryStream(img)) {
                                    using(var ctx = new CancellationTokenSource()) {
                                        // Set our maximum upload time
                                        ctx.CancelAfter(this._maxUploadTime);

                                        try {
                                            var blobClient = this._blobContainerClient.GetBlobClient(blobPath);
                                            await blobClient.UploadAsync(ms, true, ctx.Token);
                                            
                                            var headers = new BlobHttpHeaders();
                                            headers.ContentDisposition = "inline";
                                            headers.ContentType = this._ImageMime;
                                            blobClient.SetHttpHeaders(headers);
                                            
                                            this._logger.LogInformation($"Blob '{blobPath}' saved for configuration '{configuration.id}'.");
                                        } catch (TaskCanceledException e) {
                                            this._logger.LogError(e, $"Blob '{blobPath}' took too long to upload, cancelled.");
                                        }
                                    }
                                }
                            } else {
                                this._logger.LogDebug("Upload skipped due to configuration.");
                            }
                            
                            // Save it
                            if (configuration.saveFile) {
                                SaveImage(img, filePath, this._ImageFormat);
                                this._logger.LogDebug($"File '{filePath}' saved for configuration '{configuration.id}'.");
                            } else {
                                this._logger.LogDebug("Save file skipped due to configuration.");
                            }
                        } catch (Exception e) {
                            this._logger.LogError(e, $"Exception thrown while capturing the window for '{configuration.windowTitle}': {e.Message}");
                        }
                    }
                } catch (Exception e) {
                    this._logger.LogError(e, $"Uncaught exception in {nameof(CaptureProcess)}.");
                } finally {
                    Monitor.Exit(this._locks[configuration.id]);
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
