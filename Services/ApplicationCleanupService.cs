using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Allens.Models;

namespace Allens.Services
{
    public class ApplicationCleanupService : IApplicationCleanupService
    {
        public IEnumerable<string> GetExistingLeftovers(AppItem app)
        {
            var foundPaths = new List<string>();

            if (app.CleanupPaths == null || app.CleanupPaths.Length == 0)
                return foundPaths;

            foreach (var path in app.CleanupPaths)
            {
                if (string.IsNullOrWhiteSpace(path)) continue;

                var expanded = Environment.ExpandEnvironmentVariables(path);

                if (Directory.Exists(expanded) || File.Exists(expanded))
                {
                    foundPaths.Add(expanded);
                }
            }

            return foundPaths;
        }

        public async Task CleanupAsync(IEnumerable<string> paths)
        {
            await Task.Run(() =>
            {
                foreach (var path in paths)
                {
                    if (Allens.Helpers.PathSafetyHelper.IsProtectedSystemPath(path)) continue;

                    if (File.Exists(path))
                    {
                        Allens.Helpers.PathSafetyHelper.SafeDeleteFile(path);
                    }
                    else if (Directory.Exists(path))
                    {
                        Allens.Helpers.PathSafetyHelper.SafeDeleteDirectory(path, cleanParentIfEmpty: true);
                    }
                }
            });
        }
    }
}
