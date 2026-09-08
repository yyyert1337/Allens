using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Allens.Models;

namespace Allens.Services
{
    public class ApplicationDetectionService : IApplicationDetectionService
    {
        public bool IsInstalled(AppItem app)
        {
            InspectApp(app);
            return app.IsInstalled;
        }

        public void UpdateInstallationStatus(IEnumerable<AppItem> apps)
        {
            foreach (var app in apps)
            {
                InspectApp(app);
            }
        }

        public void InspectApp(AppItem app)
        {
            if (string.IsNullOrWhiteSpace(app.DetectionMethod) || string.IsNullOrWhiteSpace(app.DetectionValue))
            {
                app.IsInstalled = false;
                app.InstalledVersion = string.Empty;
                app.InstalledPath = string.Empty;
                app.HasUpdate = false;
                return;
            }

            try
            {
                if (app.DetectionMethod.Equals("path", StringComparison.OrdinalIgnoreCase))
                {
                    var expandedPath = Environment.ExpandEnvironmentVariables(app.DetectionValue);

                    // If primary detection path doesn't exist, check if containing folder has a matching binary
                    if (!File.Exists(expandedPath) && !Directory.Exists(expandedPath))
                    {
                        var targetDir = Path.GetDirectoryName(expandedPath);
                        if (!string.IsNullOrWhiteSpace(targetDir) && Directory.Exists(targetDir))
                        {
                            var matchingExe = FindMatchingExecutable(app, targetDir);
                            if (!string.IsNullOrWhiteSpace(matchingExe) && File.Exists(matchingExe))
                            {
                                expandedPath = matchingExe;
                            }
                        }
                    }

                    if (File.Exists(expandedPath))
                    {
                        app.IsInstalled = true;
                        // For portable applications or direct file targets, store the full executable path!
                        app.InstalledPath = expandedPath;

                        try
                        {
                            var verInfo = FileVersionInfo.GetVersionInfo(expandedPath);
                            var ver = !string.IsNullOrWhiteSpace(verInfo.ProductVersion)
                                ? verInfo.ProductVersion.Trim()
                                : (!string.IsNullOrWhiteSpace(verInfo.FileVersion) ? verInfo.FileVersion.Trim() : string.Empty);
                            app.InstalledVersion = CleanVersionString(ver);
                        }
                        catch
                        {
                            app.InstalledVersion = string.Empty;
                        }

                        // Immediate size display for single-file portable
                        try
                        {
                            var fi = new FileInfo(expandedPath);
                            if (fi.Length > 0)
                            {
                                app.InstalledSizeDisplay = DownloadService.FormatBytes(fi.Length);
                            }
                        }
                        catch { }
                    }
                    else if (Directory.Exists(expandedPath))
                    {
                        if (HasAnyFiles(expandedPath))
                        {
                            app.IsInstalled = true;
                            app.InstalledPath = expandedPath;
                            app.InstalledVersion = string.Empty;
                        }
                        else
                        {
                            app.IsInstalled = false;
                            app.InstalledVersion = string.Empty;
                            app.InstalledPath = string.Empty;
                        }
                    }
                    else
                    {
                        app.IsInstalled = false;
                        app.InstalledVersion = string.Empty;
                        app.InstalledPath = string.Empty;
                    }
                }
                else if (app.DetectionMethod.Equals("registry", StringComparison.OrdinalIgnoreCase))
                {
                    var parts = app.DetectionValue.Split(new[] { '\\' }, 2);
                    if (parts.Length == 2)
                    {
                        var hiveString = parts[0];
                        var subKey = parts[1];
                        
                        RegistryKey? baseKey = GetBaseKey(hiveString);
                        bool found = false;

                        if (baseKey != null)
                        {
                            // 1. Try direct registry subkey
                            using (var key = baseKey.OpenSubKey(subKey))
                            {
                                if (key != null && ExtractRegistryDetails(key, app))
                                {
                                    found = true;
                                }
                            }

                            // 2. If looking in HKLM\SOFTWARE, also try WOW6432Node
                            if (!found && hiveString.Equals("HKEY_LOCAL_MACHINE", StringComparison.OrdinalIgnoreCase) &&
                                subKey.StartsWith("SOFTWARE\\", StringComparison.OrdinalIgnoreCase) &&
                                !subKey.StartsWith("SOFTWARE\\WOW6432Node\\", StringComparison.OrdinalIgnoreCase))
                            {
                                var wow64SubKey = subKey.Insert(9, "WOW6432Node\\");
                                using var wow64Key = baseKey.OpenSubKey(wow64SubKey);
                                if (wow64Key != null && ExtractRegistryDetails(wow64Key, app))
                                {
                                    found = true;
                                }
                            }
                        }

                        // 3. If direct key still not found, search all uninstall keys by Name/DisplayName
                        if (!found)
                        {
                            found = TryFindUninstallRegistry(app);
                        }

                        // 4. Strict physical verification: app must genuinely exist on disk!
                        bool physicallyPresent = false;
                        if (found && !string.IsNullOrWhiteSpace(app.InstalledPath))
                        {
                            if (File.Exists(app.InstalledPath))
                            {
                                physicallyPresent = true;
                            }
                            else if (Directory.Exists(app.InstalledPath))
                            {
                                physicallyPresent = HasAnyFiles(app.InstalledPath);
                            }
                        }

                        if (found && !physicallyPresent)
                        {
                            var diskFound = FindAppOnDisk(app);
                            if (!string.IsNullOrWhiteSpace(diskFound))
                            {
                                app.InstalledPath = diskFound;
                                physicallyPresent = true;
                            }
                        }

                        app.IsInstalled = found && physicallyPresent;
                        if (!app.IsInstalled)
                        {
                            app.InstalledVersion = string.Empty;
                            app.InstalledPath = string.Empty;
                            app.InstalledSizeDisplay = string.Empty;
                        }
                    }
                }

                // Evaluate whether an update is available
                if (app.IsInstalled)
                {
                    if (app.DetectionMethod.Equals("path", StringComparison.OrdinalIgnoreCase))
                    {
                        var previousPath = app.InstalledPath;
                        TryFindUninstallRegistry(app);
                        if (!string.IsNullOrWhiteSpace(previousPath) && File.Exists(previousPath))
                        {
                            app.InstalledPath = previousPath;
                        }
                    }

                    // Instant single-file size check
                    if (string.IsNullOrWhiteSpace(app.InstalledSizeDisplay) && !string.IsNullOrWhiteSpace(app.InstalledPath) && File.Exists(app.InstalledPath))
                    {
                        try
                        {
                            var fi = new FileInfo(app.InstalledPath);
                            if (fi.Length > 0) app.InstalledSizeDisplay = DownloadService.FormatBytes(fi.Length);
                        }
                        catch { }
                    }

                    app.HasUpdate = CheckIfUpdateAvailable(app.Version, app.InstalledVersion);
                }
                else
                {
                    app.HasUpdate = false;
                    app.InstalledVersion = string.Empty;
                    if (app.InstallerType.Equals("portable", StringComparison.OrdinalIgnoreCase) ||
                        app.InstallerType.Equals("zip", StringComparison.OrdinalIgnoreCase) ||
                        app.DetectionMethod.Equals("path", StringComparison.OrdinalIgnoreCase))
                    {
                        var targetExp = Environment.ExpandEnvironmentVariables(app.DetectionValue);
                        app.InstalledPath = $"Не установлено (целевой путь: {targetExp})";
                    }
                    else
                    {
                        app.InstalledPath = "Не установлено";
                    }
                    app.InstalledSizeDisplay = string.Empty;
                }
            }
            catch
            {
                // Fallback on error
                app.IsInstalled = false;
                app.HasUpdate = false;
            }
        }

        private bool ExtractRegistryDetails(RegistryKey key, AppItem app)
        {
            var dispVer = key.GetValue("DisplayVersion")?.ToString();
            string foundVer = !string.IsNullOrWhiteSpace(dispVer) ? CleanVersionString(dispVer.Trim()) : string.Empty;

            string foundPath = string.Empty;

            // 1. Check InstallLocation
            var loc = key.GetValue("InstallLocation")?.ToString();
            if (!string.IsNullOrWhiteSpace(loc))
            {
                var cleanLoc = Environment.ExpandEnvironmentVariables(loc.Trim().Trim('\"'));
                if (Directory.Exists(cleanLoc) && !Allens.Helpers.PathSafetyHelper.IsProtectedSystemPath(cleanLoc) && HasAnyFiles(cleanLoc))
                {
                    foundPath = cleanLoc;
                }
            }

            // 2. If path still empty, check UninstallString / QuietUninstallString
            if (string.IsNullOrWhiteSpace(foundPath))
            {
                var uninst = (key.GetValue("UninstallString") ?? key.GetValue("QuietUninstallString"))?.ToString();
                if (!string.IsNullOrWhiteSpace(uninst))
                {
                    var cleanExe = ExtractExeFromCommand(uninst);
                    if (!string.IsNullOrWhiteSpace(cleanExe) && File.Exists(cleanExe) && !IsGenericSystemTool(cleanExe))
                    {
                        var uninstDir = Path.GetDirectoryName(cleanExe);
                        if (!string.IsNullOrWhiteSpace(uninstDir) && Directory.Exists(uninstDir) && !Allens.Helpers.PathSafetyHelper.IsProtectedSystemPath(uninstDir))
                        {
                            foundPath = uninstDir;
                        }
                    }
                }
            }

            // 3. If path still empty, check DisplayIcon
            if (string.IsNullOrWhiteSpace(foundPath))
            {
                var iconVal = key.GetValue("DisplayIcon")?.ToString();
                if (!string.IsNullOrWhiteSpace(iconVal))
                {
                    var cleanIconPath = iconVal.Split(',')[0].Trim('\"', ' ');
                    var expIcon = Environment.ExpandEnvironmentVariables(cleanIconPath);
                    if (File.Exists(expIcon) && !IsGenericSystemTool(expIcon))
                    {
                        var iconDir = Path.GetDirectoryName(expIcon);
                        if (!string.IsNullOrWhiteSpace(iconDir) && Directory.Exists(iconDir) && !Allens.Helpers.PathSafetyHelper.IsProtectedSystemPath(iconDir))
                        {
                            foundPath = iconDir;
                        }

                        if (string.IsNullOrWhiteSpace(foundVer))
                        {
                            try
                            {
                                var ver = FileVersionInfo.GetVersionInfo(expIcon);
                                foundVer = CleanVersionString(ver.ProductVersion ?? ver.FileVersion ?? string.Empty);
                            }
                            catch { }
                        }
                    }
                }
            }

            // 4. If path still empty, check disk files directly (ProgramFiles, AppData, CleanupPaths)
            if (string.IsNullOrWhiteSpace(foundPath))
            {
                var diskDir = FindAppOnDisk(app);
                if (!string.IsNullOrWhiteSpace(diskDir))
                {
                    foundPath = diskDir;
                }
            }

            // === STRICT PHYSICAL VERIFICATION ===
            // If not a single physical file or directory exists on disk, this is an ORPHANED ghost registry entry!
            if (string.IsNullOrWhiteSpace(foundPath))
            {
                return false;
            }

            // App is verified to physically exist on disk!
            app.InstalledPath = foundPath;
            if (!string.IsNullOrWhiteSpace(foundVer))
            {
                app.InstalledVersion = foundVer;
            }

            // Read EstimatedSize (DWORD in KB) only for genuinely existing applications
            var estVal = key.GetValue("EstimatedSize");
            if (estVal != null && long.TryParse(estVal.ToString(), out var sizeKb) && sizeKb > 0)
            {
                app.InstalledSizeDisplay = DownloadService.FormatBytes(sizeKb * 1024L);
            }

            return true;
        }

        private bool TryFindUninstallRegistry(AppItem app)
        {
            var roots = new[] { Registry.CurrentUser, Registry.LocalMachine };
            foreach (var root in roots)
            {
                var paths = new[]
                {
                    @"Software\Microsoft\Windows\CurrentVersion\Uninstall",
                    @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
                };

                foreach (var p in paths)
                {
                    using var uninstKey = root.OpenSubKey(p);
                    if (uninstKey == null) continue;

                    // Direct key check
                    var directCandidates = new[] { app.Name, app.Id, $"{app.Name}_is1", $"{app.Id}_is1" };
                    foreach (var dc in directCandidates)
                    {
                        using var sub = uninstKey.OpenSubKey(dc);
                        if (sub != null && ExtractRegistryDetails(sub, app))
                        {
                            return true;
                        }
                    }

                    // Enumerate subkeys
                    foreach (var subName in uninstKey.GetSubKeyNames())
                    {
                        // Filter out Steam game entries if searching for Steam client
                        if (subName.StartsWith("Steam App ", StringComparison.OrdinalIgnoreCase) &&
                            !app.Id.StartsWith("steam_app", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        using var sub = uninstKey.OpenSubKey(subName);
                        if (sub == null) continue;

                        var disp = sub.GetValue("DisplayName")?.ToString();

                        bool isMatch = IsRegistryNameMatch(subName, app.Name, app.Id) ||
                                       (!string.IsNullOrWhiteSpace(disp) && IsRegistryNameMatch(disp, app.Name, app.Id));

                        if (isMatch)
                        {
                            // Strictly verify physical presence on disk!
                            if (ExtractRegistryDetails(sub, app))
                            {
                                return true;
                            }
                        }
                    }
                }
            }
            return false;
        }

        private string? FindAppOnDisk(AppItem app)
        {
            // 1. Check DetectionValue if it's a file or directory path
            if (!string.IsNullOrWhiteSpace(app.DetectionValue) && !app.DetectionValue.StartsWith("HKEY_", StringComparison.OrdinalIgnoreCase))
            {
                var exp = Environment.ExpandEnvironmentVariables(app.DetectionValue);
                if (File.Exists(exp)) return Path.GetDirectoryName(exp) ?? exp;
                if (Directory.Exists(exp) && HasAnyFiles(exp)) return exp;
            }

            // 2. Check CleanupPaths from catalog
            if (app.CleanupPaths != null)
            {
                foreach (var cp in app.CleanupPaths)
                {
                    var exp = Environment.ExpandEnvironmentVariables(cp);
                    if (Directory.Exists(exp) && HasAnyFiles(exp))
                    {
                        return exp;
                    }
                }
            }

            // 3. Check common program locations
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

            var names = new List<string>();
            if (!string.IsNullOrWhiteSpace(app.Name))
            {
                names.Add(app.Name);
                names.Add(app.Name.Replace(" ", ""));
            }
            if (!string.IsNullOrWhiteSpace(app.Id))
            {
                names.Add(app.Id);
            }

            var baseFolders = new[]
            {
                programFiles,
                programFilesX86,
                Path.Combine(localAppData, "Programs"),
                localAppData,
                appData
            };

            foreach (var baseF in baseFolders)
            {
                if (string.IsNullOrWhiteSpace(baseF) || !Directory.Exists(baseF)) continue;

                foreach (var n in names)
                {
                    var targetDir = Path.Combine(baseF, n);
                    if (Directory.Exists(targetDir) && !Allens.Helpers.PathSafetyHelper.IsProtectedSystemPath(targetDir))
                    {
                        if (HasAnyFiles(targetDir))
                        {
                            return targetDir;
                        }
                    }
                }
            }

            return null;
        }

        private static bool HasAnyFiles(string dirPath)
        {
            try
            {
                return Directory.EnumerateFileSystemEntries(dirPath).Any();
            }
            catch
            {
                return false;
            }
        }

        private static string ExtractExeFromCommand(string command)
        {
            if (string.IsNullOrWhiteSpace(command)) return string.Empty;
            var expanded = Environment.ExpandEnvironmentVariables(command).Trim();

            if (expanded.StartsWith("\""))
            {
                var endQ = expanded.IndexOf('\"', 1);
                if (endQ > 0)
                {
                    return expanded.Substring(1, endQ - 1).Trim();
                }
            }

            var exeIdx = expanded.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            if (exeIdx > 0)
            {
                return expanded.Substring(0, exeIdx + 4).Trim('\"', ' ');
            }

            if (expanded.Contains(" "))
            {
                return expanded.Split(' ')[0].Trim('\"', ' ');
            }

            return expanded.Trim('\"', ' ');
        }

        private static bool IsGenericSystemTool(string exePath)
        {
            if (string.IsNullOrWhiteSpace(exePath)) return false;
            var name = Path.GetFileName(exePath);
            return name.Equals("msiexec.exe", StringComparison.OrdinalIgnoreCase) ||
                   name.Equals("rundll32.exe", StringComparison.OrdinalIgnoreCase) ||
                   name.Equals("cmd.exe", StringComparison.OrdinalIgnoreCase) ||
                   name.Equals("powershell.exe", StringComparison.OrdinalIgnoreCase) ||
                   name.Equals("pwsh.exe", StringComparison.OrdinalIgnoreCase) ||
                   name.Equals("conhost.exe", StringComparison.OrdinalIgnoreCase) ||
                   name.Equals("regsvr32.exe", StringComparison.OrdinalIgnoreCase) ||
                   name.Equals("reg.exe", StringComparison.OrdinalIgnoreCase) ||
                   name.Equals("explorer.exe", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsRegistryNameMatch(string candidate, string targetName, string targetId)
        {
            if (string.IsNullOrWhiteSpace(candidate)) return false;

            // Direct exact match
            if (candidate.Equals(targetName, StringComparison.OrdinalIgnoreCase) ||
                candidate.Equals(targetId, StringComparison.OrdinalIgnoreCase) ||
                candidate.Equals($"{targetName}_is1", StringComparison.OrdinalIgnoreCase) ||
                candidate.Equals($"{targetId}_is1", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // Word-boundary / prefix matches
            if (!string.IsNullOrWhiteSpace(targetName) && targetName.Length >= 3)
            {
                if (candidate.StartsWith(targetName + " ", StringComparison.OrdinalIgnoreCase) ||
                    candidate.StartsWith(targetName + " (", StringComparison.OrdinalIgnoreCase) ||
                    candidate.StartsWith(targetName + " -", StringComparison.OrdinalIgnoreCase) ||
                    candidate.StartsWith(targetName + "-", StringComparison.OrdinalIgnoreCase) ||
                    candidate.StartsWith(targetName + "_", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            if (!string.IsNullOrWhiteSpace(targetId) && targetId.Length >= 3)
            {
                if (candidate.StartsWith(targetId + " ", StringComparison.OrdinalIgnoreCase) ||
                    candidate.StartsWith(targetId + "-", StringComparison.OrdinalIgnoreCase) ||
                    candidate.StartsWith(targetId + "_", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            // Also check if candidate contains the full name as a whole word or compound (e.g. BraveSoftware Brave-Browser)
            if (!string.IsNullOrWhiteSpace(targetName) && targetName.Length >= 4)
            {
                if (candidate.IndexOf(targetName, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }

            return false;
        }

        public void MeasureFolderSizesBackground(IEnumerable<AppItem> apps)
        {
            var appsList = apps.ToList();
            System.Threading.Tasks.Task.Run(() =>
            {
                foreach (var app in appsList)
                {
                    if (app.IsInstalled && string.IsNullOrWhiteSpace(app.InstalledSizeDisplay))
                    {
                        TryMeasureFolderSize(app);
                    }
                }
            });
        }

        public void TryMeasureFolderSize(AppItem app)
        {
            if (!string.IsNullOrWhiteSpace(app.InstalledSizeDisplay)) return;
            if (string.IsNullOrWhiteSpace(app.InstalledPath)) return;
            if (Allens.Helpers.PathSafetyHelper.IsProtectedSystemPath(app.InstalledPath)) return;

            try
            {
                if (File.Exists(app.InstalledPath))
                {
                    var parentDir = Path.GetDirectoryName(app.InstalledPath);
                    if (!string.IsNullOrWhiteSpace(parentDir) && Directory.Exists(parentDir) && !Allens.Helpers.PathSafetyHelper.IsProtectedSystemPath(parentDir))
                    {
                        var di = new DirectoryInfo(parentDir);
                        long totalBytes = 0;
                        foreach (var file in di.EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MaxRecursionDepth = 5 }))
                        {
                            totalBytes += file.Length;
                        }
                        if (totalBytes > 0)
                        {
                            var formatted = DownloadService.FormatBytes(totalBytes);
                            System.Windows.Application.Current?.Dispatcher?.Invoke(() =>
                            {
                                app.InstalledSizeDisplay = formatted;
                            });
                            return;
                        }
                    }

                    var fi = new FileInfo(app.InstalledPath);
                    if (fi.Length > 0)
                    {
                        var formatted = DownloadService.FormatBytes(fi.Length);
                        System.Windows.Application.Current?.Dispatcher?.Invoke(() =>
                        {
                            app.InstalledSizeDisplay = formatted;
                        });
                        return;
                    }
                }
                else if (Directory.Exists(app.InstalledPath))
                {
                    var di = new DirectoryInfo(app.InstalledPath);
                    long totalBytes = 0;
                    foreach (var file in di.EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MaxRecursionDepth = 5 }))
                    {
                        totalBytes += file.Length;
                    }
                    if (totalBytes > 0)
                    {
                        var formatted = DownloadService.FormatBytes(totalBytes);
                        System.Windows.Application.Current?.Dispatcher?.Invoke(() =>
                        {
                            app.InstalledSizeDisplay = formatted;
                        });
                    }
                }
            }
            catch { }
        }

        private bool CheckIfUpdateAvailable(string catalogVerStr, string installedVerStr)
        {
            if (string.IsNullOrWhiteSpace(catalogVerStr) || string.IsNullOrWhiteSpace(installedVerStr))
                return false;

            if (catalogVerStr.Equals("Latest", StringComparison.OrdinalIgnoreCase))
                return false;

            var cleanCat = CleanVersionString(catalogVerStr);
            var cleanInst = CleanVersionString(installedVerStr);

            if (Version.TryParse(NormalizeVersion(cleanCat), out var catVer) && 
                Version.TryParse(NormalizeVersion(cleanInst), out var instVer))
            {
                return catVer > instVer;
            }

            return false;
        }

        private static string NormalizeVersion(string v)
        {
            var parts = v.Split('.');
            if (parts.Length == 1) return v + ".0.0.0";
            if (parts.Length == 2) return v + ".0.0";
            if (parts.Length == 3) return v + ".0";
            return v;
        }

        private static string CleanVersionString(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
            var match = Regex.Match(raw, @"^[\d\.]+");
            return match.Success ? match.Value.TrimEnd('.') : raw.Trim();
        }

        private RegistryKey? GetBaseKey(string hiveString)
        {
            return hiveString.ToUpperInvariant() switch
            {
                "HKEY_CLASSES_ROOT" => Registry.ClassesRoot,
                "HKEY_CURRENT_USER" => Registry.CurrentUser,
                "HKEY_LOCAL_MACHINE" => Registry.LocalMachine,
                "HKEY_USERS" => Registry.Users,
                "HKEY_CURRENT_CONFIG" => Registry.CurrentConfig,
                _ => null,
            };
        }

        private static string? FindMatchingExecutable(AppItem app, string targetDir)
        {
            try
            {
                if (!Directory.Exists(targetDir)) return null;

                var allExes = Directory.GetFiles(targetDir, "*.exe", SearchOption.TopDirectoryOnly);
                return allExes.FirstOrDefault(f =>
                {
                    var fname = Path.GetFileNameWithoutExtension(f);
                    if (fname.Contains("uninstall", StringComparison.OrdinalIgnoreCase) ||
                        fname.Contains("unins", StringComparison.OrdinalIgnoreCase) ||
                        fname.Contains("crash", StringComparison.OrdinalIgnoreCase) ||
                        fname.Contains("setup", StringComparison.OrdinalIgnoreCase) ||
                        fname.Contains("runner", StringComparison.OrdinalIgnoreCase) ||
                        fname.Contains("sandbox", StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }

                    return fname.Equals(app.Id, StringComparison.OrdinalIgnoreCase) ||
                           fname.StartsWith(app.Id, StringComparison.OrdinalIgnoreCase) ||
                           fname.StartsWith(app.Name.Replace(" ", ""), StringComparison.OrdinalIgnoreCase);
                });
            }
            catch
            {
                return null;
            }
        }
    }
}
