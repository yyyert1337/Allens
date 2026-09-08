using System.Threading;
using System.Threading.Tasks;
using Allens.Models;

namespace Allens.Services
{
    public interface IUninstallationService
    {
        Task<InstallationResult> UninstallAsync(AppItem app, CancellationToken cancellationToken = default);
        Task<InstallationResult> ForceUninstallAsync(AppItem app, CancellationToken cancellationToken = default);
    }
}
