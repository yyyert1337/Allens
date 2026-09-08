using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using Allens.Models;
using Allens.Helpers;

namespace Allens.Services
{
    public class InstallationService : IInstallationService
    {
        public async Task<InstallationResult> InstallAsync(AppItem app, string installerPath, CancellationToken cancellationToken = default)
        {
            if (!File.Exists(installerPath))
            {
                return new InstallationResult { IsSuccess = false, ErrorMessage = "Файл установщика не найден." };
            }

            var currentExt = Path.GetExtension(installerPath);
            if (string.IsNullOrWhiteSpace(currentExt))
            {
                string expectedExt = (app.InstallerType ?? "exe").ToLowerInvariant() switch
                {
                    "msi" => ".msi",
                    "zip" => ".zip",
                    _ => ".exe"
                };

                var correctedPath = installerPath + expectedExt;
                try
                {
                    if (File.Exists(correctedPath)) File.Delete(correctedPath);
                    File.Move(installerPath, correctedPath);
                    installerPath = correctedPath;
                }
                catch
                {
                    try
                    {
                        File.Copy(installerPath, correctedPath, true);
                        installerPath = correctedPath;
                    }
                    catch { }
                }
            }

            try
            {
                await ProcessHelper.CloseRunningAppProcessesAsync(app);
            }
            catch { }

            var startInfo = new ProcessStartInfo
            {
                UseShellExecute = true,
                CreateNoWindow = false,
                WindowStyle = ProcessWindowStyle.Normal
            };

            if (app.RequiresAdmin)
            {
                startInfo.Verb = "runas";
            }

            if (string.Equals(app.InstallerType, "zip", StringComparison.OrdinalIgnoreCase) || installerPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                try
                {

                    if (!string.IsNullOrWhiteSpace(app.SilentArguments))
                    {
                        var tempExtractDir = Path.Combine(Path.GetTempPath(), "Allens", "Extract", app.Id + "_" + Guid.NewGuid().ToString("N"));
                        Directory.CreateDirectory(tempExtractDir);
                        ExtractArchive(installerPath, tempExtractDir);

                        var exes = Directory.GetFiles(tempExtractDir, "*.exe", SearchOption.AllDirectories);
                        if (exes.Length == 0)
                        {
                            return new InstallationResult { IsSuccess = false, ErrorMessage = "В архиве не найден исполняемый файл установщика." };
                        }

                        var setupExe = exes[0];
                        foreach (var exe in exes)
                        {
                            var fname = Path.GetFileName(exe);
                            if (fname.IndexOf("setup", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                fname.IndexOf("install", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                setupExe = exe;
                                break;
                            }
                        }

                        var procInfo = new ProcessStartInfo
                        {
                            FileName = setupExe,
                            Arguments = app.SilentArguments,
                            UseShellExecute = true,
                            CreateNoWindow = false,
                            WindowStyle = ProcessWindowStyle.Normal
                        };
                        if (app.RequiresAdmin) procInfo.Verb = "runas";

                        Process? proc = null;
                        try
                        {
                            proc = Process.Start(procInfo);
                        }
                        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 740)
                        {

                            procInfo.Verb = "runas";
                            proc = Process.Start(procInfo);
                        }

                        if (proc == null)
                        {
                            return new InstallationResult { IsSuccess = false, ErrorMessage = "Не удалось запустить процесс установки." };
                        }

                        using (proc)
                        {
                            try
                            {
                                await ProcessHelper.WaitForExitWithTimeoutAsync(proc, TimeSpan.FromMinutes(15), cancellationToken);
                            }
                            catch (TimeoutException ex)
                            {
                                return new InstallationResult { IsSuccess = false, ErrorMessage = ex.Message };
                            }
                            catch (OperationCanceledException)
                            {
                                return new InstallationResult { IsSuccess = false, ErrorMessage = "Установка была отменена." };
                            }
                            finally
                            {
                                try { Directory.Delete(tempExtractDir, true); } catch { }
                            }

                            return new InstallationResult
                            {
                                IsSuccess = proc.ExitCode == 0,
                                ExitCode = proc.ExitCode,
                                ErrorMessage = proc.ExitCode == 0 ? string.Empty : $"Установщик завершил работу с кодом {proc.ExitCode}"
                            };
                        }
                    }
                    else
                    {

                        var targetDir = Environment.ExpandEnvironmentVariables(
                            string.IsNullOrWhiteSpace(app.DetectionValue) 
                                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", app.Id)
                                : (Path.GetDirectoryName(app.DetectionValue) ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", app.Id))
                        );

                        if (!Directory.Exists(targetDir))
                        {
                            Directory.CreateDirectory(targetDir);
                        }

                        var zipFileInfo = new FileInfo(installerPath);
                        long requiredSpace = zipFileInfo.Length * 3;
                        if (!DiskSpaceHelper.CheckFreeSpace(targetDir, requiredSpace, out long available))
                        {
                            return new InstallationResult
                            {
                                IsSuccess = false,
                                ErrorMessage = $"Недостаточно места на диске для распаковки (свободно {DiskSpaceHelper.FormatBytes(available)}, требуется ~{DiskSpaceHelper.FormatBytes(requiredSpace)})."
                            };
                        }

                        ExtractArchive(installerPath, targetDir);
                        FlattenExtractedDirectory(targetDir);
                        var normPath = NormalizeExecutableInDirectory(app, targetDir);
                        if (!string.IsNullOrWhiteSpace(normPath) && File.Exists(normPath))
                        {
                            app.InstalledPath = normPath;
                        }
                        CreateAppShortcut(app, targetDir);
                        RegisterCliPathIfNeeded(app, targetDir);

                        return new InstallationResult { IsSuccess = true, ExitCode = 0 };
                    }
                }
                catch (Exception ex)
                {
                    return new InstallationResult { IsSuccess = false, ErrorMessage = $"Ошибка распаковки ZIP: {ex.Message}" };
                }
            }
            else if (string.Equals(app.InstallerType, "portable", StringComparison.OrdinalIgnoreCase) ||
                     (string.Equals(app.InstallerType, "exe", StringComparison.OrdinalIgnoreCase) && 
                      string.IsNullOrWhiteSpace(app.SilentArguments) && 
                      !string.IsNullOrWhiteSpace(app.DetectionValue) && 
                      string.Equals(app.DetectionMethod, "path", StringComparison.OrdinalIgnoreCase) &&
                      !app.DetectionValue.StartsWith("HKEY_", StringComparison.OrdinalIgnoreCase)))
            {
                try
                {
                    var targetExePath = Environment.ExpandEnvironmentVariables(app.DetectionValue);
                    var targetDir = Path.GetDirectoryName(targetExePath);
                    if (!string.IsNullOrWhiteSpace(targetDir) && !Directory.Exists(targetDir))
                    {
                        Directory.CreateDirectory(targetDir);
                    }

                    File.Copy(installerPath, targetExePath, overwrite: true);
                    CreateAppShortcut(app, targetDir!);
                    RegisterCliPathIfNeeded(app, targetDir!);

                    return new InstallationResult { IsSuccess = true, ExitCode = 0 };
                }
                catch (Exception ex)
                {
                    return new InstallationResult { IsSuccess = false, ErrorMessage = $"Ошибка установки программы: {ex.Message}" };
                }
            }
            else if (string.Equals(app.InstallerType, "msi", StringComparison.OrdinalIgnoreCase))
            {
                startInfo.FileName = "msiexec.exe";
                var msiArgs = $"/i \"{installerPath}\"";
                if (!string.IsNullOrWhiteSpace(app.SilentArguments))
                {
                    msiArgs += $" {app.SilentArguments}";
                }
                else
                {
                    msiArgs += " /qn /norestart"; 
                }
                startInfo.Arguments = msiArgs;
            }
            else
            {

                startInfo.FileName = installerPath;
                if (!string.IsNullOrWhiteSpace(app.SilentArguments))
                {
                    startInfo.Arguments = app.SilentArguments;
                }
            }

            try
            {
                Process? process = null;
                try
                {
                    process = Process.Start(startInfo);
                }
                catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 740)
                {

                    startInfo.Verb = "runas";
                    process = Process.Start(startInfo);
                }

                if (process == null)
                {
                    return new InstallationResult { IsSuccess = false, ErrorMessage = "Не удалось запустить процесс установки." };
                }

                using (process)
                {
                    await ProcessHelper.WaitForExitWithTimeoutAsync(process, TimeSpan.FromMinutes(15), cancellationToken);

                    return new InstallationResult
                    {
                        IsSuccess = process.ExitCode == 0 || process.ExitCode == 3010, 
                        ExitCode = process.ExitCode
                    };
                }
            }
            catch (TimeoutException ex)
            {
                return new InstallationResult { IsSuccess = false, ErrorMessage = ex.Message };
            }
            catch (OperationCanceledException)
            {
                return new InstallationResult { IsSuccess = false, ErrorMessage = "Установка была отменена." };
            }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {

                return new InstallationResult { IsSuccess = false, ErrorMessage = "Установка отменена пользователем (UAC отклонен)." };
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                return new InstallationResult { IsSuccess = false, ErrorMessage = $"Не удалось запустить установщик ({ex.Message})" };
            }
            catch (Exception ex)
            {
                return new InstallationResult { IsSuccess = false, ErrorMessage = ex.Message };
            }
        }

        public static string? NormalizeExecutableInDirectory(AppItem app, string targetDir)
        {
            try
            {
                if (!Directory.Exists(targetDir)) return null;

                string expectedExe = string.Empty;
                if (!string.IsNullOrWhiteSpace(app.DetectionValue) && !app.DetectionValue.StartsWith("HKEY_", StringComparison.OrdinalIgnoreCase))
                {
                    expectedExe = Environment.ExpandEnvironmentVariables(app.DetectionValue);
                }

                if (!string.IsNullOrWhiteSpace(expectedExe) && File.Exists(expectedExe))
                {
                    return expectedExe;
                }

                var allExes = Directory.GetFiles(targetDir, "*.exe", SearchOption.AllDirectories)
                    .Where(f => !Path.GetFileName(f).Contains("uninstall", StringComparison.OrdinalIgnoreCase) &&
                                !Path.GetFileName(f).Contains("unins", StringComparison.OrdinalIgnoreCase) &&
                                !Path.GetFileName(f).Contains("crash", StringComparison.OrdinalIgnoreCase))
                    .ToList();

                if (allExes.Count == 0) return null;

                var bestMatch = allExes.FirstOrDefault(f => Path.GetFileName(f).StartsWith(app.Id, StringComparison.OrdinalIgnoreCase) &&
                                                            !Path.GetFileName(f).Contains("runner", StringComparison.OrdinalIgnoreCase) &&
                                                            !Path.GetFileName(f).Contains("sandbox", StringComparison.OrdinalIgnoreCase) &&
                                                            !Path.GetFileName(f).Contains("setup", StringComparison.OrdinalIgnoreCase) &&
                                                            !Path.GetFileName(f).Contains("update", StringComparison.OrdinalIgnoreCase))
                             ?? allExes.FirstOrDefault(f => Path.GetFileName(f).Contains(app.Id, StringComparison.OrdinalIgnoreCase) &&
                                                            !Path.GetFileName(f).Contains("runner", StringComparison.OrdinalIgnoreCase) &&
                                                            !Path.GetFileName(f).Contains("sandbox", StringComparison.OrdinalIgnoreCase))
                             ?? allExes.FirstOrDefault(f => Path.GetFileName(f).Contains(app.Name.Replace(" ", ""), StringComparison.OrdinalIgnoreCase));

                if (bestMatch != null && File.Exists(bestMatch))
                {
                    return bestMatch;
                }
            }
            catch { }
            return null;
        }

        private void CreateAppShortcut(AppItem app, string targetDir)
        {
            try
            {
                string targetExe = string.Empty;
                if (!string.IsNullOrWhiteSpace(app.DetectionValue) && !app.DetectionValue.StartsWith("HKEY_", StringComparison.OrdinalIgnoreCase))
                {
                    targetExe = Environment.ExpandEnvironmentVariables(app.DetectionValue);
                }

                if (!File.Exists(targetExe) && Directory.Exists(targetDir))
                {
                    targetExe = NormalizeExecutableInDirectory(app, targetDir) ?? string.Empty;
                }

                if (File.Exists(targetExe))
                {
                    var startMenuDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs");
                    Directory.CreateDirectory(startMenuDir);
                    var shortcutPath = Path.Combine(startMenuDir, $"{app.Name}.lnk");

                    var shellType = Type.GetTypeFromProgID("WScript.Shell");
                    if (shellType != null)
                    {
                        dynamic shell = Activator.CreateInstance(shellType)!;
                        dynamic shortcut = shell.CreateShortcut(shortcutPath);
                        shortcut.TargetPath = targetExe;
                        shortcut.WorkingDirectory = Path.GetDirectoryName(targetExe);
                        shortcut.Description = app.Description ?? app.Name;
                        shortcut.Save();
                    }
                }
            }
            catch { }
        }

        private static void FlattenExtractedDirectory(string targetDir)
        {
            try
            {
                if (!Directory.Exists(targetDir)) return;

                if (Directory.GetFiles(targetDir, "*.exe", SearchOption.TopDirectoryOnly).Length > 0)
                {
                    return;
                }

                var subDirs = Directory.GetDirectories(targetDir);

                if (subDirs.Length == 1)
                {
                    var singleSubDir = subDirs[0];

                    foreach (var file in Directory.GetFiles(singleSubDir, "*", SearchOption.TopDirectoryOnly))
                    {
                        var destFile = Path.Combine(targetDir, Path.GetFileName(file));
                        if (File.Exists(destFile))
                        {
                            File.Delete(destFile);
                        }
                        File.Move(file, destFile);
                    }

                    foreach (var dir in Directory.GetDirectories(singleSubDir, "*", SearchOption.TopDirectoryOnly))
                    {
                        var destSubDir = Path.Combine(targetDir, Path.GetFileName(dir));
                        if (Directory.Exists(destSubDir))
                        {
                            try { Directory.Delete(destSubDir, true); } catch { }
                        }
                        Directory.Move(dir, destSubDir);
                    }

                    try
                    {
                        Directory.Delete(singleSubDir, recursive: false);
                    }
                    catch { }
                }
            }
            catch { }
        }

        private static void ExtractArchive(string archivePath, string destinationDirectory)
        {
            Directory.CreateDirectory(destinationDirectory);
            var ext = Path.GetExtension(archivePath).ToLowerInvariant();
            if (ext == ".zip")
            {
                try
                {
                    System.IO.Compression.ZipFile.ExtractToDirectory(archivePath, destinationDirectory, overwriteFiles: true);
                    return;
                }
                catch
                {

                }
            }

            try
            {
                var psi = new ProcessStartInfo("tar.exe", $"-xf \"{archivePath}\" -C \"{destinationDirectory}\"")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    WindowStyle = ProcessWindowStyle.Hidden
                };
                using var proc = Process.Start(psi);
                proc?.WaitForExit();
            }
            catch (Exception ex)
            {
                if (ext != ".zip")
                {
                    try
                    {
                        System.IO.Compression.ZipFile.ExtractToDirectory(archivePath, destinationDirectory, overwriteFiles: true);
                    }
                    catch
                    {
                        throw new InvalidOperationException($"Не удалось распаковать архив '{Path.GetFileName(archivePath)}': {ex.Message}", ex);
                    }
                }
                else
                {
                    throw new InvalidOperationException($"Не удалось распаковать архив '{Path.GetFileName(archivePath)}': {ex.Message}", ex);
                }
            }
        }

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern IntPtr SendMessageTimeout(
            IntPtr hWnd, uint Msg, UIntPtr wParam, string lParam,
            uint fuFlags, uint uTimeout, out UIntPtr lpdwResult);

        private const uint HWND_BROADCAST = 0xffff;
        private const uint WM_SETTINGCHANGE = 0x001A;
        private const uint SMTO_ABORTIFHUNG = 0x0002;

        public static void RegisterCliPathIfNeeded(AppItem app, string targetDir)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(targetDir) || !Directory.Exists(targetDir)) return;

                bool isCliTool = false;
                if (string.Equals(app.Category, "Разработка", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(app.Category, "Development", StringComparison.OrdinalIgnoreCase))
                {
                    isCliTool = true;
                }
                else
                {
                    var id = app.Id.ToLowerInvariant();
                    var name = app.Name.ToLowerInvariant();
                    if (id.Contains("claude") || id.Contains("opencode") || id.Contains("cli") ||
                        name.Contains("claude") || name.Contains("opencode") || name.Contains("cli"))
                    {
                        isCliTool = true;
                    }
                }

                if (isCliTool)
                {
                    AddDirectoryToUserPath(targetDir);
                }
            }
            catch { }
        }

        public static void AddDirectoryToUserPath(string dir)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return;

                using var key = Registry.CurrentUser.OpenSubKey("Environment", writable: true);
                if (key == null) return;

                var currentPath = key.GetValue("Path", "", RegistryValueOptions.DoNotExpandEnvironmentNames) as string ?? "";
                var paths = currentPath.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

                if (!paths.Any(p => string.Equals(p, dir, StringComparison.OrdinalIgnoreCase)))
                {
                    var newPath = string.IsNullOrEmpty(currentPath) ? dir : $"{currentPath.TrimEnd(';')};{dir}";
                    key.SetValue("Path", newPath, RegistryValueKind.ExpandString);

                    NotifyEnvironmentChanged();
                }
            }
            catch { }
        }

        public static void RemoveDirectoryFromUserPath(string dir)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(dir)) return;

                using var key = Registry.CurrentUser.OpenSubKey("Environment", writable: true);
                if (key == null) return;

                var currentPath = key.GetValue("Path", "", RegistryValueOptions.DoNotExpandEnvironmentNames) as string ?? "";
                var paths = currentPath.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

                if (paths.RemoveAll(p => string.Equals(p, dir, StringComparison.OrdinalIgnoreCase)) > 0)
                {
                    var newPath = string.Join(";", paths);
                    key.SetValue("Path", newPath, RegistryValueKind.ExpandString);
                    NotifyEnvironmentChanged();
                }
            }
            catch { }
        }

        public static void NotifyEnvironmentChanged()
        {
            try
            {
                SendMessageTimeout(
                    (IntPtr)HWND_BROADCAST,
                    WM_SETTINGCHANGE,
                    UIntPtr.Zero,
                    "Environment",
                    SMTO_ABORTIFHUNG,
                    1000,
                    out _);
            }
            catch { }
        }
    }
}
