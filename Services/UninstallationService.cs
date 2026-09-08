using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using Allens.Models;
using Allens.Helpers;

namespace Allens.Services
{
    public class UninstallationService : IUninstallationService
    {
        public async Task<InstallationResult> UninstallAsync(AppItem app, CancellationToken cancellationToken = default)
        {
            try
            {
                // Ensure any running instances of the application are closed before attempting uninstallation
                await ProcessHelper.CloseRunningAppProcessesAsync(app);

                // 1. If application is marked as portable/zip or directory-based uninstallation:
                if (app.InstallerType.Equals("zip", StringComparison.OrdinalIgnoreCase) ||
                    app.InstallerType.Equals("portable", StringComparison.OrdinalIgnoreCase) ||
                    app.UninstallMethod.Equals("directory", StringComparison.OrdinalIgnoreCase))
                {
                    return await UninstallDirectoryOrPortableAsync(app);
                }

                string? uninstallerCmd = null;
                string extraArguments = app.UninstallArguments ?? string.Empty;

                // 2. Check if uninstallMethod is registry or if uninstallValue is a registry key
                if (app.UninstallMethod.Equals("registry", StringComparison.OrdinalIgnoreCase) ||
                    (!string.IsNullOrWhiteSpace(app.UninstallValue) && app.UninstallValue.StartsWith("HKEY_", StringComparison.OrdinalIgnoreCase)))
                {
                    uninstallerCmd = FindUninstallCommandFromRegistry(app);
                }

                // 3. Check if uninstallMethod is path
                if (string.IsNullOrWhiteSpace(uninstallerCmd) && app.UninstallMethod.Equals("path", StringComparison.OrdinalIgnoreCase))
                {
                    var expandedPath = Environment.ExpandEnvironmentVariables(app.UninstallValue);
                    if (File.Exists(expandedPath))
                    {
                        var fileName = Path.GetFileName(expandedPath);
                        if (fileName.Contains("unins", StringComparison.OrdinalIgnoreCase) ||
                            fileName.Contains("uninstall", StringComparison.OrdinalIgnoreCase) ||
                            fileName.Contains("setup", StringComparison.OrdinalIgnoreCase) ||
                            fileName.Contains("update", StringComparison.OrdinalIgnoreCase))
                        {
                            uninstallerCmd = $"\"{expandedPath}\"";
                        }
                        else
                        {
                            // Path is the application executable itself, not an uninstaller -> treat as portable removal
                            return await UninstallDirectoryOrPortableAsync(app);
                        }
                    }
                    else
                    {
                        // Path doesn't exist on disk, check registry for uninstaller
                        uninstallerCmd = FindUninstallCommandFromRegistry(app);
                    }
                }

                // 4. Global registry search fallback if still empty
                if (string.IsNullOrWhiteSpace(uninstallerCmd))
                {
                    uninstallerCmd = FindUninstallCommandFromRegistry(app);
                }

                // 5. Look for standard uninstaller in app.InstalledPath
                if (string.IsNullOrWhiteSpace(uninstallerCmd) && !string.IsNullOrWhiteSpace(app.InstalledPath) && Directory.Exists(app.InstalledPath))
                {
                    var candidates = new[] { "unins000.exe", "uninstall.exe", "uninst.exe", "setup.exe", "Uninstall.exe" };
                    foreach (var cand in candidates)
                    {
                        var candPath = Path.Combine(app.InstalledPath, cand);
                        if (File.Exists(candPath))
                        {
                            uninstallerCmd = $"\"{candPath}\"";
                            break;
                        }
                    }
                }

                // 6. If no uninstaller command found at all:
                if (string.IsNullOrWhiteSpace(uninstallerCmd))
                {
                    // Check if installed files exist on disk
                    bool hasFiles = (!string.IsNullOrWhiteSpace(app.InstalledPath) && Directory.Exists(app.InstalledPath)) ||
                                    (!string.IsNullOrWhiteSpace(app.DetectionValue) && !app.DetectionValue.StartsWith("HKEY_", StringComparison.OrdinalIgnoreCase) &&
                                     (File.Exists(Environment.ExpandEnvironmentVariables(app.DetectionValue)) || Directory.Exists(Environment.ExpandEnvironmentVariables(app.DetectionValue))));

                    if (hasFiles)
                    {
                        return await UninstallDirectoryOrPortableAsync(app);
                    }

                    // Application files and uninstaller are already absent -> it is already uninstalled!
                    CleanupLeftovers(app);
                    app.IsInstalled = false;
                    return new InstallationResult { IsSuccess = true, ExitCode = 0 };
                }

                // 7. Execute the uninstaller
                return await ExecuteUninstallerAsync(app, uninstallerCmd, extraArguments, cancellationToken);
            }
            catch (TimeoutException ex)
            {
                return new InstallationResult { IsSuccess = false, ErrorMessage = ex.Message };
            }
            catch (OperationCanceledException)
            {
                return new InstallationResult { IsSuccess = false, ErrorMessage = "Удаление отменено пользователем." };
            }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                return new InstallationResult { IsSuccess = false, ErrorMessage = "Удаление отменено пользователем (UAC отклонен)." };
            }
            catch (Exception ex)
            {
                if (IsAppAlreadyGone(app))
                {
                    CleanupLeftovers(app);
                    app.IsInstalled = false;
                    return new InstallationResult { IsSuccess = true, ExitCode = 0 };
                }
                return new InstallationResult { IsSuccess = false, ErrorMessage = ex.Message };
            }
        }

        private async Task<InstallationResult> ExecuteUninstallerAsync(AppItem app, string command, string extraArgs, CancellationToken cancellationToken)
        {
            ParseCommand(command, out string executable, out string arguments);

            // Handle MsiExec
            if (executable.Equals("msiexec", StringComparison.OrdinalIgnoreCase) ||
                executable.Equals("msiexec.exe", StringComparison.OrdinalIgnoreCase) ||
                executable.EndsWith("msiexec.exe", StringComparison.OrdinalIgnoreCase))
            {
                executable = "msiexec.exe";
                if (!arguments.Contains("/qn", StringComparison.OrdinalIgnoreCase) && !arguments.Contains("/quiet", StringComparison.OrdinalIgnoreCase))
                {
                    arguments += " /qn /norestart";
                }
            }
            else
            {
                if (!File.Exists(executable))
                {
                    // The uninstaller executable is missing. If app files are already gone, consider uninstalled!
                    if (IsAppAlreadyGone(app))
                    {
                        CleanupLeftovers(app);
                        app.IsInstalled = false;
                        return new InstallationResult { IsSuccess = true, ExitCode = 0 };
                    }

                    if (!string.IsNullOrWhiteSpace(app.InstalledPath) && Directory.Exists(app.InstalledPath))
                    {
                        return await UninstallDirectoryOrPortableAsync(app);
                    }

                    CleanupLeftovers(app);
                    app.IsInstalled = false;
                    return new InstallationResult { IsSuccess = true, ExitCode = 0 };
                }

                if (!string.IsNullOrWhiteSpace(extraArgs))
                {
                    if (!arguments.Contains(extraArgs, StringComparison.OrdinalIgnoreCase))
                    {
                        arguments = $"{arguments} {extraArgs}".Trim();
                    }
                }
                else
                {
                    if (executable.Contains("unins000.exe", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!arguments.Contains("/VERYSILENT", StringComparison.OrdinalIgnoreCase))
                        {
                            arguments = $"{arguments} /VERYSILENT /NORESTART".Trim();
                        }
                    }
                }
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = executable,
                Arguments = arguments,
                UseShellExecute = true,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                Verb = app.RequiresAdmin ? "runas" : ""
            };

            using var process = Process.Start(startInfo);
            if (process == null)
            {
                if (IsAppAlreadyGone(app))
                {
                    CleanupLeftovers(app);
                    app.IsInstalled = false;
                    return new InstallationResult { IsSuccess = true, ExitCode = 0 };
                }
                return new InstallationResult { IsSuccess = false, ErrorMessage = "Не удалось запустить процесс удаления." };
            }

            await ProcessHelper.WaitForExitWithTimeoutAsync(process, TimeSpan.FromMinutes(15), cancellationToken);

            // Allow brief delay for uninstaller child processes/threads (e.g. NSIS/Inno helper spawns) to finish and release file locks
            await Task.Delay(1500, cancellationToken);

            // Execute deep leftover cleanup (Geek Uninstaller style)
            CleanupLeftovers(app);

            // Exit code 0 (success), 3010 (reboot required), 1605 (product already uninstalled)
            bool isSuccess = process.ExitCode == 0 || process.ExitCode == 3010 || process.ExitCode == 1605 || IsAppAlreadyGone(app);

            if (isSuccess)
            {
                app.IsInstalled = false;
            }

            return new InstallationResult
            {
                IsSuccess = isSuccess,
                ExitCode = process.ExitCode,
                ErrorMessage = isSuccess ? string.Empty : $"Процесс удаления завершился с кодом {process.ExitCode}"
            };
        }

        public async Task<InstallationResult> ForceUninstallAsync(AppItem app, CancellationToken cancellationToken = default)
        {
            try
            {
                // 1. Force close and terminate any running processes associated with the application
                await ProcessHelper.CloseRunningAppProcessesAsync(app);
                KillAppProcesses(app);

                // 2. Deep clean files, directories, shortcuts, appdata, registry, autostart, and uninstall keys
                CleanupLeftovers(app);

                // 3. Remove from user PATH if installed path was registered
                if (!string.IsNullOrWhiteSpace(app.InstalledPath))
                {
                    InstallationService.RemoveDirectoryFromUserPath(app.InstalledPath);
                }

                // 4. Reset installed state and metadata
                app.IsInstalled = false;
                app.InstalledVersion = string.Empty;
                app.InstalledPath = string.Empty;
                app.InstalledSizeDisplay = string.Empty;
                app.HasUpdate = false;

                return new InstallationResult
                {
                    IsSuccess = true,
                    ExitCode = 0,
                    ErrorMessage = string.Empty
                };
            }
            catch (Exception ex)
            {
                return new InstallationResult
                {
                    IsSuccess = false,
                    ErrorMessage = $"Ошибка при принудительном удалении: {ex.Message}"
                };
            }
        }

        private async Task<InstallationResult> UninstallDirectoryOrPortableAsync(AppItem app)
        {
            await ProcessHelper.CloseRunningAppProcessesAsync(app);

            string targetPath = string.Empty;
            if (!string.IsNullOrWhiteSpace(app.UninstallValue) && !app.UninstallValue.StartsWith("HKEY_", StringComparison.OrdinalIgnoreCase))
            {
                targetPath = Environment.ExpandEnvironmentVariables(app.UninstallValue);
            }
            else if (!string.IsNullOrWhiteSpace(app.InstalledPath) && Directory.Exists(app.InstalledPath))
            {
                targetPath = app.InstalledPath;
            }
            else if (!string.IsNullOrWhiteSpace(app.DetectionValue) && !app.DetectionValue.StartsWith("HKEY_", StringComparison.OrdinalIgnoreCase))
            {
                targetPath = Environment.ExpandEnvironmentVariables(app.DetectionValue);
            }

            if (!string.IsNullOrWhiteSpace(targetPath))
            {
                if (Allens.Helpers.PathSafetyHelper.IsProtectedSystemPath(targetPath))
                {
                    return new InstallationResult
                    {
                        IsSuccess = false,
                        ErrorMessage = "Удаление отменено: указанный путь защищен от удаления."
                    };
                }

                if (File.Exists(targetPath))
                {
                    Allens.Helpers.PathSafetyHelper.SafeDeleteFile(targetPath, allowElevation: true);
                    var parent = Path.GetDirectoryName(targetPath);
                    if (!string.IsNullOrWhiteSpace(parent) && Directory.Exists(parent) && !Allens.Helpers.PathSafetyHelper.IsProtectedSystemPath(parent))
                    {
                        var dirName = Path.GetFileName(parent);
                        if (dirName.Equals(app.Name, StringComparison.OrdinalIgnoreCase) || dirName.Equals(app.Id, StringComparison.OrdinalIgnoreCase))
                        {
                            Allens.Helpers.PathSafetyHelper.SafeDeleteDirectory(parent, cleanParentIfEmpty: true, allowElevation: true);
                        }
                    }
                }
                else if (Directory.Exists(targetPath))
                {
                    Allens.Helpers.PathSafetyHelper.SafeDeleteDirectory(targetPath, cleanParentIfEmpty: true, allowElevation: true);
                }
            }

            CleanupLeftovers(app);
            app.IsInstalled = false;
            return new InstallationResult { IsSuccess = true, ExitCode = 0 };
        }

        private string? FindUninstallCommandFromRegistry(AppItem app)
        {
            var roots = new[] { Registry.LocalMachine, Registry.CurrentUser };
            var uninstallKeys = new[]
            {
                @"Software\Microsoft\Windows\CurrentVersion\Uninstall",
                @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
            };

            // 1. Explicit path in UninstallValue
            if (!string.IsNullOrWhiteSpace(app.UninstallValue) && app.UninstallValue.StartsWith("HKEY_", StringComparison.OrdinalIgnoreCase))
            {
                var parts = app.UninstallValue.Split(new[] { '\\' }, 2);
                if (parts.Length == 2)
                {
                    var baseKey = GetBaseKey(parts[0]);
                    if (baseKey != null)
                    {
                        using var key = baseKey.OpenSubKey(parts[1]);
                        if (key != null)
                        {
                            var cmd = (key.GetValue("QuietUninstallString") ?? key.GetValue("UninstallString"))?.ToString();
                            if (!string.IsNullOrWhiteSpace(cmd)) return cmd;
                        }
                    }
                }
            }

            // 2. Iterate roots and uninstall key paths
            foreach (var root in roots)
            {
                foreach (var uKeyPath in uninstallKeys)
                {
                    using var uKey = root.OpenSubKey(uKeyPath);
                    if (uKey == null) continue;

                    // Direct name checks
                    var directNames = new[] { app.Id, app.Name, $"{app.Name}_is1", $"{app.Id}_is1" };
                    foreach (var dn in directNames)
                    {
                        using var sub = uKey.OpenSubKey(dn);
                        if (sub != null)
                        {
                            var cmd = (sub.GetValue("QuietUninstallString") ?? sub.GetValue("UninstallString"))?.ToString();
                            if (!string.IsNullOrWhiteSpace(cmd)) return cmd;
                        }
                    }

                    // Enumerate all subkeys to find by subkey name or DisplayName
                    foreach (var subName in uKey.GetSubKeyNames())
                    {
                        bool subNameMatch = subName.IndexOf(app.Name, StringComparison.OrdinalIgnoreCase) >= 0 ||
                                            (!string.IsNullOrWhiteSpace(app.Id) && subName.IndexOf(app.Id, StringComparison.OrdinalIgnoreCase) >= 0);

                        using var sub = uKey.OpenSubKey(subName);
                        if (sub == null) continue;

                        var dispName = sub.GetValue("DisplayName")?.ToString();
                        bool dispNameMatch = !string.IsNullOrWhiteSpace(dispName) &&
                                             (dispName.Equals(app.Name, StringComparison.OrdinalIgnoreCase) ||
                                              dispName.IndexOf(app.Name, StringComparison.OrdinalIgnoreCase) >= 0 ||
                                              app.Name.IndexOf(dispName, StringComparison.OrdinalIgnoreCase) >= 0);

                        if (subNameMatch || dispNameMatch)
                        {
                            var cmd = (sub.GetValue("QuietUninstallString") ?? sub.GetValue("UninstallString"))?.ToString();
                            if (!string.IsNullOrWhiteSpace(cmd)) return cmd;
                        }
                    }
                }
            }

            return null;
        }

        private void KillAppProcesses(AppItem app)
        {
            var processNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(app.Id)) processNames.Add(app.Id);
            if (!string.IsNullOrWhiteSpace(app.Name))
            {
                processNames.Add(app.Name);
                processNames.Add(app.Name.Replace(" ", ""));
                processNames.Add(app.Name.Replace(" ", "-"));
            }

            if (!string.IsNullOrWhiteSpace(app.DetectionValue) && !app.DetectionValue.StartsWith("HKEY_", StringComparison.OrdinalIgnoreCase))
            {
                var exeName = Path.GetFileNameWithoutExtension(Environment.ExpandEnvironmentVariables(app.DetectionValue));
                if (!string.IsNullOrWhiteSpace(exeName)) processNames.Add(exeName);
            }

            // Also inspect installed folder for any executables (helpers, background daemons, updaters)
            if (!string.IsNullOrWhiteSpace(app.InstalledPath) && Directory.Exists(app.InstalledPath) && !Allens.Helpers.PathSafetyHelper.IsProtectedSystemPath(app.InstalledPath))
            {
                try
                {
                    var di = new DirectoryInfo(app.InstalledPath);
                    foreach (var exeFile in di.EnumerateFiles("*.exe", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 2, IgnoreInaccessible = true }))
                    {
                        var name = Path.GetFileNameWithoutExtension(exeFile.Name);
                        if (!string.IsNullOrWhiteSpace(name)) processNames.Add(name);
                    }
                }
                catch { }
            }

            foreach (var pName in processNames)
            {
                // Never kill critical system or current application processes
                if (string.Equals(pName, "explorer", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(pName, "Allens", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(pName, "devenv", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(pName, "system", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(pName, "cmd", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(pName, "powershell", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(pName, "pwsh", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(pName, "conhost", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(pName, "svchost", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    foreach (var proc in Process.GetProcessesByName(pName))
                    {
                        using (proc)
                        {
                            try
                            {
                                if (proc.Id == Environment.ProcessId || 
                                    string.Equals(proc.ProcessName, "explorer", StringComparison.OrdinalIgnoreCase) ||
                                    string.Equals(proc.ProcessName, "Allens", StringComparison.OrdinalIgnoreCase))
                                {
                                    continue;
                                }

                                proc.Kill(entireProcessTree: true);
                                proc.WaitForExit(1000);
                            }
                            catch { }
                        }
                    }
                }
                catch { }
            }
        }

        private void CleanupLeftovers(AppItem app)
        {
            // 1. Terminate any remaining app processes
            KillAppProcesses(app);

            // 2. Deep clean installed directory & detection targets
            CleanupInstallDirectory(app);

            // 3. Deep clean shortcuts from Desktop, Start Menu, and Startup
            CleanupShortcuts(app);

            // 4. Deep clean user and application data directories (%APPDATA%, %LOCALAPPDATA%, %PROGRAMDATA%, etc.)
            CleanupUserDataDirectories(app);

            // 5. Clean manifest explicit CleanupPaths
            CleanupExplicitPaths(app);

            // 6. Deep clean registry software keys
            CleanupRegistrySoftwareKeys(app);

            // 7. Clean registry autostart (Run / RunOnce) entries
            CleanupRegistryAutostart(app);

            // 8. Clean leftover registry Uninstall entries
            CleanupRegistryUninstallKeys(app);
        }

        private void CleanupInstallDirectory(AppItem app)
        {
            var targets = new List<string>();
            if (!string.IsNullOrWhiteSpace(app.InstalledPath))
                targets.Add(app.InstalledPath);

            if (!string.IsNullOrWhiteSpace(app.DetectionValue) && !app.DetectionValue.StartsWith("HKEY_", StringComparison.OrdinalIgnoreCase))
            {
                var exp = Environment.ExpandEnvironmentVariables(app.DetectionValue);
                if (File.Exists(exp))
                {
                    targets.Add(exp);
                    var parent = Path.GetDirectoryName(exp);
                    if (!string.IsNullOrWhiteSpace(parent)) targets.Add(parent);
                }
                else if (Directory.Exists(exp))
                {
                    targets.Add(exp);
                }
            }

            foreach (var t in targets.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (Allens.Helpers.PathSafetyHelper.IsProtectedSystemPath(t)) continue;

                if (File.Exists(t))
                {
                    Allens.Helpers.PathSafetyHelper.SafeDeleteFile(t);
                }
                else if (Directory.Exists(t))
                {
                    var dirName = Path.GetFileName(t);
                    if (MatchesAppName(dirName, app))
                    {
                        Allens.Helpers.PathSafetyHelper.SafeDeleteDirectory(t, cleanParentIfEmpty: true);
                    }
                    else
                    {
                        Allens.Helpers.PathSafetyHelper.SafeDeleteDirectory(t, cleanParentIfEmpty: false);
                    }
                }
            }
        }

        private void CleanupShortcuts(AppItem app)
        {
            var shortcutDirs = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), "Programs"),
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Microsoft\Windows\Start Menu\Programs\Startup"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"Microsoft\Windows\Start Menu\Programs\Startup")
            };

            var candidateNames = GetAppMatchNames(app);

            foreach (var dir in shortcutDirs)
            {
                if (!Directory.Exists(dir)) continue;

                foreach (var name in candidateNames)
                {
                    Allens.Helpers.PathSafetyHelper.SafeDeleteFile(Path.Combine(dir, $"{name}.lnk"));
                    Allens.Helpers.PathSafetyHelper.SafeDeleteFile(Path.Combine(dir, $"{name}.url"));

                    var subDir = Path.Combine(dir, name);
                    if (Directory.Exists(subDir))
                    {
                        Allens.Helpers.PathSafetyHelper.SafeDeleteDirectory(subDir);
                    }
                }

                try
                {
                    foreach (var file in Directory.EnumerateFiles(dir, "*.lnk", SearchOption.TopDirectoryOnly))
                    {
                        var fn = Path.GetFileNameWithoutExtension(file);
                        if (candidateNames.Any(c => fn.Equals(c, StringComparison.OrdinalIgnoreCase) ||
                                                    fn.StartsWith($"{c} ", StringComparison.OrdinalIgnoreCase) ||
                                                    fn.StartsWith($"{c} -", StringComparison.OrdinalIgnoreCase) ||
                                                    fn.StartsWith($"{c} (", StringComparison.OrdinalIgnoreCase)))
                        {
                            Allens.Helpers.PathSafetyHelper.SafeDeleteFile(file);
                        }
                    }
                }
                catch { }
            }
        }

        private void CleanupUserDataDirectories(AppItem app)
        {
            var baseDirs = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Saved Games"),
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
            };

            var matchNames = GetAppMatchNames(app);

            foreach (var baseDir in baseDirs)
            {
                if (string.IsNullOrWhiteSpace(baseDir) || !Directory.Exists(baseDir)) continue;

                foreach (var name in matchNames)
                {
                    if (string.IsNullOrWhiteSpace(name)) continue;

                    // Direct folder match
                    var targetDir = Path.Combine(baseDir, name);
                    if (Directory.Exists(targetDir) && !Allens.Helpers.PathSafetyHelper.IsProtectedSystemPath(targetDir))
                    {
                        Allens.Helpers.PathSafetyHelper.SafeDeleteDirectory(targetDir, cleanParentIfEmpty: false);
                    }

                    // Dot prefix (e.g. .appname) in UserProfile
                    if (baseDir.Equals(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), StringComparison.OrdinalIgnoreCase))
                    {
                        var dotDir = Path.Combine(baseDir, "." + name);
                        if (Directory.Exists(dotDir) && !Allens.Helpers.PathSafetyHelper.IsProtectedSystemPath(dotDir))
                        {
                            Allens.Helpers.PathSafetyHelper.SafeDeleteDirectory(dotDir, cleanParentIfEmpty: false);
                        }
                    }

                    // Publisher vendor subfolder (e.g. AppData\Local\Vendor\AppName)
                    if (!string.IsNullOrWhiteSpace(app.Publisher) && !IsGenericVendorName(app.Publisher))
                    {
                        var pubDir = Path.Combine(baseDir, app.Publisher);
                        if (Directory.Exists(pubDir) && !Allens.Helpers.PathSafetyHelper.IsProtectedSystemPath(pubDir))
                        {
                            var pubAppDir = Path.Combine(pubDir, name);
                            if (Directory.Exists(pubAppDir))
                            {
                                Allens.Helpers.PathSafetyHelper.SafeDeleteDirectory(pubAppDir, cleanParentIfEmpty: true);
                            }
                        }
                    }
                }
            }
        }

        private void CleanupExplicitPaths(AppItem app)
        {
            if (app.CleanupPaths == null) return;

            foreach (var cp in app.CleanupPaths)
            {
                if (string.IsNullOrWhiteSpace(cp)) continue;
                var exp = Environment.ExpandEnvironmentVariables(cp);
                if (Allens.Helpers.PathSafetyHelper.IsProtectedSystemPath(exp)) continue;

                if (File.Exists(exp))
                {
                    Allens.Helpers.PathSafetyHelper.SafeDeleteFile(exp);
                }
                else if (Directory.Exists(exp))
                {
                    Allens.Helpers.PathSafetyHelper.SafeDeleteDirectory(exp, cleanParentIfEmpty: true);
                }
            }
        }

        private void CleanupRegistrySoftwareKeys(AppItem app)
        {
            var roots = new[] { Registry.CurrentUser, Registry.LocalMachine };
            var basePaths = new[] { @"Software", @"Software\WOW6432Node" };
            var matchNames = GetAppMatchNames(app);

            foreach (var root in roots)
            {
                foreach (var bp in basePaths)
                {
                    try
                    {
                        using var baseKey = root.OpenSubKey(bp, writable: true);
                        if (baseKey == null) continue;

                        foreach (var name in matchNames)
                        {
                            DeleteSubKeyTreeSafe(baseKey, name);

                            // Check Publisher\Name
                            if (!string.IsNullOrWhiteSpace(app.Publisher) && !IsGenericVendorName(app.Publisher))
                            {
                                using var pubKey = baseKey.OpenSubKey(app.Publisher, writable: true);
                                if (pubKey != null)
                                {
                                    DeleteSubKeyTreeSafe(pubKey, name);
                                    if (pubKey.SubKeyCount == 0 && pubKey.ValueCount == 0)
                                    {
                                        DeleteSubKeyTreeSafe(baseKey, app.Publisher);
                                    }
                                }
                            }
                        }
                    }
                    catch { }
                }
            }
        }

        private void CleanupRegistryAutostart(AppItem app)
        {
            var roots = new[] { Registry.CurrentUser, Registry.LocalMachine };
            var runPaths = new[]
            {
                @"Software\Microsoft\Windows\CurrentVersion\Run",
                @"Software\Microsoft\Windows\CurrentVersion\RunOnce"
            };

            var matchNames = GetAppMatchNames(app);

            foreach (var root in roots)
            {
                foreach (var rp in runPaths)
                {
                    try
                    {
                        using var runKey = root.OpenSubKey(rp, writable: true);
                        if (runKey == null) continue;

                        foreach (var valName in runKey.GetValueNames())
                        {
                            bool matches = matchNames.Any(m => 
                                valName.Equals(m, StringComparison.OrdinalIgnoreCase) ||
                                valName.StartsWith($"{m} ", StringComparison.OrdinalIgnoreCase) ||
                                valName.StartsWith($"{m}-", StringComparison.OrdinalIgnoreCase) ||
                                valName.StartsWith($"{m}_", StringComparison.OrdinalIgnoreCase));

                            if (!matches)
                            {
                                var valStr = runKey.GetValue(valName)?.ToString();
                                if (!string.IsNullOrWhiteSpace(valStr))
                                {
                                    if (!string.IsNullOrWhiteSpace(app.InstalledPath) && !Allens.Helpers.PathSafetyHelper.IsProtectedSystemPath(app.InstalledPath) && valStr.IndexOf(app.InstalledPath, StringComparison.OrdinalIgnoreCase) >= 0)
                                    {
                                        matches = true;
                                    }
                                    else if (!string.IsNullOrWhiteSpace(app.DetectionValue) && !app.DetectionValue.StartsWith("HKEY_", StringComparison.OrdinalIgnoreCase))
                                    {
                                        var detPath = Environment.ExpandEnvironmentVariables(app.DetectionValue);
                                        if (valStr.IndexOf(detPath, StringComparison.OrdinalIgnoreCase) >= 0)
                                        {
                                            matches = true;
                                        }
                                    }
                                }
                            }

                            if (matches)
                            {
                                try
                                {
                                    runKey.DeleteValue(valName, throwOnMissingValue: false);
                                }
                                catch { }
                            }
                        }
                    }
                    catch { }
                }
            }
        }

        private void CleanupRegistryUninstallKeys(AppItem app)
        {
            var roots = new[] { Registry.CurrentUser, Registry.LocalMachine };
            var uninstallPaths = new[]
            {
                @"Software\Microsoft\Windows\CurrentVersion\Uninstall",
                @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
            };

            var matchNames = GetAppMatchNames(app);

            foreach (var root in roots)
            {
                foreach (var up in uninstallPaths)
                {
                    try
                    {
                        using var uKey = root.OpenSubKey(up, writable: true);
                        if (uKey == null) continue;

                        foreach (var subName in uKey.GetSubKeyNames())
                        {
                            bool shouldDelete = false;

                            if (matchNames.Any(m => subName.Equals(m, StringComparison.OrdinalIgnoreCase) ||
                                                    subName.Equals($"{m}_is1", StringComparison.OrdinalIgnoreCase)))
                            {
                                shouldDelete = true;
                            }
                            else
                            {
                                using var sub = uKey.OpenSubKey(subName);
                                if (sub != null)
                                {
                                    var disp = sub.GetValue("DisplayName")?.ToString();
                                    if (!string.IsNullOrWhiteSpace(disp) &&
                                        (disp.Equals(app.Name, StringComparison.OrdinalIgnoreCase) ||
                                         (!string.IsNullOrWhiteSpace(app.Id) && disp.Equals(app.Id, StringComparison.OrdinalIgnoreCase))))
                                    {
                                        shouldDelete = true;
                                    }
                                }
                            }

                            if (shouldDelete)
                            {
                                DeleteSubKeyTreeSafe(uKey, subName);
                            }
                        }
                    }
                    catch { }
                }
            }
        }

        private List<string> GetAppMatchNames(AppItem app)
        {
            var list = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(app.Id)) list.Add(app.Id);
            if (!string.IsNullOrWhiteSpace(app.Name))
            {
                list.Add(app.Name);
                list.Add(app.Name.Replace(" ", ""));
                list.Add(app.Name.Replace(" ", "-"));
                list.Add(app.Name.Replace(" ", "_"));
            }

            return list.Where(s => s.Length > 1).ToList();
        }

        private bool MatchesAppName(string name, AppItem app)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            var candidates = GetAppMatchNames(app);
            return candidates.Any(c => name.Equals(c, StringComparison.OrdinalIgnoreCase) ||
                                       name.IndexOf(c, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private bool IsGenericVendorName(string vendor)
        {
            if (string.IsNullOrWhiteSpace(vendor)) return true;
            var generic = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "Microsoft", "Windows", "Google", "Adobe", "Apple", "Oracle",
                "Intel", "AMD", "NVIDIA", "Mozilla", "Valve", "Common Files"
            };
            return generic.Contains(vendor.Trim());
        }

        private void DeleteSubKeyTreeSafe(RegistryKey parentKey, string subKeyName)
        {
            try
            {
                if (Allens.Helpers.PathSafetyHelper.IsProtectedRegistryKey(subKeyName)) return;
                parentKey.DeleteSubKeyTree(subKeyName, throwOnMissingSubKey: false);
            }
            catch { }
        }

        private bool IsAppAlreadyGone(AppItem app)
        {
            if (FindUninstallCommandFromRegistry(app) != null)
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(app.DetectionValue) && !app.DetectionValue.StartsWith("HKEY_", StringComparison.OrdinalIgnoreCase))
            {
                var expanded = Environment.ExpandEnvironmentVariables(app.DetectionValue);
                if (File.Exists(expanded) || Directory.Exists(expanded))
                    return false;
            }

            if (!string.IsNullOrWhiteSpace(app.InstalledPath) && Directory.Exists(app.InstalledPath) && !Allens.Helpers.PathSafetyHelper.IsProtectedSystemPath(app.InstalledPath))
            {
                try
                {
                    if (Directory.EnumerateFileSystemEntries(app.InstalledPath).Any())
                        return false;
                }
                catch { }
            }

            return true;
        }

        private void ParseCommand(string command, out string executable, out string arguments)
        {
            command = command.Trim();
            executable = command;
            arguments = string.Empty;

            if (command.StartsWith("\""))
            {
                var endQuote = command.IndexOf("\"", 1);
                if (endQuote > 0)
                {
                    executable = command.Substring(1, endQuote - 1);
                    if (command.Length > endQuote + 1)
                    {
                        arguments = command.Substring(endQuote + 1).Trim();
                    }
                }
            }
            else
            {
                // Try to find matching file if spaces exist without quotes
                int spaceIndex = command.IndexOf(' ');
                while (spaceIndex > 0)
                {
                    var potentialExe = command.Substring(0, spaceIndex);
                    if (File.Exists(potentialExe) || potentialExe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    {
                        executable = potentialExe;
                        arguments = command.Substring(spaceIndex + 1).Trim();
                        return;
                    }
                    spaceIndex = command.IndexOf(' ', spaceIndex + 1);
                }

                spaceIndex = command.IndexOf(' ');
                if (spaceIndex > 0)
                {
                    executable = command.Substring(0, spaceIndex);
                    arguments = command.Substring(spaceIndex + 1).Trim();
                }
            }
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
    }
}
