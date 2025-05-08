using System;
using System.IO;
using System.Collections.Generic;
using System.Threading.Tasks;
using Autodesk.Forge;
using Autodesk.Forge.Model;


namespace Revit.SDK.Samples.CloudAPISample.CS.APS
{
    class DataManagement
    {
        /// <summary>
        /// 
        /// </summary>
        public class Item
        {
            public Item(string id, string text, string type)
            {
                this.ID = id;
                this.Type = type;
                this.Text = text;
            }

            public string ID;
            public string Type;
            public string Text;
        }

        /// <summary>
        /// 
        /// </summary>
        /// <returns></returns>
        public static async Task<IList<Item>> GetHubsAsync()
        {
            HubsApi hubsApi = new HubsApi();
            string userAccessToken = ThreeLeggedToken.GetToken();
            hubsApi.Configuration.AccessToken = userAccessToken;

            IList<Item> nodes = new List<Item>();
            var hubs = await hubsApi.GetHubsAsync();
            foreach (KeyValuePair<string, dynamic> hubInfo in new DynamicDictionaryItems(hubs.data))
            {
                string hubType = "hubs";
                switch ((string)hubInfo.Value.attributes.extension.type)
                {
                    case "hubs:autodesk.core:Hub":
                        hubType = "hubs";
                        break;
                    case "hubs:autodesk.a360:PersonalHub":
                        hubType = "personalhub";
                        break;
                    case "hubs:autodesk.bim360:Account":
                        hubType = "bim360hubs";
                        break;
                }
                Item item = new Item(hubInfo.Value.links.self.href, hubInfo.Value.attributes.name, hubType);
                nodes.Add(item);
            }

            return nodes;
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="hubId"></param>
        /// <returns></returns>
        public static async Task<IList<Item>> GetProjectsAsync(string hubId)
        {
            IList<Item> nodes = new List<Item>();

            ProjectsApi projectsApi = new ProjectsApi();
            string userAccessToken = ThreeLeggedToken.GetToken();
            projectsApi.Configuration.AccessToken = userAccessToken;
            var projects = await projectsApi.GetHubProjectsAsync(hubId);
            foreach (KeyValuePair<string, dynamic> projectInfo in new DynamicDictionaryItems(projects.data))
            {
                string projectType = "projects";
                switch ((string)projectInfo.Value.attributes.extension.type)
                {
                    case "projects:autodesk.core:Project":
                        projectType = "a360projects";
                        break;
                    case "projects:autodesk.bim360:Project":
                        projectType = "bim360projects";
                        break;
                }
                Item projectNode = new Item(projectInfo.Value.links.self.href, projectInfo.Value.attributes.name, projectType);
                nodes.Add(projectNode);
            }

            return nodes;
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="hubId"></param>
        /// <param name="projectId"></param>
        /// <returns></returns>
        public static async Task<IList<Item>> GetTopFoldersAsync(string hubId, string projectId)
        {
            IList<Item> nodes = new List<Item>();

            ProjectsApi projectsApi = new ProjectsApi();
            string userAccessToken = ThreeLeggedToken.GetToken();
            projectsApi.Configuration.AccessToken = userAccessToken;
            var folders = await projectsApi.GetProjectTopFoldersAsync(hubId, projectId);
            foreach (KeyValuePair<string, dynamic> folderInfo in new DynamicDictionaryItems(folders.data))
            {
                Item projectNode = new Item(folderInfo.Value.links.self.href, folderInfo.Value.attributes.displayName, "folders");
                nodes.Add(projectNode);
            }

            return nodes;
        }


        /// <summary>
        /// 
        /// </summary>
        /// <param name="projectId"></param>
        /// <param name="folderId"></param>
        /// <returns></returns>
        public static async Task<IList<Item>> GetFolderContentsAsync(string projectId, string folderId)
        {
            IList<Item> folderItems = new List<Item>();

            FoldersApi folderApi = new FoldersApi();
            string userAccessToken = ThreeLeggedToken.GetToken();
            folderApi.Configuration.AccessToken = userAccessToken;
            var folderContents = await folderApi.GetFolderContentsAsync(projectId, folderId);
            foreach (KeyValuePair<string, dynamic> folderContentItem in new DynamicDictionaryItems(folderContents.data))
            {
                string displayName = folderContentItem.Value.attributes.displayName;
                if (string.IsNullOrWhiteSpace(displayName))
                {
                    continue;
                }

                Item itemNode = new Item(folderContentItem.Value.links.self.href, displayName, (string)folderContentItem.Value.type);

                folderItems.Add(itemNode);
            }

            return folderItems;
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="projectId"></param>
        /// <param name="itemId"></param>
        /// <returns></returns>
        public static async Task<IList<Item>> GetItemVersionsAsync(string projectId, string itemId)
        {
            string userAccessToken = ThreeLeggedToken.GetToken();
            IList<Item> versionsList = new List<Item>();
            ItemsApi itemsApi = new ItemsApi();
            itemsApi.Configuration.AccessToken = userAccessToken;
            var versions = await itemsApi.GetItemVersionsAsync(projectId, itemId);
            foreach (KeyValuePair<string, dynamic> version in new DynamicDictionaryItems(versions.data))
            {
                DateTime versionDate = version.Value.attributes.lastModifiedTime;

                string urn = string.Empty;
                try { urn = (string)version.Value.relationships.derivatives.data.id; }
                catch { urn = "not_available"; } // some BIM 360 versions don't have viewable

                Item itemNode = new Item("/versions/" + urn, versionDate.ToString("dd/MM/yy HH:mm:ss"), "versions");

                versionsList.Add(itemNode);
            }

            return versionsList;
        }

        /// <summary>
        ///
        /// </summary>
        /// <param name="projectId"></param>
        /// <param name="itemId"></param>
        /// <param name="outputFolder"></param>
        public static async Task DownloadFile(string projectId, string itemId, string outputFolder)
        {
            // Declare variables at the method level so they're accessible throughout
            dynamic item = null;
            string displayName = "unknown";

            try
            {
                string userAccessToken = ThreeLeggedToken.GetToken();
                ItemsApi itemsApi = new ItemsApi();
                itemsApi.Configuration.AccessToken = userAccessToken;

                // Get item details - now 'item' is accessible throughout the method
                item = await itemsApi.GetItemAsync(projectId, itemId);

                // Extract file name with proper null checking
                displayName = item?.data?.attributes?.displayName ?? "unknown_file";

                if (string.IsNullOrEmpty(displayName) || displayName == "unknown_file")
                {
                    throw new Exception("Cannot get display name from item");
                }

                // Check if file already exists
                string outputPath = Path.Combine(outputFolder, displayName);
                if (File.Exists(outputPath))
                {
                    Console.WriteLine($"File already exists: {displayName}");
                    return;
                }

                // Get storage location
                string storageUrl = null;

                if (item?.included != null)
                {
                    foreach (KeyValuePair<string, dynamic> version in new DynamicDictionaryItems(item.included))
                    {
                        try
                        {
                            var storage = version.Value?.relationships?.storage;
                            if (storage != null)
                            {
                                storageUrl = storage.meta?.link?.href?.ToString();
                                if (!string.IsNullOrEmpty(storageUrl))
                                {
                                    break;
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"Error getting storage URL from version: {ex.Message}");
                        }
                    }
                }

                if (string.IsNullOrEmpty(storageUrl))
                {
                    throw new Exception($"Cannot get storage URL for file: {displayName}");
                }

                // Parse storage information
                string[] parameters = storageUrl.Split('/');
                if (parameters.Length < 3)
                {
                    throw new Exception($"Invalid storage URL format: {storageUrl}");
                }

                string bucketKey = parameters[parameters.Length - 3];
                string objectNameWithQuery = parameters[parameters.Length - 1];
                string objectName = objectNameWithQuery.Split('?')[0];

                // Get object details
                var objectApi = new ObjectsApi();
                objectApi.Configuration.AccessToken = userAccessToken;

                ObjectDetails objectDetails;
                try
                {
                    objectDetails = objectApi.GetObjectDetails(bucketKey, objectName);
                }
                catch (Exception ex)
                {
                    throw new Exception($"Cannot get object details for {objectName}: {ex.Message}");
                }

                // Handle the size conversion properly
                long fileSize = 0;
                if (objectDetails?.Size != null)
                {
                    // Explicit conversion from int? to long
                    fileSize = Convert.ToInt64(objectDetails.Size);
                }
                else
                {
                    throw new Exception("Cannot determine file size");
                }

                Console.WriteLine($"Starting download of {displayName} ({fileSize} bytes)");

                // Create output directory if it doesn't exist
                Directory.CreateDirectory(outputFolder);

                // Download the file in chunks
                long chunkSize = 5 * 1024 * 1024; // 5MB chunks
                long bytesDownloaded = 0;

                using (var fileStream = new FileStream(outputPath, FileMode.Create, FileAccess.Write))
                {
                    while (bytesDownloaded < fileSize)
                    {
                        long rangeStart = bytesDownloaded;
                        long rangeEnd = Math.Min(bytesDownloaded + chunkSize - 1, fileSize - 1);
                        string range = $"bytes={rangeStart}-{rangeEnd}";

                        try
                        {
                            using (var downloadStream = objectApi.GetObject(bucketKey, objectName, range))
                            {
                                // Use a buffer for better performance
                                byte[] buffer = new byte[8192];
                                int bytesRead;

                                while ((bytesRead = downloadStream.Read(buffer, 0, buffer.Length)) > 0)
                                {
                                    fileStream.Write(buffer, 0, bytesRead);
                                }
                            }

                            bytesDownloaded = rangeEnd + 1;

                            // Report progress
                            int percentComplete = (int)((bytesDownloaded * 100) / fileSize);
                            Console.WriteLine($"Downloaded {percentComplete}% of {displayName}");
                        }
                        catch (Exception ex)
                        {
                            throw new Exception($"Error downloading chunk {range}: {ex.Message}");
                        }
                    }
                }

                Console.WriteLine($"Successfully downloaded: {displayName}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Download failed for {displayName}: {ex.Message}");

                // Now we can safely use 'displayName' here because it's declared at the method level
                string errorFileName = $"ERROR_{Path.GetFileNameWithoutExtension(displayName)}.txt";
                string errorPath = Path.Combine(outputFolder, errorFileName);

                // Create detailed error log
                string errorContent = $"Download failed at {DateTime.Now}\n" +
                                    $"Project ID: {projectId}\n" +
                                    $"Item ID: {itemId}\n" +
                                    $"File Name: {displayName}\n" +
                                    $"Error: {ex.Message}\n" +
                                    $"Stack trace: {ex.StackTrace}";

                File.WriteAllText(errorPath, errorContent);

                throw; // Re-throw the exception so the caller knows the download failed
            }
        }

        public static async Task<bool> IsRevitModelWorksharedAsync(string projectId, string itemId)
        {
            try
            {
                string userAccessToken = ThreeLeggedToken.GetToken();
                ItemsApi itemsApi = new ItemsApi();
                itemsApi.Configuration.AccessToken = userAccessToken;

                var item = await itemsApi.GetItemAsync(projectId, itemId);

                string displayName = item.data.attributes.displayName;
                if (!displayName.EndsWith(".rvt", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                if (item.included != null)
                {
                    foreach (var included in item.included)
                    {
                        if (included.type == "versions")
                        {
                            var extension = included.attributes?.extension;
                            if (extension != null)
                            {
                                string extensionType = extension.type?.ToString();
                                if (extensionType == "versions:autodesk.bim360:C4RModel" ||
                                    extensionType == "versions:autodesk.core:C4RModel")
                                {
                                    return true;
                                }
                                var data = extension.data;
                                if (data != null && data.worksharingEnabled == true)
                                {
                                    return true;
                                }
                            }
                        }
                    }
                }

                return false;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error checking worksharing status: {ex.Message}");
                return false;
            }
        }
    }
}
