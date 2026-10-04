using System;

namespace SteamPluginManager.Services.ResourcePipeline
{
    public enum WorkflowStage
    {
        Idle,
        Searching,
        Downloading,
        Extracting,
        Compressing,
        Uploading,
        Completed,
        Failed,
        NotFound
    }

    public record ResourceSearchResult(
        string Title,
        string DownloadUrl,
        string DirectoryUrl,
        string FileName,
        long FileSizeBytes,
        string SourceName,
        string ArchiveFormat
    );

    public record ResourceWorkflowProgress(
        WorkflowStage Stage,
        int Percentage,
        string Message,
        long BytesTransferred = 0,
        long TotalBytes = 0
    );

    public record ResourceWorkflowResult(
        bool Success,
        WorkflowStage FinalStage,
        string? RemoteStorageKey,
        string? ErrorMessage = null,
        Exception? Exception = null
    );
}
