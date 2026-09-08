using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Allens.Services
{
    public class HashVerificationService : IHashVerificationService
    {
        public async Task<bool> VerifyFileHashAsync(string filePath, string expectedSha256, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(expectedSha256))
            {

                return true;
            }

            if (!File.Exists(filePath))
            {
                return false;
            }

            var actualHash = await ComputeSha256Async(filePath, cancellationToken);
            return string.Equals(actualHash, expectedSha256.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        public async Task<string> ComputeSha256Async(string filePath, CancellationToken cancellationToken = default)
        {
            if (!File.Exists(filePath))
            {
                throw new FileNotFoundException("Файл для проверки хэша не найден.", filePath);
            }

            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
            using var sha256 = SHA256.Create();

            var hashBytes = await sha256.ComputeHashAsync(stream, cancellationToken);

            return Convert.ToHexString(hashBytes);
        }
    }
}
