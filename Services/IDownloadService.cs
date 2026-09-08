using System;
using System.Threading;
using System.Threading.Tasks;
using Allens.Models;

namespace Allens.Services
{
    public interface IDownloadService
    {
        Task<long?> GetFileSizeAsync(string url, CancellationToken cancellationToken = default);
        Task<string> DownloadFileAsync(string url, string fileName, IProgress<DownloadProgressInfo>? progress = null, CancellationToken cancellationToken = default, string? wingetId = null);
        void CleanupOldPartFiles(TimeSpan? maxAge = null);
        void DeletePartFile(string fileName);
        void ClearDownloadCache();
        long GetDownloadCacheSizeBytes();
        string GetDownloadCacheSizeDisplay();
        Task<string?> TryResolveLatestGitHubReleaseUrlAsync(string currentUrl, string? expectedFileName = null, CancellationToken cancellationToken = default);
    }
}
