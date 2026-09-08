using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Allens.Models;

namespace Allens.Services
{
    public class DownloadService : IDownloadService
    {
        private readonly HttpClient _httpClient;
        private readonly string _downloadDirectory;

        public DownloadService(HttpClient httpClient)
        {
            _httpClient = httpClient;
            _httpClient.Timeout = Timeout.InfiniteTimeSpan;

            var tempPath = Path.GetTempPath();
            _downloadDirectory = Path.Combine(tempPath, "Allens", "Downloads");

            if (!Directory.Exists(_downloadDirectory))
            {
                Directory.CreateDirectory(_downloadDirectory);
            }

            Task.Run(() => CleanupOldPartFiles(TimeSpan.FromDays(7)));
        }

        private static HttpRequestMessage CreateRequest(HttpMethod method, string url)
        {
            var request = new HttpRequestMessage(method, url);

            if (url.Contains("sourceforge.net", StringComparison.OrdinalIgnoreCase))
            {
                request.Headers.UserAgent.ParseAdd("Wget/1.21.4");
            }
            else
            {
                request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36");
            }
            request.Headers.Accept.ParseAdd("*/*");
            return request;
        }

        public async Task<long?> GetFileSizeAsync(string url, CancellationToken cancellationToken = default)
        {
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            linkedCts.CancelAfter(TimeSpan.FromSeconds(5));
            var token = linkedCts.Token;

            try
            {
                using var request = CreateRequest(HttpMethod.Head, url);
                using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);

                if (response.IsSuccessStatusCode && response.Content.Headers.ContentLength.HasValue && response.Content.Headers.ContentLength.Value > 0)
                {
                    return response.Content.Headers.ContentLength.Value;
                }

                using var getRequest = CreateRequest(HttpMethod.Get, url);
                using var getResponse = await _httpClient.SendAsync(getRequest, HttpCompletionOption.ResponseHeadersRead, token);
                if (getResponse.IsSuccessStatusCode && getResponse.Content.Headers.ContentLength.HasValue)
                {
                    return getResponse.Content.Headers.ContentLength.Value;
                }
            }
            catch
            {

            }

            return null;
        }

        public static string FormatBytes(long bytes)
        {
            if (bytes >= 1024L * 1024 * 1024)
                return $"{(double)bytes / (1024L * 1024 * 1024):F1} GB";
            if (bytes >= 1024L * 1024)
                return $"{(double)bytes / (1024L * 1024):F1} MB";
            if (bytes >= 1024)
                return $"{(double)bytes / 1024:F0} KB";
            return $"{bytes} B";
        }

        public async Task<string> DownloadFileAsync(string url, string fileName, IProgress<DownloadProgressInfo>? progress = null, CancellationToken cancellationToken = default, string? wingetId = null)
        {
            if (string.IsNullOrWhiteSpace(Path.GetExtension(fileName)))
            {
                fileName += ".exe";
            }

            var destinationPath = Path.Combine(_downloadDirectory, fileName);

            if (string.IsNullOrWhiteSpace(url) && !string.IsNullOrWhiteSpace(wingetId))
            {
                return await DownloadViaWingetAsync(wingetId, destinationPath, progress, cancellationToken);
            }

            try
            {
                return await DownloadHttpFileAsync(url, destinationPath, progress, cancellationToken);
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {

                if (IsGitHubReleaseUrl(url))
                {
                    try
                    {
                        var resolvedUrl = await TryResolveLatestGitHubReleaseUrlAsync(url, fileName, cancellationToken);
                        if (!string.IsNullOrWhiteSpace(resolvedUrl) && !string.Equals(resolvedUrl, url, StringComparison.OrdinalIgnoreCase))
                        {
                            return await DownloadHttpFileAsync(resolvedUrl, destinationPath, progress, cancellationToken);
                        }
                    }
                    catch { }
                }

                if (!string.IsNullOrWhiteSpace(wingetId))
                {
                    try
                    {
                        return await DownloadViaWingetAsync(wingetId, destinationPath, progress, cancellationToken);
                    }
                    catch { }
                }

                throw;
            }
        }

        private async Task<string> DownloadHttpFileAsync(string url, string destinationPath, IProgress<DownloadProgressInfo>? progress, CancellationToken cancellationToken)
        {
            var partPath = destinationPath + ".part";
            var metaPath = partPath + ".meta";

            long existingBytes = 0;
            string storedETag = string.Empty;

            if (File.Exists(partPath) && File.Exists(metaPath))
            {
                try
                {
                    var metaLines = File.ReadAllLines(metaPath);
                    var storedUrl = metaLines.Length > 0 ? metaLines[0].Trim() : string.Empty;
                    storedETag = metaLines.Length > 1 ? metaLines[1].Trim() : string.Empty;

                    if (string.Equals(storedUrl, url, StringComparison.OrdinalIgnoreCase))
                    {
                        existingBytes = new FileInfo(partPath).Length;
                    }
                    else
                    {

                        try { File.Delete(partPath); } catch { }
                        try { File.Delete(metaPath); } catch { }
                        existingBytes = 0;
                    }
                }
                catch
                {
                    existingBytes = 0;
                }
            }
            else if (File.Exists(partPath) && !File.Exists(metaPath))
            {

                try { File.Delete(partPath); } catch { }
                existingBytes = 0;
            }

            using var request = CreateRequest(HttpMethod.Get, url);
            if (existingBytes > 0)
            {
                request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(existingBytes, null);
                if (!string.IsNullOrWhiteSpace(storedETag))
                {
                    try
                    {
                        request.Headers.IfRange = new System.Net.Http.Headers.RangeConditionHeaderValue(storedETag);
                    }
                    catch { }
                }
            }

            HttpResponseMessage response;
            try
            {
                response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            }
            catch (Exception)
            {
                throw;
            }

            using (response)
            {

                if (response.StatusCode == System.Net.HttpStatusCode.RequestedRangeNotSatisfiable)
                {
                    try { File.Delete(partPath); } catch { }
                    try { File.Delete(metaPath); } catch { }
                    existingBytes = 0;

                    using var retryRequest = CreateRequest(HttpMethod.Get, url);
                    using var retryResponse = await _httpClient.SendAsync(retryRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                    retryResponse.EnsureSuccessStatusCode();

                    try
                    {
                        var eTag = retryResponse.Headers.ETag?.Tag ?? string.Empty;
                        File.WriteAllLines(metaPath, new[] { url, eTag });
                    }
                    catch { }

                    return await StreamDownloadToFileAsync(retryResponse, partPath, destinationPath, 0, progress, cancellationToken);
                }

                if (!response.IsSuccessStatusCode)
                {
                    var statusCode = (int)response.StatusCode;
                    string friendlyMessage = statusCode switch
                    {
                        404 => "Файл не найден на сервере (404 Not Found)",
                        403 => "Доступ к файлу ограничен (403 Forbidden)",
                        500 or 502 or 503 or 504 => $"Сервер временно недоступен ({statusCode})",
                        _ => $"Ошибка загрузки с сервера (Код {statusCode})"
                    };
                    throw new HttpRequestException(friendlyMessage, null, response.StatusCode);
                }

                bool isResuming = response.StatusCode == System.Net.HttpStatusCode.PartialContent;
                long startOffset = isResuming ? existingBytes : 0;

                try
                {
                    var eTag = response.Headers.ETag?.Tag ?? storedETag;
                    File.WriteAllLines(metaPath, new[] { url, eTag });
                }
                catch { }

                return await StreamDownloadToFileAsync(response, partPath, destinationPath, startOffset, progress, cancellationToken);
            }
        }

        private async Task<string> DownloadViaWingetAsync(string wingetId, string destinationPath, IProgress<DownloadProgressInfo>? progress, CancellationToken cancellationToken)
        {
            progress?.Report(new DownloadProgressInfo
            {
                BytesReceived = 0,
                TotalBytesToReceive = 100,
                ProgressPercentage = 0,
                BytesPerSecond = 0,
                EstimatedTimeRemaining = TimeSpan.Zero
            });

            var tempWingetDir = Path.Combine(_downloadDirectory, "winget_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempWingetDir);

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "winget",
                    Arguments = $"download --id {wingetId} -d \"{tempWingetDir}\" --accept-source-agreements --accept-package-agreements",
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                using var process = new Process { StartInfo = psi };
                process.Start();

                var monitorTask = Task.Run(async () =>
                {
                    int pct = 10;
                    while (!process.HasExited && !cancellationToken.IsCancellationRequested)
                    {
                        pct = Math.Min(pct + 5, 90);
                        progress?.Report(new DownloadProgressInfo
                        {
                            BytesReceived = pct,
                            TotalBytesToReceive = 100,
                            ProgressPercentage = pct,
                            BytesPerSecond = 0,
                            EstimatedTimeRemaining = TimeSpan.Zero
                        });
                        try { await Task.Delay(500, cancellationToken).ConfigureAwait(false); } catch { break; }
                    }
                }, cancellationToken);

                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

                if (process.ExitCode != 0)
                {
                    var err = await process.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
                    throw new InvalidOperationException($"WinGet error (Code {process.ExitCode}): {err}");
                }

                var files = Directory.GetFiles(tempWingetDir, "*", SearchOption.AllDirectories);
                if (files.Length == 0)
                {
                    throw new FileNotFoundException($"WinGet не сохранил файл для {wingetId}");
                }

                string chosenFile = files[0];
                long maxLen = -1;
                foreach (var f in files)
                {
                    var fi = new FileInfo(f);
                    if (fi.Length > maxLen)
                    {
                        maxLen = fi.Length;
                        chosenFile = f;
                    }
                }

                if (File.Exists(destinationPath))
                {
                    try { File.Delete(destinationPath); } catch { }
                }

                File.Copy(chosenFile, destinationPath, overwrite: true);

                progress?.Report(new DownloadProgressInfo
                {
                    BytesReceived = maxLen,
                    TotalBytesToReceive = maxLen,
                    ProgressPercentage = 100,
                    BytesPerSecond = 0,
                    EstimatedTimeRemaining = TimeSpan.Zero
                });

                return destinationPath;
            }
            finally
            {
                try
                {
                    if (Directory.Exists(tempWingetDir))
                    {
                        Directory.Delete(tempWingetDir, true);
                    }
                }
                catch { }
            }
        }

        private async Task<string> StreamDownloadToFileAsync(HttpResponseMessage response, string partPath, string destinationPath, long startOffset, IProgress<DownloadProgressInfo>? progress, CancellationToken cancellationToken)
        {
            var contentLength = response.Content.Headers.ContentLength ?? -1L;
            var totalBytes = contentLength > 0 ? (startOffset + contentLength) : -1L;

            var fileMode = startOffset > 0 ? FileMode.Append : FileMode.Create;
            using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using (var fileStream = new FileStream(partPath, fileMode, FileAccess.Write, FileShare.None, 16384, true))
            {
                var totalRead = startOffset;
                var buffer = new byte[16384];
                var isMoreToRead = true;

                var stopwatch = Stopwatch.StartNew();
                var lastReportTime = stopwatch.Elapsed;

                do
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var read = await contentStream.ReadAsync(buffer, 0, buffer.Length, cancellationToken);
                    if (read == 0)
                    {
                        isMoreToRead = false;
                        continue;
                    }

                    await fileStream.WriteAsync(buffer, 0, read, cancellationToken);
                    totalRead += read;

                    if (progress != null)
                    {
                        var currentTime = stopwatch.Elapsed;
                        if (currentTime - lastReportTime >= TimeSpan.FromMilliseconds(150) || !isMoreToRead)
                        {
                            var elapsedSeconds = currentTime.TotalSeconds;
                            var bytesPerSecond = elapsedSeconds > 0 ? (totalRead - startOffset) / elapsedSeconds : 0;
                            var percentage = totalBytes > 0 ? (double)totalRead / totalBytes * 100 : 0;

                            var timeRemaining = TimeSpan.Zero;
                            if (bytesPerSecond > 0 && totalBytes > 0)
                            {
                                var secondsRemaining = (totalBytes - totalRead) / bytesPerSecond;
                                if (secondsRemaining > 0) timeRemaining = TimeSpan.FromSeconds(secondsRemaining);
                            }

                            progress.Report(new DownloadProgressInfo
                            {
                                BytesReceived = totalRead,
                                TotalBytesToReceive = totalBytes,
                                ProgressPercentage = percentage,
                                BytesPerSecond = bytesPerSecond,
                                EstimatedTimeRemaining = timeRemaining
                            });

                            lastReportTime = currentTime;
                        }
                    }
                }
                while (isMoreToRead);
            }

            try { File.Delete(partPath + ".meta"); } catch { }

            if (File.Exists(destinationPath))
            {
                try { File.Delete(destinationPath); } catch { }
            }
            File.Move(partPath, destinationPath);

            return destinationPath;
        }

        public void CleanupOldPartFiles(TimeSpan? maxAge = null)
        {
            try
            {
                if (!Directory.Exists(_downloadDirectory)) return;

                var threshold = DateTime.UtcNow - (maxAge ?? TimeSpan.FromDays(7));
                var di = new DirectoryInfo(_downloadDirectory);

                foreach (var file in di.EnumerateFiles("*.part"))
                {
                    try
                    {
                        if (file.LastWriteTimeUtc < threshold)
                        {
                            file.Delete();
                            var meta = file.FullName + ".meta";
                            if (File.Exists(meta)) File.Delete(meta);
                        }
                    }
                    catch { }
                }

                foreach (var metaFile in di.EnumerateFiles("*.part.meta"))
                {
                    try
                    {
                        var correspondingPart = metaFile.FullName.Substring(0, metaFile.FullName.Length - 5);
                        if (!File.Exists(correspondingPart) || metaFile.LastWriteTimeUtc < threshold)
                        {
                            metaFile.Delete();
                        }
                    }
                    catch { }
                }
            }
            catch { }
        }

        public void DeletePartFile(string fileName)
        {
            try
            {
                var destinationPath = Path.Combine(_downloadDirectory, fileName);
                var partPath = destinationPath + ".part";
                if (File.Exists(partPath)) try { File.Delete(partPath); } catch { }
                if (File.Exists(partPath + ".meta")) try { File.Delete(partPath + ".meta"); } catch { }

                if (string.IsNullOrWhiteSpace(Path.GetExtension(fileName)))
                {
                    var exePart = destinationPath + ".exe.part";
                    if (File.Exists(exePart)) try { File.Delete(exePart); } catch { }
                    if (File.Exists(exePart + ".meta")) try { File.Delete(exePart + ".meta"); } catch { }
                }
            }
            catch { }
        }

        public void ClearDownloadCache()
        {
            try
            {
                if (!Directory.Exists(_downloadDirectory)) return;

                var di = new DirectoryInfo(_downloadDirectory);
                foreach (var file in di.EnumerateFiles("*", SearchOption.AllDirectories))
                {
                    try
                    {
                        file.Attributes = FileAttributes.Normal;
                        file.Delete();
                    }
                    catch { }
                }

                foreach (var dir in di.EnumerateDirectories())
                {
                    try
                    {
                        dir.Delete(true);
                    }
                    catch { }
                }
            }
            catch { }
        }

        public long GetDownloadCacheSizeBytes()
        {
            try
            {
                if (!Directory.Exists(_downloadDirectory)) return 0;
                var di = new DirectoryInfo(_downloadDirectory);
                long total = 0;
                foreach (var file in di.EnumerateFiles("*", SearchOption.AllDirectories))
                {
                    try { total += file.Length; } catch { }
                }
                return total;
            }
            catch
            {
                return 0;
            }
        }

        public string GetDownloadCacheSizeDisplay()
        {
            var bytes = GetDownloadCacheSizeBytes();
            if (bytes == 0) return "0 MB";
            return FormatBytes(bytes);
        }

        public static bool IsGitHubReleaseUrl(string? url)
        {
            if (string.IsNullOrWhiteSpace(url)) return false;
            return url.Contains("github.com/", StringComparison.OrdinalIgnoreCase) &&
                   url.Contains("/releases/", StringComparison.OrdinalIgnoreCase);
        }

        public async Task<string?> TryResolveLatestGitHubReleaseUrlAsync(string currentUrl, string? expectedFileName = null, CancellationToken cancellationToken = default)
        {
            try
            {
                var match = Regex.Match(currentUrl, @"github\.com/(?<owner>[^/]+)/(?<repo>[^/]+)/releases", RegexOptions.IgnoreCase);
                if (!match.Success) return null;

                var owner = match.Groups["owner"].Value;
                var repo = match.Groups["repo"].Value;
                var apiUrl = $"https://api.github.com/repos/{owner}/{repo}/releases/latest";

                using var request = new HttpRequestMessage(HttpMethod.Get, apiUrl);
                request.Headers.UserAgent.ParseAdd("Allens-App/1.0.2 (Windows NT 10.0; Win64; x64)");
                request.Headers.Accept.ParseAdd("application/vnd.github.v3+json");

                using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken);
                if (!response.IsSuccessStatusCode) return null;

                var json = await response.Content.ReadAsStringAsync(cancellationToken);
                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
                {
                    return null;
                }

                var candidateUrls = new List<(string Name, string Url, long Size)>();
                foreach (var asset in assets.EnumerateArray())
                {
                    if (asset.TryGetProperty("name", out var nameProp) &&
                        asset.TryGetProperty("browser_download_url", out var urlProp))
                    {
                        var name = nameProp.GetString() ?? string.Empty;
                        var downloadUrl = urlProp.GetString() ?? string.Empty;
                        long size = asset.TryGetProperty("size", out var sizeProp) ? sizeProp.GetInt64() : 0;
                        if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(downloadUrl))
                        {
                            candidateUrls.Add((name, downloadUrl, size));
                        }
                    }
                }

                if (candidateUrls.Count == 0) return null;

                if (!string.IsNullOrWhiteSpace(expectedFileName))
                {
                    var exact = candidateUrls.FirstOrDefault(c => string.Equals(c.Name, expectedFileName, StringComparison.OrdinalIgnoreCase));
                    if (!string.IsNullOrWhiteSpace(exact.Url)) return exact.Url;

                    var nameWithoutExt = Path.GetFileNameWithoutExtension(expectedFileName);
                    var ext = Path.GetExtension(expectedFileName);
                    var matchedExt = candidateUrls.Where(c => c.Name.EndsWith(ext, StringComparison.OrdinalIgnoreCase)).ToList();

                    var matchedName = matchedExt.FirstOrDefault(c => c.Name.StartsWith(nameWithoutExt, StringComparison.OrdinalIgnoreCase));
                    if (!string.IsNullOrWhiteSpace(matchedName.Url)) return matchedName.Url;
                }

                var windowsAssets = candidateUrls.Where(c =>
                {
                    var ln = c.Name.ToLowerInvariant();
                    if (ln.EndsWith(".sha256") || ln.EndsWith(".asc") || ln.EndsWith(".sig") || ln.EndsWith(".txt") || ln.EndsWith(".md")) return false;
                    if (ln.Contains("arm64") || ln.Contains("arm") || ln.Contains("linux") || ln.Contains("darwin") || ln.Contains("mac") || ln.Contains("freebsd")) return false;
                    return ln.EndsWith(".exe") || ln.EndsWith(".msi") || ln.EndsWith(".zip") || ln.EndsWith(".7z");
                }).ToList();

                if (windowsAssets.Count > 0)
                {
                    var x64 = windowsAssets.FirstOrDefault(c => c.Name.Contains("x64", StringComparison.OrdinalIgnoreCase) ||
                                                                c.Name.Contains("win64", StringComparison.OrdinalIgnoreCase) ||
                                                                c.Name.Contains("windows", StringComparison.OrdinalIgnoreCase) ||
                                                                c.Name.Contains("win", StringComparison.OrdinalIgnoreCase));
                    if (!string.IsNullOrWhiteSpace(x64.Url)) return x64.Url;

                    return windowsAssets[0].Url;
                }
            }
            catch
            {

            }

            return null;
        }
    }
}
