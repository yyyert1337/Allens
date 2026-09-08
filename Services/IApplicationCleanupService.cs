using System.Collections.Generic;
using System.Threading.Tasks;
using Allens.Models;

namespace Allens.Services
{
    public interface IApplicationCleanupService
    {
        IEnumerable<string> GetExistingLeftovers(AppItem app);
        Task CleanupAsync(IEnumerable<string> paths);
    }
}
