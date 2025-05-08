using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Autodesk.Revit.UI;
using CloudAPISample.Samples.Migration;
using Revit.SDK.Samples.CloudAPISample.CS.APS;
using Revit.SDK.Samples.CloudAPISample.CS.Coroutine;
using Revit.SDK.Samples.CloudAPISample.CS.View;

namespace Revit.SDK.Samples.CloudAPISample.CS.Migration
{
    /// <summary>
    /// Manages the migration process between ACC hubs
    /// </summary>
    public class MigrationManager : SampleContext
    {
        private ViewMigrationToBim360 _view;
        private RevitFileProcessor _fileProcessor;
        private bool _isInitialized = false;

        // Settings and state
        private string _localDirectory;
        private Guid _sourceAccountId;
        private Guid _sourceProjectId;
        private string _sourceFolderId;
        private Guid _destAccountId;
        private Guid _destProjectId;
        private string _destFolderId;

        /// <summary>
        /// Model for binding UI elements and storing migration configuration
        /// </summary>
        public MigrationModel Model { get; } = new MigrationModel();

        /// <summary>
        /// Constructor
        /// </summary>
        public MigrationManager()
        {
            // Pass 'this' to ViewMigrationToBim360 but cast to the expected type
            var dummyContext = new MigrationToBim360();
            _view = new ViewMigrationToBim360(dummyContext);
            View = _view;
        }

        /// <summary>
        /// Initialize components
        /// </summary>
        public void Initialize()
        {
            if (_isInitialized)
                return;

            if (Application != null)
            {
                _fileProcessor = new RevitFileProcessor(Application);
                _isInitialized = true;
            }
        }

        /// <summary>
        /// Terminate this context
        /// </summary>
        public override void Terminate()
        {
            View = null;
            _view = null;
            _fileProcessor = null;
            _isInitialized = false;
        }

        /// <summary>
        /// Set source and destination parameters
        /// </summary>
        public void SetParameters(
            string localDir,
            Guid sourceAccountId, Guid sourceProjectId, string sourceFolderId,
            Guid destAccountId, Guid destProjectId, string destFolderId)
        {
            _localDirectory = localDir;
            _sourceAccountId = sourceAccountId;
            _sourceProjectId = sourceProjectId;
            _sourceFolderId = sourceFolderId;
            _destAccountId = destAccountId;
            _destProjectId = destProjectId;
            _destFolderId = destFolderId;
        }

        /// <summary>
        /// Download files from ACC source
        /// </summary>
        public IEnumerator DownloadFiles()
        {
            Initialize();

            if (string.IsNullOrEmpty(_localDirectory) || !Directory.Exists(_localDirectory))
            {
                yield return "Local directory does not exist";
                yield break;
            }

            yield return "Starting download process...";

            // Variables declared outside try-catch to avoid yield restrictions
            string token = null;
            Exception authError = null;

            // Authentication phase
            try
            {
                token = TwoLeggedToken.GetToken();
            }
            catch (Exception ex)
            {
                authError = ex;
            }

            // Handle authentication results outside try-catch
            yield return "Authenticating...";

            if (authError != null)
            {
                yield return $"Error during authentication: {authError.Message}";
                yield break;
            }

            if (string.IsNullOrEmpty(token))
            {
                yield return "Failed to get authentication token";
                yield break;
            }

            // Get folder contents
            List<DataManagement.Item> items = null;
            Exception downloadError = null;
            int totalItems = 0;

            try
            {
                // Since we're in a coroutine, we can't use async/await directly
                // We need to use Task.Run and wait synchronously
                var task = GetFolderContentsAsync(_sourceProjectId.ToString(), _sourceFolderId);
                task.Wait();
                items = task.Result;
                totalItems = CountTotalItems(items);
            }
            catch (Exception ex)
            {
                downloadError = ex;
            }

            // Handle download results outside try-catch
            yield return "Starting recursive download...";

            if (downloadError != null)
            {
                yield return $"Error getting folder contents: {downloadError.Message}";
                yield break;
            }

            // Create download tracker for UI updates
            DownloadTracker tracker = new DownloadTracker
            {
                TotalItems = totalItems,
                ProcessedItems = 0,
                View = _view
            };

            // Process all items recursively
            var downloadCoroutine = DownloadItemsRecursive(
                _sourceProjectId.ToString(),
                items,
                _localDirectory,
                tracker);

            while (downloadCoroutine.MoveNext())
            {
                yield return downloadCoroutine.Current;
            }

            yield return "Download completed successfully";
        }

        /// <summary>
        /// Process Revit files in preparation for upload
        /// </summary>
        public IEnumerator ProcessFiles()
        {
            Initialize();

            if (string.IsNullOrEmpty(_localDirectory) || !Directory.Exists(_localDirectory))
            {
                yield return "Local directory does not exist";
                yield break;
            }

            yield return "Starting file processing...";

            // Variables for try-catch pattern
            string[] revitFiles = null;
            Exception searchError = null;

            try
            {
                revitFiles = Directory.GetFiles(_localDirectory, "*.rvt", SearchOption.AllDirectories);
            }
            catch (Exception ex)
            {
                searchError = ex;
            }

            // Handle results outside try-catch
            if (searchError != null)
            {
                yield return $"Error searching for files: {searchError.Message}";
                yield break;
            }

            if (revitFiles == null || revitFiles.Length == 0)
            {
                yield return "No Revit files found in the directory";
                yield break;
            }

            yield return $"Found {revitFiles.Length} Revit files";

            // Process each file
            int processed = 0;
            foreach (var filePath in revitFiles)
            {
                processed++;
                int progress = (int)(processed * 100.0 / revitFiles.Length);

                // Update UI
                if (_view != null)
                {
                    // Note: UpdateProcessingProgress method doesn't exist on ViewMigrationToBim360
                    // We'll use UpdateUploadingProgress instead
                    _view.UpdateUploadingProgress(
                        $"Processing {processed} of {revitFiles.Length}: {Path.GetFileName(filePath)}",
                        progress);
                }

                // Process the file
                var processCoroutine = _fileProcessor.ProcessRevitFile(filePath);
                while (processCoroutine.MoveNext())
                {
                    yield return processCoroutine.Current;
                }
            }

            // Save link information
            Exception saveError = null;
            try
            {
                _fileProcessor.SaveTrackingInformation(_localDirectory);
            }
            catch (Exception ex)
            {
                saveError = ex;
            }

            if (saveError != null)
            {
                yield return $"Error saving tracking information: {saveError.Message}";
            }

            yield return "Processing completed successfully";
        }

        /// <summary>
        /// Upload files to ACC destination
        /// </summary>
        public IEnumerator UploadFiles()
        {
            Initialize();

            if (string.IsNullOrEmpty(_localDirectory) || !Directory.Exists(_localDirectory))
            {
                yield return "Local directory does not exist";
                yield break;
            }

            yield return "Starting upload process...";

            // Variables for try-catch pattern
            string token = null;
            Exception authError = null;

            try
            {
                token = TwoLeggedToken.GetToken();
            }
            catch (Exception ex)
            {
                authError = ex;
            }

            yield return "Authenticating...";

            if (authError != null)
            {
                yield return $"Error during authentication: {authError.Message}";
                yield break;
            }

            if (string.IsNullOrEmpty(token))
            {
                yield return "Failed to get authentication token";
                yield break;
            }

            // Load tracking information if it exists
            Exception loadError = null;
            try
            {
                _fileProcessor.LoadTrackingInformation(_localDirectory);
            }
            catch (Exception ex)
            {
                loadError = ex;
            }

            if (loadError != null)
            {
                yield return $"Warning: Could not load tracking information: {loadError.Message}";
            }

            // Find all Revit files in the directory
            string[] revitFiles = null;
            Exception searchError = null;

            try
            {
                revitFiles = Directory.GetFiles(_localDirectory, "*.rvt", SearchOption.AllDirectories);
            }
            catch (Exception ex)
            {
                searchError = ex;
            }

            if (searchError != null)
            {
                yield return $"Error searching for files: {searchError.Message}";
                yield break;
            }

            if (revitFiles == null || revitFiles.Length == 0)
            {
                yield return "No Revit files found in the directory";
                yield break;
            }

            yield return $"Found {revitFiles.Length} Revit files for upload";

            // Upload each file
            int uploaded = 0;
            foreach (var filePath in revitFiles)
            {
                uploaded++;
                int progress = (int)(uploaded * 100.0 / revitFiles.Length);

                // Update UI
                if (_view != null)
                {
                    _view.UpdateUploadingProgress(
                        $"Uploading {uploaded} of {revitFiles.Length}: {Path.GetFileName(filePath)}",
                        progress);
                }

                // Upload the file
                var uploadCoroutine = _fileProcessor.UploadToACC(
                    filePath, _destAccountId, _destProjectId, _destFolderId);

                while (uploadCoroutine.MoveNext())
                {
                    yield return uploadCoroutine.Current;
                }
            }

            // Save updated tracking information
            Exception saveError = null;
            try
            {
                _fileProcessor.SaveTrackingInformation(_localDirectory);
            }
            catch (Exception ex)
            {
                saveError = ex;
            }

            if (saveError != null)
            {
                yield return $"Warning: Could not save tracking information: {saveError.Message}";
            }

            yield return "Upload completed successfully";
        }

        /// <summary>
        /// Reload links in uploaded files
        /// </summary>
        public IEnumerator ReloadLinks()
        {
            Initialize();

            if (string.IsNullOrEmpty(_localDirectory) || !Directory.Exists(_localDirectory))
            {
                yield return "Local directory does not exist";
                yield break;
            }

            yield return "Starting link reloading process...";

            // Load tracking information
            Exception loadError = null;
            try
            {
                _fileProcessor.LoadTrackingInformation(_localDirectory);
            }
            catch (Exception ex)
            {
                loadError = ex;
            }

            if (loadError != null)
            {
                yield return $"Error loading tracking information: {loadError.Message}";
                yield break;
            }

            // Find all Revit files in the directory
            string[] revitFiles = null;
            Exception searchError = null;

            try
            {
                revitFiles = Directory.GetFiles(_localDirectory, "*.rvt", SearchOption.AllDirectories);
            }
            catch (Exception ex)
            {
                searchError = ex;
            }

            if (searchError != null)
            {
                yield return $"Error searching for files: {searchError.Message}";
                yield break;
            }

            if (revitFiles == null || revitFiles.Length == 0)
            {
                yield return "No Revit files found in the directory";
                yield break;
            }

            yield return $"Found {revitFiles.Length} Revit files for link reloading";

            // Process each file
            int processed = 0;
            foreach (var filePath in revitFiles)
            {
                processed++;
                int progress = (int)(processed * 100.0 / revitFiles.Length);
                string fileName = Path.GetFileName(filePath);

                // Update UI
                if (_view != null)
                {
                    _view.UpdateReloadingProgress(
                        $"Reloading links {processed} of {revitFiles.Length}: {fileName}",
                        progress);
                }

                // Reload links
                var reloadCoroutine = _fileProcessor.ReloadLinksInCloud(fileName);
                while (reloadCoroutine.MoveNext())
                {
                    yield return reloadCoroutine.Current;
                }
            }

            yield return "Link reloading completed successfully";
        }

        /// <summary>
        /// Get folder contents asynchronously
        /// </summary>
        private async Task<List<DataManagement.Item>> GetFolderContentsAsync(string projectId, string folderId)
        {
            var items = await DataManagement.GetFolderContentsAsync(projectId, folderId);
            return items.ToList();
        }

        /// <summary>
        /// Count total items in a folder including subfolders
        /// </summary>
        private int CountTotalItems(List<DataManagement.Item> items)
        {
            // This is an estimate since we don't traverse the whole tree
            // Just count visible items and multiply by an estimated depth factor
            return items.Count * 2; // Simple estimate
        }

        /// <summary>
        /// Download items recursively
        /// </summary>
        private IEnumerator DownloadItemsRecursive(
            string projectId,
            List<DataManagement.Item> items,
            string outputFolder,
            DownloadTracker tracker)
        {
            // Create directory if it doesn't exist
            if (!Directory.Exists(outputFolder))
            {
                Directory.CreateDirectory(outputFolder);
            }

            // Process each item
            foreach (var item in items)
            {
                if (item.Type == "folders")
                {
                    // Variables for try-catch pattern
                    List<DataManagement.Item> contents = null;
                    Exception folderError = null;

                    try
                    {
                        var task = GetFolderContentsAsync(projectId, GetResourceId(item.ID));
                        task.Wait();
                        contents = task.Result;
                    }
                    catch (Exception ex)
                    {
                        folderError = ex;
                    }

                    // Handle results outside try-catch
                    if (folderError != null)
                    {
                        yield return $"Error getting folder contents for {item.Text}: {folderError.Message}";
                        continue;
                    }

                    // Process subfolder
                    var subfolderPath = Path.Combine(outputFolder, item.Text);
                    yield return $"Processing subfolder: {item.Text}";

                    var subfolderCoroutine = DownloadItemsRecursive(
                        projectId, contents, subfolderPath, tracker);

                    while (subfolderCoroutine.MoveNext())
                    {
                        yield return subfolderCoroutine.Current;
                    }
                }
                else if (item.Type == "items")
                {
                    yield return $"Downloading file: {item.Text}";

                    // Variables for try-catch pattern
                    Exception downloadError = null;

                    try
                    {
                        var task = DataManagement.DownloadFile(projectId, GetResourceId(item.ID), outputFolder);
                        task.Wait();

                        // Update tracker
                        tracker.ProcessedItems++;
                    }
                    catch (Exception ex)
                    {
                        downloadError = ex;
                    }

                    // Handle results outside try-catch
                    if (downloadError != null)
                    {
                        yield return $"Error downloading {item.Text}: {downloadError.Message}";
                    }
                    else
                    {
                        int progress = (int)(tracker.ProcessedItems * 100.0 / tracker.TotalItems);

                        // Update UI
                        if (tracker.View != null)
                        {
                            // Note: UpdateDownloadProgress doesn't exist on ViewMigrationToBim360
                            // We'll use UpdateUploadingProgress for consistency
                            tracker.View.UpdateUploadingProgress(
                                $"Downloaded {tracker.ProcessedItems} of {tracker.TotalItems} items",
                                progress);
                        }

                        yield return $"Downloaded: {item.Text}";
                    }
                }
            }
        }

        /// <summary>
        /// Extract resource ID from a full path
        /// </summary>
        private string GetResourceId(string fullPath)
        {
            string[] parts = fullPath.Split('/');
            return parts[parts.Length - 1];
        }
    }

    /// <summary>
    /// Tracks download progress for UI updates
    /// </summary>
    public class DownloadTracker
    {
        /// <summary>
        /// Total number of items to download
        /// </summary>
        public int TotalItems { get; set; }

        /// <summary>
        /// Number of items already downloaded
        /// </summary>
        public int ProcessedItems { get; set; }

        /// <summary>
        /// Reference to the view for UI updates
        /// </summary>
        public ViewMigrationToBim360 View { get; set; }
    }

    /// <summary>
    /// Model for binding UI elements and storing migration configuration
    /// </summary>
    public class MigrationModel
    {
        /// <summary>
        /// Collection of available folders in the target location
        /// </summary>
        public List<FolderLocation> AvailableFolders { get; set; } = new List<FolderLocation>();

        /// <summary>
        /// Collection of migration rules for file placement
        /// </summary>
        public List<MigrationRule> Rules { get; set; } = new List<MigrationRule>();
    }
}