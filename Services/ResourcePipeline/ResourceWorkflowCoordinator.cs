using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace SteamPluginManager.Services.ResourcePipeline
{
    public class ResourceWorkflowCoordinator
    {
        private readonly IResourceService _resourceService;
        private readonly IStorageService _storageService;

        public ResourceWorkflowCoordinator(IResourceService? resourceService = null, IStorageService? storageService = null)
        {
            _resourceService = resourceService ?? new OnlineFixResourceService();
            _storageService = storageService ?? new BackblazeB2StorageService();
        }

        public async Task<ResourceWorkflowResult> ExecuteWorkflowAsync(
            int appId, 
            string gameName, 
            IProgress<ResourceWorkflowProgress>? progress = null, 
            CancellationToken cancellationToken = default)
        {
            if (appId <= 0)
                return new ResourceWorkflowResult(false, WorkflowStage.Failed, null, "Invalid AppID provided.");

            var settings = ServiceConfiguration.Current.ResourceSource;
            var tempBase = Path.Combine(Path.GetTempPath(), "HZLuaManager_Workflow", $"{appId}_{Guid.NewGuid():N}");

            try
            {
                Directory.CreateDirectory(tempBase);

                // Stage 1: Search
                Logger.Log($"[ResourceWorkflow] Starting resource search for '{gameName}' (AppID: {appId}) on {_resourceService.ServiceName}...");
                progress?.Report(new ResourceWorkflowProgress(WorkflowStage.Searching, 10, $"Searching {_resourceService.ServiceName} for '{gameName}'..."));

                var resource = await _resourceService.SearchResourceAsync(gameName, appId, cancellationToken);
                if (resource == null)
                {
                    Logger.Log($"[ResourceWorkflow] No resource found for '{gameName}' ({appId}) on {_resourceService.ServiceName}. Workflow terminated gracefully.");
                    progress?.Report(new ResourceWorkflowProgress(WorkflowStage.NotFound, 100, $"No resource found for '{gameName}' on {_resourceService.ServiceName}."));
                    return new ResourceWorkflowResult(false, WorkflowStage.NotFound, null, "Resource not found on source.");
                }

                Logger.Log($"[ResourceWorkflow] Resource found: '{resource.FileName}' at {resource.DownloadUrl}");

                // Stage 2: Download
                progress?.Report(new ResourceWorkflowProgress(WorkflowStage.Downloading, 20, $"Downloading '{resource.FileName}'..."));
                var downloadDir = Path.Combine(tempBase, "download");
                var downloadedFilePath = await _resourceService.DownloadResourceAsync(resource, downloadDir, progress, cancellationToken);

                if (!File.Exists(downloadedFilePath))
                {
                    throw new FileNotFoundException($"Downloaded resource file not found at: {downloadedFilePath}");
                }

                Logger.Log($"[ResourceWorkflow] Successfully downloaded {downloadedFilePath} ({new FileInfo(downloadedFilePath).Length / (1024 * 1024)}MB)");

                // Stage 3: Extract with password
                var extractDir = Path.Combine(tempBase, "extracted");
                var archivePassword = settings.ArchivePassword;
                Logger.Log($"[ResourceWorkflow] Extracting archive using configured password...");
                progress?.Report(new ResourceWorkflowProgress(WorkflowStage.Extracting, 50, "Extracting and decrypting archive..."));

                await _resourceService.ExtractArchiveAsync(downloadedFilePath, extractDir, archivePassword, progress, cancellationToken);
                Logger.Log($"[ResourceWorkflow] Extraction complete to {extractDir}");

                // Stage 4: Repack into {appId}.zip
                var repackZipPath = Path.Combine(tempBase, $"{appId}.zip");
                var effectiveExtractDir = OnlineFixResourceService.ResolveEffectiveContentDirectory(extractDir, resource.FileName, gameName);
                Logger.Log($"[ResourceWorkflow] Repackaging extracted contents from '{effectiveExtractDir}' into {appId}.zip...");
                progress?.Report(new ResourceWorkflowProgress(WorkflowStage.Compressing, 75, $"Creating {appId}.zip..."));

                if (_resourceService is OnlineFixResourceService ofs)
                {
                    await ofs.CreateZipArchiveAsync(extractDir, repackZipPath, resource.FileName, gameName, progress, cancellationToken);
                }
                else
                {
                    await _resourceService.CreateZipArchiveAsync(extractDir, repackZipPath, progress, cancellationToken);
                }
                if (!File.Exists(repackZipPath))
                {
                    throw new FileNotFoundException($"Repackaged zip archive was not created: {repackZipPath}");
                }

                Logger.Log($"[ResourceWorkflow] Standardized archive ready: {repackZipPath} ({new FileInfo(repackZipPath).Length / (1024 * 1024)}MB)");

                // Stage 5: Upload to Storage
                var prefix = settings.StoragePrefix.TrimEnd('/') + "/";
                var remoteKey = $"{prefix}{appId}.zip";
                Logger.Log($"[ResourceWorkflow] Uploading {remoteKey} to {_storageService.ProviderName}...");
                progress?.Report(new ResourceWorkflowProgress(WorkflowStage.Uploading, 85, $"Uploading {remoteKey} to {_storageService.ProviderName}..."));

                var remoteUrl = await _storageService.UploadFileAsync(repackZipPath, remoteKey, "application/zip", progress, cancellationToken);

                Logger.Log($"[ResourceWorkflow] Resource workflow completed successfully! Remote URL: {remoteUrl}");
                progress?.Report(new ResourceWorkflowProgress(WorkflowStage.Completed, 100, $"Successfully uploaded {remoteKey}!"));

                return new ResourceWorkflowResult(true, WorkflowStage.Completed, remoteKey);
            }
            catch (OperationCanceledException)
            {
                Logger.Log($"[ResourceWorkflow] Workflow for {appId} was cancelled by user or timeout.");
                progress?.Report(new ResourceWorkflowProgress(WorkflowStage.Failed, 0, "Operation was cancelled."));
                return new ResourceWorkflowResult(false, WorkflowStage.Failed, null, "Operation was cancelled.");
            }
            catch (Exception ex)
            {
                Logger.Log($"[ResourceWorkflow] Pipeline error for '{gameName}' ({appId}): {ex.Message}\n{ex.StackTrace}");
                progress?.Report(new ResourceWorkflowProgress(WorkflowStage.Failed, 0, $"Error: {ex.Message}"));
                return new ResourceWorkflowResult(false, WorkflowStage.Failed, null, ex.Message, ex);
            }
            finally
            {
                // Always clean up workspace
                try
                {
                    if (Directory.Exists(tempBase))
                    {
                        Directory.Delete(tempBase, recursive: true);
                    }
                }
                catch (Exception cleanupEx)
                {
                    Logger.Log($"[ResourceWorkflow] Warning cleaning temp workspace {tempBase}: {cleanupEx.Message}");
                }
            }
        }
    }
}
