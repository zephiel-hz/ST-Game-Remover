using System;
using System.Threading;
using System.Threading.Tasks;

namespace SteamPluginManager.Services.ResourcePipeline
{
    public interface IResourceService
    {
        string ServiceName { get; }

        Task<ResourceSearchResult?> SearchResourceAsync(
            string gameName, 
            int appId, 
            CancellationToken cancellationToken = default);

        Task<string> DownloadResourceAsync(
            ResourceSearchResult resource, 
            string targetDirectory, 
            IProgress<ResourceWorkflowProgress>? progress = null, 
            CancellationToken cancellationToken = default);

        Task<string> ExtractArchiveAsync(
            string archiveFilePath, 
            string outputDirectory, 
            string? password = null, 
            IProgress<ResourceWorkflowProgress>? progress = null, 
            CancellationToken cancellationToken = default);

        Task<string> CreateZipArchiveAsync(
            string sourceDirectory, 
            string outputZipPath, 
            IProgress<ResourceWorkflowProgress>? progress = null, 
            CancellationToken cancellationToken = default);
    }
}
