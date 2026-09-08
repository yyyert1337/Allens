using System;
using System.Collections.Generic;
using System.IO;

namespace Allens.Helpers
{
    public static class PathSafetyHelper
    {
        public static bool IsProtectedSystemPath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return true;

            try
            {
                var fullPath = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path))
                                   .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

                var root = Path.GetPathRoot(fullPath)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                if (fullPath.Length <= 3) return true;

                var protectedRoots = new List<string>
                {
                    Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                    Environment.GetFolderPath(Environment.SpecialFolder.System),
                    Environment.GetFolderPath(Environment.SpecialFolder.SystemX86),
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles),
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFilesX86),
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    Path.GetDirectoryName(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)) ?? @"C:\Users"
                };

                foreach (var pr in protectedRoots)
                {
                    if (string.IsNullOrWhiteSpace(pr)) continue;
                    var cleanPr = Path.GetFullPath(pr).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    if (string.Equals(fullPath, cleanPr, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }

                var winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows)
                                        .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (string.Equals(fullPath, winDir, StringComparison.OrdinalIgnoreCase) ||
                    fullPath.StartsWith(winDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                var segments = fullPath.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
                if (segments.Length <= 1) return true;

                return false;
            }
            catch
            {
                return true;
            }
        }

        public static bool IsProtectedRegistryKey(string? subKeyPath)
        {
            if (string.IsNullOrWhiteSpace(subKeyPath)) return true;

            var clean = subKeyPath.Trim('\\', ' ');
            var parts = clean.Split('\\');
            if (parts.Length <= 1) return true;

            var protectedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "Software", "System", "Microsoft", "Windows", "Classes", "CurrentControlSet",
                "CurrentVersion", "Explorer", "Policies", "Run", "RunOnce", "Uninstall",
                "App Paths", "WOW6432Node", "Google", "Mozilla", "Valve"
            };

            var lastPart = parts[^1];
            if (protectedNames.Contains(lastPart) && parts.Length <= 2)
            {
                return true;
            }

            return false;
        }

        public static bool SafeDeleteFile(string? filePath, bool allowElevation = true)
        {
            if (string.IsNullOrWhiteSpace(filePath)) return false;
            if (IsProtectedSystemPath(filePath)) return false;

            try
            {
                if (File.Exists(filePath))
                {
                    File.SetAttributes(filePath, FileAttributes.Normal);
                    File.Delete(filePath);
                    return true;
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException || ex is IOException)
            {
                if (allowElevation)
                {
                    return TryElevatedDelete(filePath, isDirectory: false);
                }
            }
            catch { }
            return false;
        }

        public static bool SafeDeleteDirectory(string? dirPath, bool cleanParentIfEmpty = false, bool allowElevation = true)
        {
            if (string.IsNullOrWhiteSpace(dirPath)) return false;
            if (IsProtectedSystemPath(dirPath)) return false;

            try
            {
                if (!Directory.Exists(dirPath)) return true;

                var di = new DirectoryInfo(dirPath);
                foreach (var file in di.EnumerateFiles("*", SearchOption.AllDirectories))
                {
                    try { file.Attributes = FileAttributes.Normal; } catch { }
                }

                Directory.Delete(dirPath, recursive: true);

                if (cleanParentIfEmpty)
                {
                    var parent = Path.GetDirectoryName(dirPath);
                    if (!string.IsNullOrWhiteSpace(parent) && 
                        Directory.Exists(parent) && 
                        !IsProtectedSystemPath(parent))
                    {
                        try
                        {
                            if (!Directory.EnumerateFileSystemEntries(parent).Any())
                            {
                                Directory.Delete(parent);
                            }
                        }
                        catch { }
                    }
                }

                return true;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException || ex is IOException)
            {
                if (allowElevation && Directory.Exists(dirPath))
                {
                    bool ok = TryElevatedDelete(dirPath, isDirectory: true);
                    if (ok && cleanParentIfEmpty)
                    {
                        var parent = Path.GetDirectoryName(dirPath);
                        if (!string.IsNullOrWhiteSpace(parent) && Directory.Exists(parent) && !IsProtectedSystemPath(parent))
                        {
                            try
                            {
                                if (!Directory.EnumerateFileSystemEntries(parent).Any())
                                {
                                    Directory.Delete(parent);
                                }
                            }
                            catch { }
                        }
                    }
                    return ok;
                }
                return false;
            }
            catch
            {
                return false;
            }
        }

        public static bool TryElevatedDelete(string path, bool isDirectory)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            if (IsProtectedSystemPath(path)) return false;

            try
            {
                var fullPath = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path));
                var cmd = isDirectory ? $"/c rd /s /q \"{fullPath}\"" : $"/c del /f /q \"{fullPath}\"";
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = cmd,
                    Verb = "runas",
                    UseShellExecute = true,
                    CreateNoWindow = true,
                    WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden
                };
                using var proc = System.Diagnostics.Process.Start(psi);
                proc?.WaitForExit(10000);

                return isDirectory ? !Directory.Exists(fullPath) : !File.Exists(fullPath);
            }
            catch
            {
                return false;
            }
        }
    }
}
