using System;
using System.Threading;
using System.Threading.Tasks;

namespace SteamPluginManager.Services.ResourcePipeline
{
    public interface IStorageService
    {
        string ProviderName { get; }

        Task<string> UploadFileAsync(
            string localFilePath, 
            string remoteKey, 
            string contentType = "application/zip", 
            IProgress<ResourceWorkflowProgress>? progress = null, 
            CancellationToken cancellationToken = default);

        Task<bool> FileExistsAsync(
            string remoteKey, 
            CancellationToken cancellationToken = default);
    }
}
