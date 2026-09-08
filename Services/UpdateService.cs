using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Allens.Models;

namespace Allens.Services
{
    public class UpdateService : IUpdateService
    {
        private const string CurrentAppVersion = "1.0.2";
        private const string GitHubRepo = "yyyert1337/Allens";
        private const string ReleasesApiUrl = "https://api.github.com/repos/" + GitHubRepo + "/releases/latest";
        private const string VersionRawUrl = "https://raw.githubusercontent.com/" + GitHubRepo + "/main/version.json";
        private readonly HttpClient _httpClient;
        private readonly ILoggerService _logger;

        public UpdateService(HttpClient httpClient, ILoggerService logger)
        {
            _httpClient = httpClient;
            _logger = logger;
        }

        public async Task<UpdateInfo?> CheckForUpdateAsync(CancellationToken cancellationToken = default)
        {
            // 1. Try GitHub Releases API
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, ReleasesApiUrl);
                request.Headers.Add("User-Agent", "Allens-Updater/1.0");
                request.Headers.Add("Accept", "application/vnd.github.v3+json");

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(TimeSpan.FromSeconds(6));

                using var response = await _httpClient.SendAsync(request, cts.Token);
                if (response.IsSuccessStatusCode)
                {
                    var json = await response.Content.ReadAsStringAsync(cts.Token);
                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;

                    string tagName = root.TryGetProperty("tag_name", out var tagProp) ? tagProp.GetString() ?? "" : "";
                    string body = root.TryGetProperty("body", out var bodyProp) ? bodyProp.GetString() ?? "" : "";

                    string downloadUrl = "";
                    long fileSize = 0;

                    if (root.TryGetProperty("assets", out var assetsProp) && assetsProp.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var asset in assetsProp.EnumerateArray())
                        {
                            var name = asset.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                            if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                            {
                                downloadUrl = asset.TryGetProperty("browser_download_url", out var dl) ? dl.GetString() ?? "" : "";
                                fileSize = asset.TryGetProperty("size", out var s) ? s.GetInt64() : 0;
                                break;
                            }
                        }
                    }

                    var cleanTag = CleanVersion(tagName);
                    if (IsNewerVersion(cleanTag, CurrentAppVersion))
                    {
                        return new UpdateInfo
                        {
                            HasUpdate = true,
                            CurrentVersion = CurrentAppVersion,
                            LatestVersion = cleanTag,
                            DownloadUrl = downloadUrl,
                            ReleaseNotes = body,
                            FileSize = fileSize
                        };
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogInfo($"Update check via GitHub API skipped: {ex.Message}");
            }

            // 2. Fallback check via version.json
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, VersionRawUrl);
                request.Headers.Add("User-Agent", "Allens-Updater/1.0");

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(TimeSpan.FromSeconds(5));

                using var response = await _httpClient.SendAsync(request, cts.Token);
                if (response.IsSuccessStatusCode)
                {
                    var json = await response.Content.ReadAsStringAsync(cts.Token);
                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;

                    string version = root.TryGetProperty("version", out var v) ? v.GetString() ?? "" : "";
                    string url = root.TryGetProperty("downloadUrl", out var dl) ? dl.GetString() ?? "" : "";
                    string notes = root.TryGetProperty("notes", out var n) ? n.GetString() ?? "" : "";

                    var cleanVer = CleanVersion(version);
                    if (IsNewerVersion(cleanVer, CurrentAppVersion))
                    {
                        return new UpdateInfo
                        {
                            HasUpdate = true,
                            CurrentVersion = CurrentAppVersion,
                            LatestVersion = cleanVer,
                            DownloadUrl = url,
                            ReleaseNotes = notes
                        };
                    }
                }
            }
            catch { }

            return new UpdateInfo
            {
                HasUpdate = false,
                CurrentVersion = CurrentAppVersion,
                LatestVersion = CurrentAppVersion
            };
        }

        public async Task<string> DownloadUpdateAsync(string downloadUrl, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(downloadUrl))
            {
                throw new ArgumentException("Download URL is empty.");
            }

            var tempDir = Path.Combine(Path.GetTempPath(), "Allens", "Update");
            Directory.CreateDirectory(tempDir);
            var destPath = Path.Combine(tempDir, "Allens_Update.exe");

            if (File.Exists(destPath))
            {
                try { File.Delete(destPath); } catch { }
            }

            using var response = await _httpClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength ?? -1L;
            await using var srcStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var dstStream = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None);

            var buffer = new byte[81920];
            long totalRead = 0;
            int bytesRead;

            while ((bytesRead = await srcStream.ReadAsync(buffer, 0, buffer.Length, cancellationToken)) > 0)
            {
                await dstStream.WriteAsync(buffer, 0, bytesRead, cancellationToken);
                totalRead += bytesRead;

                if (totalBytes > 0 && progress != null)
                {
                    var pct = (double)totalRead / totalBytes * 100.0;
                    progress.Report(pct);
                }
            }

            return destPath;
        }

        public void ApplyUpdateAndRestart(string downloadedInstallerPath)
        {
            var currentExe = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrEmpty(currentExe) || !File.Exists(downloadedInstallerPath))
            {
                return;
            }

            var currentPid = Process.GetCurrentProcess().Id;
            var scriptDir = Path.Combine(Path.GetTempPath(), "Allens");
            Directory.CreateDirectory(scriptDir);
            var scriptPath = Path.Combine(scriptDir, "allens_updater.cmd");

            var scriptContent = "@echo off\r\n" +
                                "timeout /t 1 /nobreak >nul\r\n" +
                                ":waitloop\r\n" +
                                $"tasklist /fi \"pid eq {currentPid}\" | find \":\" >nul\r\n" +
                                "if errorlevel 1 goto waitloop\r\n" +
                                $"copy /y \"{downloadedInstallerPath}\" \"{currentExe}\" >nul\r\n" +
                                $"del \"{downloadedInstallerPath}\" >nul\r\n" +
                                $"start \"\" \"{currentExe}\"\r\n" +
                                "del \"%~f0\" >nul\r\n" +
                                "exit\r\n";

            File.WriteAllText(scriptPath, scriptContent);

            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c \"{scriptPath}\"",
                CreateNoWindow = true,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden
            };

            Process.Start(psi);
            _logger.LogInfo("Allens auto-updater launched detached helper script. Shutting down.");

            System.Windows.Application.Current?.Dispatcher?.Invoke(() =>
            {
                System.Windows.Application.Current.Shutdown(0);
            });

            Environment.Exit(0);
        }

        private static string CleanVersion(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "0.0.0";
            return raw.TrimStart('v', 'V').Trim();
        }

        private static bool IsNewerVersion(string latestStr, string currentStr)
        {
            try
            {
                if (Version.TryParse(latestStr, out var latest) && Version.TryParse(currentStr, out var current))
                {
                    return latest > current;
                }

                var latestParts = latestStr.Split('.');
                var currentParts = currentStr.Split('.');
                int count = Math.Max(latestParts.Length, currentParts.Length);

                for (int i = 0; i < count; i++)
                {
                    int l = i < latestParts.Length && int.TryParse(latestParts[i], out var lv) ? lv : 0;
                    int c = i < currentParts.Length && int.TryParse(currentParts[i], out var cv) ? cv : 0;
                    if (l > c) return true;
                    if (l < c) return false;
                }
            }
            catch { }
            return false;
        }
    }
}
