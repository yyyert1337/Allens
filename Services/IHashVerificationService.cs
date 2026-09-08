using System.Threading;
using System.Threading.Tasks;

namespace Allens.Services
{
    public interface IHashVerificationService
    {
        Task<bool> VerifyFileHashAsync(string filePath, string expectedSha256, CancellationToken cancellationToken = default);
        Task<string> ComputeSha256Async(string filePath, CancellationToken cancellationToken = default);
    }
}
