using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace CloudAPISample.Samples.Migration
{
    /// <summary>
    /// Handles processing Revit files for migration with workset preservation
    /// </summary>
    /// <summary>
    /// Handles processing Revit files for migration with workset preservation
    /// </summary>
    public class RevitFileProcessor
    {
        private readonly UIApplication _application;
        private readonly Dictionary<string, RevitLinkInfo> _linkInfoMap = new Dictionary<string, RevitLinkInfo>();
        private readonly Dictionary<string, string> _filePathToModelGuidMap = new Dictionary<string, string>();

        /// <summary>
        /// File name for storing link information
        /// </summary>
        public const string LINKS_INFO_FILE = "linkinfo.json";

        /// <summary>
        /// File name for storing model GUID information
        /// </summary>
        public const string MODELS_GUID_FILE = "modelsguid.json";

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="application">Revit UIApplication</param>
        public RevitFileProcessor(UIApplication application)
        {
            _application = application;
        }

        /// <summary>
        /// Process a Revit file to identify links and preserve worksets
        /// </summary>
        /// <param name="filePath">Path to the Revit file</param>
        /// <returns>IEnumerator for coroutine execution</returns>
        public IEnumerator ProcessRevitFile(string filePath)
        {
            yield return $"Processing file: {Path.GetFileName(filePath)}";

            // Create open options with worksets preserved
            OpenOptions openOptions = new OpenOptions
            {
                DetachFromCentralOption = DetachFromCentralOption.DetachAndPreserveWorksets,
                OpenForeignOption = OpenForeignOption.DoNotOpen
            };

            Document document = null;
            string fileName = Path.GetFileName(filePath);
            bool success = false;
            string statusMessage = "";

            try
            {
                // Open the document with appropriate options
                ModelPath modelPath = ModelPathUtils.ConvertUserVisiblePathToModelPath(filePath);
                document = _application.Application.OpenDocumentFile(modelPath, openOptions);

                // Check if the document is workshared
                bool isWorkshared = document.IsWorkshared;
                statusMessage = $"File is workshared: {isWorkshared}";

                // Collect information about links in the document
                var links = GetRevitLinkInfo(document);

                if (links.Count > 0)
                {
                    statusMessage = $"Found {links.Count} links in the file";

                    // Add links to our tracking dictionary
                    foreach (var link in links)
                    {
                        _linkInfoMap[link.LinkPath] = link;
                    }
                }
                else
                {
                    statusMessage = "No links found in the file";
                }

                // Close the document
                document.Close(false);
                success = true;
            }
            catch (Exception ex)
            {
                statusMessage = $"Error processing file: {ex.Message}";
                if (document != null && !document.IsReadOnly)
                    document.Close(false);
                success = false;
            }

            yield return statusMessage;

            if (!success)
                throw new Exception(statusMessage);
        }

        /// <summary>
        /// Upload a Revit file to ACC with worksets preserved
        /// </summary>
        /// <param name="filePath">Path to the Revit file</param>
        /// <param name="accountId">ACC account ID</param>
        /// <param name="projectId">ACC project ID</param>
        /// <param name="folderUrn">ACC folder URN</param>
        /// <returns>IEnumerator for coroutine execution</returns>
        public IEnumerator UploadToACC(string filePath, Guid accountId, Guid projectId, string folderUrn)
        {
            string fileName = Path.GetFileName(filePath);
            yield return $"Uploading file: {fileName}";

            // Create open options with worksets preserved
            OpenOptions openOptions = new OpenOptions
            {
                DetachFromCentralOption = DetachFromCentralOption.DetachAndPreserveWorksets,
                OpenForeignOption = OpenForeignOption.DoNotOpen
            };

            Document document = null;
            bool success = false;
            string statusMessage = "";

            try
            {
                // Open the document with appropriate options
                ModelPath modelPath = ModelPathUtils.ConvertUserVisiblePathToModelPath(filePath);
                document = _application.Application.OpenDocumentFile(modelPath, openOptions);

                // Upload the document to ACC
                string uploadFileName = $"Migrated_{fileName}";
                statusMessage = $"Saving to cloud as: {uploadFileName}";

                document.SaveAsCloudModel(accountId, projectId, folderUrn, uploadFileName);

                // Get the cloud model path for later reference
                ModelPath cloudPath = document.GetCloudModelPath();
                string guidInfo = $"{cloudPath.GetProjectGUID()},{cloudPath.GetModelGUID()}";

                // Store the mapping from file path to cloud model GUID
                _filePathToModelGuidMap[fileName] = guidInfo;

                statusMessage = $"Successfully uploaded: {uploadFileName}";
                document.Close(false);
                success = true;
            }
            catch (Exception ex)
            {
                statusMessage = $"Error uploading file: {ex.Message}";
                if (document != null && !document.IsReadOnly)
                    document.Close(false);
                success = false;
            }

            yield return statusMessage;

            if (!success)
                throw new Exception(statusMessage);
        }

        /// <summary>
        /// Reload links in a cloud Revit model
        /// </summary>
        /// <param name="fileName">Original file name</param>
        /// <returns>IEnumerator for coroutine execution</returns>
        public IEnumerator ReloadLinksInCloud(string fileName)
        {
            yield return $"Reloading links for: {fileName}";

            // Check if we have the cloud path for this file
            if (!_filePathToModelGuidMap.TryGetValue(fileName, out string guidInfo))
            {
                yield return $"No cloud information found for {fileName}";
                yield break;
            }

            string[] guids = guidInfo.Split(',');
            if (guids.Length != 2)
            {
                yield return $"Invalid GUID information for {fileName}";
                yield break;
            }

            // Create the cloud model path
            ModelPath cloudPath = ModelPathUtils.ConvertCloudGUIDsToCloudPath(
                "US", Guid.Parse(guids[0]), Guid.Parse(guids[1]));

            // Open options for cloud models
            OpenOptions openOptions = new OpenOptions();
            Document document = null;
            bool success = false;
            string statusMessage = "";

            try
            {
                // Open the cloud document
                document = _application.Application.OpenDocumentFile(cloudPath, openOptions,
                    new DefaultOpenFromCloudCallback());

                statusMessage = $"Document opened: {document.Title}";

                // Get all link types in the document
                var linkTypes = new FilteredElementCollector(document)
                    .OfClass(typeof(RevitLinkType))
                    .Cast<RevitLinkType>()
                    .ToList();

                if (linkTypes.Count == 0)
                {
                    statusMessage = "No links found in cloud document";
                    document.Close(false);
                    yield break;
                }

                statusMessage = $"Found {linkTypes.Count} links in cloud document";

                int reloadedCount = 0;

                // Start a transaction to reload links
                using (Transaction tx = new Transaction(document, "Reload Links"))
                {
                    tx.Start();

                    // Process each link
                    foreach (RevitLinkType linkType in linkTypes)
                    {
                        string linkName = linkType.Name;
                        statusMessage = $"Processing link: {linkName}";

                        // Check if we have cloud information for this link
                        if (_filePathToModelGuidMap.TryGetValue(linkName, out string linkGuidInfo))
                        {
                            string[] linkGuids = linkGuidInfo.Split(',');
                            if (linkGuids.Length == 2)
                            {
                                statusMessage = $"Found cloud path for link: {linkName}";

                                try
                                {
                                    // Create the cloud model path for the link
                                    ModelPath linkCloudPath = ModelPathUtils.ConvertCloudGUIDsToCloudPath(
                                        "US", Guid.Parse(linkGuids[0]), Guid.Parse(linkGuids[1]));

                                    // Reload the link
                                    linkType.LoadFrom(linkCloudPath, new WorksetConfiguration());
                                    reloadedCount++;
                                    statusMessage = $"Successfully reloaded link: {linkName}";
                                }
                                catch (Exception ex)
                                {
                                    statusMessage = $"Error reloading link {linkName}: {ex.Message}";
                                }
                            }
                        }
                        else
                        {
                            statusMessage = $"No cloud information found for link: {linkName}";
                        }
                    }

                    tx.Commit();
                    statusMessage = $"Reloaded {reloadedCount} links";
                }

                // Save the document with updated links
                var transOptions = new TransactWithCentralOptions();
                var syncOptions = new SynchronizeWithCentralOptions();
                document.SynchronizeWithCentral(transOptions, syncOptions);

                statusMessage = $"Synchronized document with central";
                document.Close(false);
                success = true;
            }
            catch (Exception ex)
            {
                statusMessage = $"Error reloading links: {ex.Message}";
                if (document != null && !document.IsReadOnly)
                    document.Close(false);
                success = false;
            }

            yield return statusMessage;

            if (!success)
                throw new Exception(statusMessage);
        }

        /// <summary>
        /// Process a workshared Revit file to identify links and preserve worksets
        /// </summary>
        /// <param name="filePath">Path to the workshared Revit file</param>
        /// <returns>IEnumerator for coroutine execution</returns>
        public IEnumerator ProcessWorkshakedRevitFile(string filePath)
        {
            yield return $"Processing workshared file: {Path.GetFileName(filePath)}";

            // Declare variables outside try-catch to comply with yield restrictions
            Document document = null;
            //string statusMessage = "";
            bool isWorkshared = false;
            List<RevitLinkInfo> links = null;
            List<Workset> worksets = null;
            Exception processError = null;

            // Create open options with worksets preserved
            OpenOptions openOptions = new OpenOptions
            {
                DetachFromCentralOption = DetachFromCentralOption.DetachAndPreserveWorksets,
                OpenForeignOption = OpenForeignOption.DoNotOpen,
                Audit = false
            };

            WorksetConfiguration worksetConfig = new WorksetConfiguration(WorksetConfigurationOption.OpenAllWorksets);
            openOptions.SetOpenWorksetsConfiguration(worksetConfig);

            try
            {
                ModelPath modelPath = ModelPathUtils.ConvertUserVisiblePathToModelPath(filePath);
                document = _application.Application.OpenDocumentFile(modelPath, openOptions);

                isWorkshared = document.IsWorkshared;

                if (isWorkshared)
                {
                    FilteredElementCollector worksetCollector = new FilteredElementCollector(document);
                    worksetCollector.OfClass(typeof(Workset));
                    worksets = worksetCollector.Cast<Workset>().ToList();
                }

                links = GetRevitLinkInfo(document);

                document.Close(false);
                document = null; // Set to null after closing
            }
            catch (Exception ex)
            {
                processError = ex;
                if (document != null && document.IsValidObject)
                {
                    document.Close(false);
                }
            }

            // Handle results outside try-catch
            if (processError != null)
            {
                yield return $"Error processing workshared file: {processError.Message}";
                throw processError;
            }

            yield return $"Document is workshared: {isWorkshared}";

            if (isWorkshared && worksets != null)
            {
                yield return $"Found {worksets.Count} worksets";

                // Store workset information for later use
                foreach (Workset workset in worksets)
                {
                    yield return $"Workset: {workset.Name} (ID: {workset.Id})";
                }
            }

            if (links != null && links.Count > 0)
            {
                yield return $"Found {links.Count} linked models";

                // Store each link with its own path as the key
                foreach (var link in links)
                {
                    _linkInfoMap[link.LinkPath] = link;
                }
            }
            else
            {
                yield return "No linked models found";
            }
        }

        /// <summary>
        /// Reload links in a workshared model that has been uploaded to the cloud
        /// </summary>
        /// <param name="fileName">Name of the original file</param>
        /// <returns>IEnumerator for coroutine execution</returns>
        public IEnumerator ReloadLinksInWorkshakedModel(string fileName)
        {
            yield return $"Reloading links in workshared model: {fileName}";

            // Check if we have cloud information for this file
            if (!_filePathToModelGuidMap.TryGetValue(fileName, out string guidInfo))
            {
                yield return $"No cloud information found for {fileName}";
                yield break;
            }

            string[] guids = guidInfo.Split(',');
            if (guids.Length != 2)
            {
                yield return $"Invalid GUID information for {fileName}";
                yield break;
            }

            // Variables for try-catch pattern
            Document document = null;
            Exception processError = null;
            List<RevitLinkType> linkTypes = null;
            int reloadedCount = 0;

            try
            {
                // Create the cloud model path
                ModelPath cloudPath = ModelPathUtils.ConvertCloudGUIDsToCloudPath(
                    "US", Guid.Parse(guids[0]), Guid.Parse(guids[1]));

                // Open the cloud model with worksets
                OpenOptions openOptions = new OpenOptions();
                WorksetConfiguration worksetConfig = new WorksetConfiguration(WorksetConfigurationOption.OpenAllWorksets);
                openOptions.SetOpenWorksetsConfiguration(worksetConfig);

                document = _application.Application.OpenDocumentFile(cloudPath, openOptions,
                    new DefaultOpenFromCloudCallback());

                // Get all link types
                linkTypes = new FilteredElementCollector(document)
                    .OfClass(typeof(RevitLinkType))
                    .Cast<RevitLinkType>()
                    .ToList();

                if (linkTypes.Count > 0)
                {
                    using (Transaction tx = new Transaction(document, "Reload Links"))
                    {
                        tx.Start();

                        foreach (RevitLinkType linkType in linkTypes)
                        {
                            string linkName = linkType.Name;

                            // Check if we have cloud information for this link
                            if (_filePathToModelGuidMap.TryGetValue(linkName, out string linkGuidInfo))
                            {
                                string[] linkGuids = linkGuidInfo.Split(',');
                                if (linkGuids.Length == 2)
                                {
                                    try
                                    {
                                        ModelPath linkCloudPath = ModelPathUtils.ConvertCloudGUIDsToCloudPath(
                                            "US", Guid.Parse(linkGuids[0]), Guid.Parse(linkGuids[1]));

                                        // Reload with workset configuration
                                        WorksetConfiguration linkWorksetConfig = new WorksetConfiguration(
                                            WorksetConfigurationOption.OpenAllWorksets);

                                        linkType.LoadFrom(linkCloudPath, linkWorksetConfig);
                                        reloadedCount++;
                                    }
                                    catch (Exception ex)
                                    {
                                        ex.Message.ToString();
                                    }
                                }
                            }
                        }

                        tx.Commit();
                    }

                    // Synchronize with central
                    TransactWithCentralOptions transOptions = new TransactWithCentralOptions();
                    SynchronizeWithCentralOptions syncOptions = new SynchronizeWithCentralOptions();
                    syncOptions.SetRelinquishOptions(new RelinquishOptions(true));

                    document.SynchronizeWithCentral(transOptions, syncOptions);
                }

                document.Close(false);
                document = null;
            }
            catch (Exception ex)
            {
                processError = ex;
                if (document != null && document.IsValidObject)
                {
                    document.Close(false);
                }
            }

            // Handle results outside try-catch
            if (processError != null)
            {
                yield return $"Error reloading links: {processError.Message}";
                throw processError;
            }

            if (linkTypes == null || linkTypes.Count == 0)
            {
                yield return "No links found in cloud document";
            }
            else
            {
                yield return $"Successfully reloaded {reloadedCount} of {linkTypes.Count} links";
            }

            yield return "Link reloading completed";
        }

        /// <summary>
        /// Save link and model information to JSON files
        /// </summary>
        public void SaveTrackingInformation(string basePath)
        {
            // Convert link info to a serializable dictionary
            var linkInfoDict = new Dictionary<string, List<string>>();
            foreach (var kvp in _linkInfoMap)
            {
                var info = kvp.Value;
                if (!linkInfoDict.ContainsKey(kvp.Key))
                {
                    linkInfoDict[kvp.Key] = new List<string>();
                }
                linkInfoDict[kvp.Key].Add(info.LinkName);
            }

            // Save link information
            string linksJson = JsonConvert.SerializeObject(linkInfoDict, Formatting.Indented);
            File.WriteAllText(Path.Combine(basePath, LINKS_INFO_FILE), linksJson);

            // Save model GUID information
            string modelsJson = JsonConvert.SerializeObject(_filePathToModelGuidMap, Formatting.Indented);
            File.WriteAllText(Path.Combine(basePath, MODELS_GUID_FILE), modelsJson);
        }

        /// <summary>
        /// Load link and model information from JSON files
        /// </summary>
        public void LoadTrackingInformation(string basePath)
        {
            string linksFile = Path.Combine(basePath, LINKS_INFO_FILE);
            string modelsFile = Path.Combine(basePath, MODELS_GUID_FILE);

            if (File.Exists(modelsFile))
            {
                string modelsJson = File.ReadAllText(modelsFile);
                var modelDict = JsonConvert.DeserializeObject<Dictionary<string, string>>(modelsJson);
                foreach (var kvp in modelDict)
                {
                    _filePathToModelGuidMap[kvp.Key] = kvp.Value;
                }
            }
        }

        /// <summary>
        /// Get information about all Revit links in a document
        /// </summary>
        private List<RevitLinkInfo> GetRevitLinkInfo(Document document)
        {
            List<RevitLinkInfo> result = new List<RevitLinkInfo>();

            // Get all link types in the document
            var linkTypes = new FilteredElementCollector(document)
                .OfClass(typeof(RevitLinkType))
                .Cast<RevitLinkType>()
                .ToList();

            foreach (RevitLinkType linkType in linkTypes)
            {
                // Skip if the link type is invalid
                if (linkType == null)
                    continue;

                // Get link information
                RevitLinkInfo linkInfo = new RevitLinkInfo
                {
                    LinkTypeId = linkType.Id,
                    LinkName = linkType.Name,
                    IsAttached  = linkType.GetLinkedFileStatus() == LinkedFileStatus.Loaded
                };

                // Try to get the external file reference
                ExternalFileReference extRef = null;
                try
                {
                    extRef = linkType.GetExternalFileReference();
                }
                catch (Exception)
                {
                    // Skip if we can't get the external file reference
                    continue;
                }

                if (extRef != null)
                {
                    // Get the model path
                    ModelPath modelPath = extRef.GetPath();
                    linkInfo.LinkPath = ModelPathUtils.ConvertModelPathToUserVisiblePath(modelPath);

                    // Add to the result
                    result.Add(linkInfo);
                }
            }

            return result;
        }
    }

    /// <summary>
    /// Information about a Revit link
    /// </summary>
    public class RevitLinkInfo
    {
        /// <summary>
        /// ElementId of the link type
        /// </summary>
        public ElementId LinkTypeId { get; set; }

        /// <summary>
        /// Name of the link
        /// </summary>
        public string LinkName { get; set; }

        /// <summary>
        /// Path to the link file
        /// </summary>
        public string LinkPath { get; set; }

        /// <summary>
        /// Whether the link is attached
        /// </summary>
        public bool IsAttached { get; set; }
    }
}
