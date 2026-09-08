using System;
using System.Threading;
using System.Threading.Tasks;
using Allens.Models;

namespace Allens.Services
{
    public interface IUpdateService
    {
        Task<UpdateInfo?> CheckForUpdateAsync(CancellationToken cancellationToken = default);
        Task<string> DownloadUpdateAsync(string downloadUrl, IProgress<double>? progress = null, CancellationToken cancellationToken = default);
        void ApplyUpdateAndRestart(string downloadedInstallerPath);
    }
}
