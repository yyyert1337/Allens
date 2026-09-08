using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Allens.Models;
using Allens.Helpers;

namespace Allens.Services
{
    public class InstallationQueueService : IInstallationQueueService
    {
        private readonly IDownloadService _downloadService;
        private readonly IHashVerificationService _hashService;
        private readonly IInstallationService _installationService;
        private readonly IApplicationDetectionService _detectionService;
        private readonly ILoggerService _logger;

        public ObservableCollection<QueueItem> Queue { get; } = new();
        public bool IsProcessing { get; private set; }
        private readonly SemaphoreSlim _queueSemaphore = new(1, 1);

        public InstallationQueueService(
            IDownloadService downloadService,
            IHashVerificationService hashService,
            IInstallationService installationService,
            IApplicationDetectionService detectionService,
            ILoggerService logger)
        {
            _downloadService = downloadService;
            _hashService = hashService;
            _installationService = installationService;
            _detectionService = detectionService;
            _logger = logger;
        }

        public void AddToQueue(AppItem app, QueueOperationType operationType = QueueOperationType.Install)
        {
            if (System.Windows.Application.Current?.Dispatcher != null &&
                !System.Windows.Application.Current.Dispatcher.CheckAccess())
            {
                System.Windows.Application.Current.Dispatcher.Invoke(() => AddToQueue(app, operationType));
                return;
            }

            var existing = Queue.FirstOrDefault(q => q.App.Id == app.Id);
            if (existing == null)
            {
                Queue.Add(new QueueItem(app, operationType));
            }
            else
            {
                existing.OperationType = operationType;
                existing.Status = QueueItemStatus.Pending;
                existing.Opacity = 1.0;
                existing.StatusMessage = "Ожидание...";
                existing.ProgressPercentage = 0;
                existing.ProgressText = string.Empty;
            }

            // Always guarantee that queue execution starts immediately
            _ = ProcessQueueAsync();
        }

        public void RemoveFromQueue(AppItem app)
        {
            if (System.Windows.Application.Current?.Dispatcher != null &&
                !System.Windows.Application.Current.Dispatcher.CheckAccess())
            {
                System.Windows.Application.Current.Dispatcher.Invoke(() => RemoveFromQueue(app));
                return;
            }

            var item = Queue.FirstOrDefault(q => q.App.Id == app.Id);
            if (item != null)
            {
                item.Cancel();
                Queue.Remove(item);

                // Clean up any remaining .part file when removed from queue
                try
                {
                    var fileName = DetermineInstallerFileName(app);
                    _downloadService.DeletePartFile(fileName);
                }
                catch { }
            }
        }

        public void ClearQueue()
        {
            if (System.Windows.Application.Current?.Dispatcher != null &&
                !System.Windows.Application.Current.Dispatcher.CheckAccess())
            {
                System.Windows.Application.Current.Dispatcher.Invoke(ClearQueue);
                return;
            }

            if (!IsProcessing)
            {
                Queue.Clear();
            }
        }

        public async Task ScheduleAutoDismissAsync(QueueItem item)
        {
            try
            {
                // Wait 10 seconds after completion as requested
                await Task.Delay(10000);
                
                if (!Queue.Contains(item)) return;

                // Smooth fade-out animation over 400ms (16 steps x 25ms)
                for (int i = 1; i <= 16; i++)
                {
                    await Task.Delay(25);
                    System.Windows.Application.Current?.Dispatcher?.Invoke(() =>
                    {
                        item.Opacity = Math.Max(0.0, 1.0 - (i / 16.0));
                    });
                }

                // Cleanly remove from queue
                System.Windows.Application.Current?.Dispatcher?.Invoke(() =>
                {
                    Queue.Remove(item);
                });
            }
            catch
            {
                // Ignore background dismiss errors
            }
        }

        public async Task ProcessQueueAsync(CancellationToken cancellationToken = default)
        {
            if (!await _queueSemaphore.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            {
                // An execution loop is already running and will process any new pending items
                return;
            }

            try
            {
                IsProcessing = true;
                _downloadService.CleanupOldPartFiles();

                while (!cancellationToken.IsCancellationRequested)
                {
                    QueueItem? item = null;
                    if (System.Windows.Application.Current?.Dispatcher != null &&
                        !System.Windows.Application.Current.Dispatcher.CheckAccess())
                    {
                        item = await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                            Queue.FirstOrDefault(q => q.Status == QueueItemStatus.Pending));
                    }
                    else
                    {
                        item = Queue.FirstOrDefault(q => q.Status == QueueItemStatus.Pending);
                    }

                    if (item == null) break;

                    await ProcessItemAsync(item, cancellationToken);
                }
            }
            finally
            {
                IsProcessing = false;
                _queueSemaphore.Release();
            }
        }

        private async Task ProcessItemAsync(QueueItem item, CancellationToken cancellationToken)
        {
            using var itemCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            item.Cts = itemCts;
            var token = itemCts.Token;
            string? downloadedFilePath = null;
            try
            {
                var actionVerb = item.OperationType switch
                {
                    QueueOperationType.Reinstall => "reinstallation",
                    QueueOperationType.Update => "update",
                    _ => "installation"
                };
                var actionTitle = item.OperationType switch
                {
                    QueueOperationType.Reinstall => "Переустановка",
                    QueueOperationType.Update => "Обновление",
                    _ => "Установка"
                };

                _logger.LogInfo($"Starting {actionVerb} for {item.App.Name}");
                
                // 0. Pre-flight Disk Space Check
                long requiredSpace = DiskSpaceHelper.EstimateRequiredBytes(item.App);
                var tempDir = Path.GetTempPath();
                if (!DiskSpaceHelper.CheckFreeSpace(tempDir, requiredSpace, out long availableSpace))
                {
                    item.Status = QueueItemStatus.Error;
                    item.StatusMessage = $"Недостаточно места на диске (свободно {DiskSpaceHelper.FormatBytes(availableSpace)}, требуется ~{DiskSpaceHelper.FormatBytes(requiredSpace)})";
                    _logger.LogError($"Insufficient disk space for {item.App.Name}. Available: {availableSpace}, Required: {requiredSpace}");
                    return;
                }

                // 1. Download (or use existing downloaded installer if available)
                var fileName = DetermineInstallerFileName(item.App);
                var tempDownloadDir = Path.Combine(Path.GetTempPath(), "Allens", "Downloads");
                var potentialCachedFile = Path.Combine(tempDownloadDir, fileName);

                bool useCached = false;
                if (File.Exists(potentialCachedFile) && new FileInfo(potentialCachedFile).Length > 1024)
                {
                    if (string.IsNullOrWhiteSpace(item.App.Sha256))
                    {
                        useCached = true;
                    }
                    else
                    {
                        useCached = await _hashService.VerifyFileHashAsync(potentialCachedFile, item.App.Sha256, token);
                    }
                }

                if (useCached)
                {
                    downloadedFilePath = potentialCachedFile;
                    item.ProgressPercentage = 100;
                    item.ProgressText = string.Empty;
                    _logger.LogInfo($"Using existing downloaded file for {item.App.Name}: {downloadedFilePath}");
                }
                else
                {
                    item.Status = QueueItemStatus.Downloading;
                    item.StatusMessage = item.OperationType switch
                    {
                        QueueOperationType.Reinstall => "Скачивание для переустановки...",
                        QueueOperationType.Update => "Скачивание обновления...",
                        _ => "Скачивание..."
                    };
                    
                    var progress = new Progress<DownloadProgressInfo>(p =>
                    {
                        item.ProgressPercentage = p.ProgressPercentage;
                        item.ProgressText = $"{p.BytesReceived / 1024 / 1024} MB / {p.TotalBytesToReceive / 1024 / 1024} MB";
                    });

                    downloadedFilePath = await _downloadService.DownloadFileAsync(item.App.DownloadUrl, fileName, progress, token, item.App.WingetId);
                    _logger.LogInfo($"Downloaded {item.App.Name} to {downloadedFilePath}");

                    // 2. Verify Hash
                    item.Status = QueueItemStatus.Verifying;
                    item.StatusMessage = "Проверка...";
                    item.ProgressText = "";
                    item.ProgressPercentage = 0;

                    var isHashValid = await _hashService.VerifyFileHashAsync(downloadedFilePath, item.App.Sha256, token);
                    if (!isHashValid)
                    {
                        item.Status = QueueItemStatus.Error;
                        item.StatusMessage = "Ошибка проверки файла (Hash mismatch).";
                        _logger.LogError($"Hash mismatch for {item.App.Name}. File: {downloadedFilePath}");
                        return;
                    }
                }

                // 3. Install / Reinstall / Update
                item.Status = item.OperationType == QueueOperationType.Reinstall ? QueueItemStatus.Reinstalling : QueueItemStatus.Installing;
                item.StatusMessage = item.OperationType switch
                {
                    QueueOperationType.Reinstall => "Переустановка...",
                    QueueOperationType.Update => "Обновление...",
                    _ => "Установка..."
                };
                _logger.LogInfo($"{actionTitle} {item.App.Name}...");
                
                var installResult = await _installationService.InstallAsync(item.App, downloadedFilePath, token);
                
                if (installResult.IsSuccess)
                {
                    item.Status = QueueItemStatus.Completed;
                    item.StatusMessage = item.OperationType switch
                    {
                        QueueOperationType.Reinstall => "Переустановлено ✓",
                        QueueOperationType.Update => "Обновлено ✓",
                        _ => "Установлено ✓"
                    };
                    item.ProgressPercentage = 100;
                    item.ProgressText = string.Empty;
                    item.App.IsInstalled = true;
                    item.App.HasUpdate = false;
                    item.App.IsSelected = false;

                    // Automatically detect and populate InstalledPath, InstalledVersion and exact size
                    try
                    {
                        _detectionService.InspectApp(item.App);
                    }
                    catch { }

                    // Enforce invariant: successful installation keeps app marked as installed in UI
                    item.App.IsInstalled = true;
                    item.App.HasUpdate = false;

                    _logger.LogInfo($"Successfully {actionVerb}ed {item.App.Name}. Path: {item.App.InstalledPath}");

                    // Beautiful automatic dismissal 10 seconds after completion
                    _ = ScheduleAutoDismissAsync(item);
                }
                else
                {
                    item.Status = QueueItemStatus.Error;
                    item.StatusMessage = string.IsNullOrWhiteSpace(installResult.ErrorMessage) 
                        ? $"Ошибка {actionTitle.ToLower()} (Code: {installResult.ExitCode})" 
                        : installResult.ErrorMessage;
                    _logger.LogError($"Failed to {actionVerb} {item.App.Name}. Code: {installResult.ExitCode}, Msg: {installResult.ErrorMessage}");
                }
            }
            catch (OperationCanceledException)
            {
                if (Queue.Contains(item))
                {
                    item.Status = QueueItemStatus.Error;
                    item.StatusMessage = "Отменено";
                }
                _logger.LogInfo($"Operation for {item.App.Name} was cancelled.");
            }
            catch (Exception ex)
            {
                item.Status = QueueItemStatus.Error;
                item.StatusMessage = FormatErrorMessage(ex);
                _logger.LogError($"Exception during operation on {item.App.Name}", ex);
            }
            finally
            {
                item.Cts = null;

                // 4. Cleanup temp file
                if (!string.IsNullOrWhiteSpace(downloadedFilePath) && File.Exists(downloadedFilePath))
                {
                    try
                    {
                        File.Delete(downloadedFilePath);
                        _logger.LogInfo($"Cleaned up temp file: {downloadedFilePath}");
                    }
                    catch { /* Ignore cleanup errors */ }
                }
            }
        }

        public static string DetermineInstallerFileName(AppItem app)
        {
            string expectedExt = (app.InstallerType ?? "exe").ToLowerInvariant() switch
            {
                "msi" => ".msi",
                "zip" => ".zip",
                _ => ".exe"
            };

            try
            {
                var uri = new Uri(app.DownloadUrl);
                var rawName = Path.GetFileName(uri.LocalPath);

                if (string.IsNullOrWhiteSpace(rawName) ||
                    rawName.Equals("download", StringComparison.OrdinalIgnoreCase) ||
                    rawName.Equals("win64", StringComparison.OrdinalIgnoreCase) ||
                    rawName.Equals("winx64", StringComparison.OrdinalIgnoreCase) ||
                    rawName.Equals("latest", StringComparison.OrdinalIgnoreCase) ||
                    rawName.Equals("setup", StringComparison.OrdinalIgnoreCase) ||
                    rawName.Equals("install", StringComparison.OrdinalIgnoreCase))
                {
                    return $"{app.Id}{expectedExt}";
                }

                var ext = Path.GetExtension(rawName);
                if (string.IsNullOrWhiteSpace(ext))
                {
                    return $"{rawName}{expectedExt}";
                }

                if (!ext.Equals(expectedExt, StringComparison.OrdinalIgnoreCase) &&
                    !ext.Equals(".exe", StringComparison.OrdinalIgnoreCase) &&
                    !ext.Equals(".msi", StringComparison.OrdinalIgnoreCase) &&
                    !ext.Equals(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    return $"{app.Id}{expectedExt}";
                }

                return rawName;
            }
            catch
            {
                return $"{app.Id}{expectedExt}";
            }
        }

        private static string FormatErrorMessage(Exception ex)
        {
            if (ex is System.Net.Http.HttpRequestException httpEx)
            {
                return httpEx.Message;
            }
            if (ex.Message.Contains("404 (Not Found)"))
            {
                return "Файл не найден на сервере (404 Not Found)";
            }
            if (ex.Message.Contains("403 (Forbidden)"))
            {
                return "Доступ к серверу ограничен (403 Forbidden)";
            }
            return ex.Message;
        }
    }
}
