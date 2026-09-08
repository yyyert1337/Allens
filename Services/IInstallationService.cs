using System.Threading;
using System.Threading.Tasks;
using Allens.Models;

namespace Allens.Services
{
    public class InstallationResult
    {
        public bool IsSuccess { get; set; }
        public int ExitCode { get; set; }
        public string ErrorMessage { get; set; } = string.Empty;
    }

    public interface IInstallationService
    {
        Task<InstallationResult> InstallAsync(AppItem app, string installerPath, CancellationToken cancellationToken = default);
    }
}
