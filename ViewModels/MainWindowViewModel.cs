using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Allens.Models;
using Allens.Services;

namespace Allens.ViewModels
{
    public partial class MainWindowViewModel : ObservableObject
    {
        private readonly ICatalogService _catalogService;
        private readonly IDownloadService _downloadService;
        private readonly IApplicationDetectionService _detectionService;
        private readonly IUninstallationService _uninstallationService;
        private readonly IApplicationCleanupService _cleanupService;
        private readonly ISettingsService _settingsService;
        private readonly ILoggerService _logger;
        private readonly IUpdateService _updateService;
        private List<AppItem> _allApps = new();

        [ObservableProperty]
        private bool _isUpdateAvailable;

        [ObservableProperty]
        private string _latestAllensVersion = string.Empty;

        [ObservableProperty]
        private string _updateDownloadUrl = string.Empty;

        [ObservableProperty]
        private string _updateReleaseNotes = string.Empty;

        [ObservableProperty]
        private bool _isUpdatingApp;

        [ObservableProperty]
        private string _updateStatusText = string.Empty;

        [ObservableProperty]
        private string _downloadCacheSize = "0 MB";

        [ObservableProperty]
        private bool _isInternetAvailable = true;

        [ObservableProperty]
        private string _searchText = string.Empty;

        [ObservableProperty]
        private string _selectedCategory = "Все приложения";

        [ObservableProperty]
        private string _selectedStatusFilter = "Все"; 

        [ObservableProperty]
        private AppItem? _selectedAppDetails;

        [ObservableProperty]
        private bool _isDetailsOpen;

        [ObservableProperty]
        private int _selectedCount = 0;

        [ObservableProperty]
        private string _snackbarMessage = string.Empty;

        [ObservableProperty]
        private bool _isSnackbarVisible = false;

        [ObservableProperty]
        private bool _isDialogOpen = false;

        [ObservableProperty]
        private string _dialogTitle = string.Empty;

        [ObservableProperty]
        private string _dialogMessage = string.Empty;

        [ObservableProperty]
        private string _dialogConfirmText = "Подтвердить";

        [ObservableProperty]
        private string _dialogCancelText = "Отмена";

        [ObservableProperty]
        private bool _dialogIsDanger = false;

        [ObservableProperty]
        private bool _dialogHasCancel = true;

        private TaskCompletionSource<bool>? _dialogTcs;

        public ObservableCollection<AppItem> FilteredApps { get; } = new();

        public IInstallationQueueService QueueService { get; }

        public MainWindowViewModel(
            ICatalogService catalogService, 
            IDownloadService downloadService,
            IInstallationQueueService queueService,
            IApplicationDetectionService detectionService,
            IUninstallationService uninstallationService,
            IApplicationCleanupService cleanupService,
            ISettingsService settingsService,
            ILoggerService logger,
            IUpdateService updateService)
        {
            _catalogService = catalogService;
            _downloadService = downloadService;
            QueueService = queueService;
            _detectionService = detectionService;
            _uninstallationService = uninstallationService;
            _cleanupService = cleanupService;
            _settingsService = settingsService;
            _logger = logger;
            _updateService = updateService;

            QueueService.Queue.CollectionChanged += (s, e) =>
            {
                RefreshDownloadCacheSize();
            };

            try
            {
                NetworkChange.NetworkAvailabilityChanged += (s, e) =>
                {
                    App.Current?.Dispatcher?.Invoke(async () =>
                    {
                        await CheckInternetConnectivityAsync();
                    });
                };
            }
            catch { }

            _ = LoadInitialDataAsync();
        }

        private async Task LoadInitialDataAsync()
        {
            await _settingsService.LoadSettingsAsync();
            _logger.LogInfo("Allens app started.");
            _ = CheckInternetConnectivityAsync();
            RefreshDownloadCacheSize();
            _ = CheckForAppUpdatesAsync();
            await LoadCatalogAsync();
        }

        private async Task LoadCatalogAsync()
        {
            var apps = await _catalogService.GetCatalogAsync();
            _allApps = apps.ToList();

            _detectionService.UpdateInstallationStatus(_allApps);

            foreach (var app in _allApps)
            {
                app.PropertyChanged += (s, e) => 
                {
                    if (e.PropertyName == nameof(AppItem.IsSelected))
                    {
                        UpdateSelectedCount();
                    }
                };
            }

            ApplyFilter();

            _detectionService.MeasureFolderSizesBackground(_allApps);

            _ = Task.Run(async () =>
            {
                foreach (var app in _allApps)
                {
                    if (!string.IsNullOrWhiteSpace(app.DownloadUrl))
                    {
                        try
                        {
                            var bytes = await _downloadService.GetFileSizeAsync(app.DownloadUrl);
                            if (bytes.HasValue && bytes.Value > 0)
                            {
                                var formatted = DownloadService.FormatBytes(bytes.Value);
                                System.Windows.Application.Current?.Dispatcher?.Invoke(() =>
                                {
                                    app.SizeDisplay = formatted;
                                });
                            }
                        }
                        catch { }
                    }
                }
            });
        }

        private void UpdateSelectedCount()
        {
            SelectedCount = _allApps.Count(a => a.IsSelected);
        }

        partial void OnSearchTextChanged(string value)
        {
            ApplyFilter();
        }

        partial void OnSelectedCategoryChanged(string value)
        {
            ApplyFilter();
        }

        [RelayCommand]
        private void SelectCategory(string category)
        {
            SelectedCategory = category;
        }

        partial void OnSelectedStatusFilterChanged(string value)
        {
            ApplyFilter();
        }

        [RelayCommand]
        private void SelectStatusFilter(string filter)
        {
            SelectedStatusFilter = filter;
        }

        [RelayCommand]
        private void OpenAppDetails(AppItem? app)
        {
            if (app == null) return;
            SelectedAppDetails = app;
            IsDetailsOpen = true;

            try
            {
                _detectionService.InspectApp(app);
            }
            catch { }
        }

        [RelayCommand]
        private void CloseAppDetails()
        {
            IsDetailsOpen = false;
        }

        [RelayCommand]
        private void OpenWebsite(string? url)
        {
            if (string.IsNullOrWhiteSpace(url)) return;
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to open website {url}: {ex.Message}");
            }
        }

        [RelayCommand]
        private void OpenInstallFolder(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            try
            {
                if (File.Exists(path))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "explorer.exe",
                        Arguments = $"/select,\"{path}\"",
                        UseShellExecute = true
                    });
                    return;
                }

                if (Directory.Exists(path))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "explorer.exe",
                        Arguments = $"\"{path}\"",
                        UseShellExecute = true
                    });
                    return;
                }

                ShowNotification("Файл или папка не найдены");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to open folder {path}: {ex.Message}");
            }
        }

        private void ApplyFilter()
        {
            var filtered = _allApps.AsEnumerable();

            if (SelectedCategory != "Все приложения")
            {
                filtered = filtered.Where(a => a.Category.Equals(SelectedCategory, StringComparison.OrdinalIgnoreCase));
            }

            if (SelectedStatusFilter == "Установленные")
            {
                filtered = filtered.Where(a => a.IsInstalled);
            }
            else if (SelectedStatusFilter == "Не установленные")
            {
                filtered = filtered.Where(a => !a.IsInstalled);
            }
            else if (SelectedStatusFilter == "Есть обновления")
            {
                filtered = filtered.Where(a => a.IsInstalled && a.HasUpdate);
            }

            if (!string.IsNullOrWhiteSpace(SearchText))
            {
                var query = SearchText.ToLower();
                filtered = filtered.Where(a => 
                    a.Name.ToLower().Contains(query) || 
                    a.Description.ToLower().Contains(query) || 
                    a.Publisher.ToLower().Contains(query)
                );
            }

            FilteredApps.Clear();
            foreach (var app in filtered)
            {
                FilteredApps.Add(app);
            }
        }

        [RelayCommand]
        private void InstallApp(AppItem app)
        {
            if (app == null) return;
            if (!IsInternetAvailable && !app.IsInstalled)
            {
                ShowNotification("⚠️ Нет подключения к интернету для загрузки программы.");
                return;
            }

            var op = app.IsInstalled
                ? (app.HasUpdate ? QueueOperationType.Update : QueueOperationType.Reinstall)
                : QueueOperationType.Install;

            QueueService.AddToQueue(app, op);
            app.IsSelected = false;
            _ = QueueService.ProcessQueueAsync();
        }

        [RelayCommand]
        private async Task InstallSelectedAsync()
        {
            var selected = _allApps.Where(a => a.IsSelected).ToList();
            if (!selected.Any()) return;

            if (!IsInternetAvailable)
            {
                ShowNotification("⚠️ Нет подключения к интернету для загрузки приложений.");
                return;
            }

            try
            {
                long totalEstimatedBytes = 0;
                foreach (var app in selected)
                {
                    long dlSize = ParseSizeDisplayBytes(app.SizeDisplay);
                    totalEstimatedBytes += (long)(dlSize * 2.2); 
                }

                var systemDrive = new DriveInfo(Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\");
                if (systemDrive.IsReady)
                {
                    if (systemDrive.AvailableFreeSpace < 1024L * 1024 * 1024) 
                    {
                        var freeDisp = DownloadService.FormatBytes(systemDrive.AvailableFreeSpace);
                        await ShowDialogAsync(
                            title: "Критически мало места на диске",
                            message: $"На системном диске C:\\ свободно всего {freeDisp}. Пожалуйста, освободите место перед установкой программ.",
                            confirmText: "Понятно",
                            cancelText: "",
                            isDanger: true,
                            hasCancel: false
                        );
                        return;
                    }

                    if (systemDrive.AvailableFreeSpace < totalEstimatedBytes)
                    {
                        var freeDisp = DownloadService.FormatBytes(systemDrive.AvailableFreeSpace);
                        var reqDisp = DownloadService.FormatBytes(totalEstimatedBytes);
                        var proceed = await ShowDialogAsync(
                            title: "Внимание: мало места на диске",
                            message: $"На системном диске свободно {freeDisp}, а для выбранных программ потребуется приблизительно {reqDisp}.\n\nПродолжить установку?",
                            confirmText: "Всё равно установить",
                            cancelText: "Отмена",
                            isDanger: false,
                            hasCancel: true
                        );
                        if (!proceed) return;
                    }
                }
            }
            catch { }

            foreach (var app in selected)
            {
                var op = app.IsInstalled
                    ? (app.HasUpdate ? QueueOperationType.Update : QueueOperationType.Reinstall)
                    : QueueOperationType.Install;
                QueueService.AddToQueue(app, op);
                app.IsSelected = false; 
            }

            _ = QueueService.ProcessQueueAsync();
        }

        [RelayCommand]
        private void LaunchApp(AppItem? app)
        {
            if (app == null) return;
            try
            {
                if (!string.IsNullOrWhiteSpace(app.InstalledPath))
                {

                    if (File.Exists(app.InstalledPath))
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = app.InstalledPath,
                            WorkingDirectory = Path.GetDirectoryName(app.InstalledPath),
                            UseShellExecute = true
                        });
                        ShowNotification($"Запуск {app.Name}...");
                        return;
                    }

                    if (Directory.Exists(app.InstalledPath))
                    {
                        var dir = new DirectoryInfo(app.InstalledPath);
                        var exeFiles = dir.EnumerateFiles("*.exe", SearchOption.AllDirectories)
                            .Where(f => !f.Name.Contains("uninstall", StringComparison.OrdinalIgnoreCase) &&
                                        !f.Name.Contains("unins", StringComparison.OrdinalIgnoreCase) &&
                                        !f.Name.Contains("crash", StringComparison.OrdinalIgnoreCase) &&
                                        !f.Name.Contains("helper", StringComparison.OrdinalIgnoreCase) &&
                                        !f.Name.Contains("update", StringComparison.OrdinalIgnoreCase))
                            .ToList();

                        var bestMatch = exeFiles.FirstOrDefault(f => f.Name.Contains(app.Id, StringComparison.OrdinalIgnoreCase))
                                     ?? exeFiles.FirstOrDefault(f => f.Name.Contains(app.Name.Replace(" ", ""), StringComparison.OrdinalIgnoreCase))
                                     ?? exeFiles.FirstOrDefault();

                        if (bestMatch != null)
                        {
                            Process.Start(new ProcessStartInfo
                            {
                                FileName = bestMatch.FullName,
                                WorkingDirectory = bestMatch.DirectoryName,
                                UseShellExecute = true
                            });
                            ShowNotification($"Запуск {app.Name}...");
                            return;
                        }
                    }
                }

                var shortcut = FindStartMenuShortcut(app.Name);
                if (!string.IsNullOrWhiteSpace(shortcut) && File.Exists(shortcut))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = shortcut,
                        UseShellExecute = true
                    });
                    ShowNotification($"Запуск {app.Name}...");
                    return;
                }

                ShowNotification($"Исполняемый файл для {app.Name} не найден");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to launch {app.Name}: {ex.Message}");
                ShowNotification($"Ошибка запуска: {ex.Message}");
            }
        }

        public void RefreshDownloadCacheSize()
        {
            try
            {
                DownloadCacheSize = _downloadService.GetDownloadCacheSizeDisplay();
            }
            catch
            {
                DownloadCacheSize = "0 MB";
            }
        }

        [RelayCommand]
        private void ClearCache()
        {
            try
            {
                _downloadService.ClearDownloadCache();
                RefreshDownloadCacheSize();
                ShowNotification("Кэш загрузок успешно очищен.");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to clear download cache: {ex.Message}");
                ShowNotification("Не удалось очистить кэш загрузок.");
            }
        }

        public async Task CheckInternetConnectivityAsync()
        {
            try
            {
                if (!NetworkInterface.GetIsNetworkAvailable())
                {
                    IsInternetAvailable = false;
                    return;
                }

                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
                using var res = await client.GetAsync("http://www.msftconnecttest.com/connecttest.txt", HttpCompletionOption.ResponseHeadersRead);
                IsInternetAvailable = res.IsSuccessStatusCode;
            }
            catch
            {
                IsInternetAvailable = false;
            }
        }

        [RelayCommand]
        private async Task RetryInternetAsync()
        {
            await CheckInternetConnectivityAsync();
            if (IsInternetAvailable)
            {
                ShowNotification("Подключение к интернету восстановлено.");
            }
            else
            {
                ShowNotification("Подключение к интернету по-прежнему отсутствует.");
            }
        }

        private static long ParseSizeDisplayBytes(string sizeDisplay)
        {
            if (string.IsNullOrWhiteSpace(sizeDisplay)) return 150L * 1024 * 1024;
            try
            {
                var parts = sizeDisplay.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2 && double.TryParse(parts[0].Replace(',', '.'), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double val))
                {
                    var unit = parts[1].ToUpperInvariant();
                    if (unit.Contains("GB") || unit.Contains("ГБ")) return (long)(val * 1024 * 1024 * 1024);
                    if (unit.Contains("MB") || unit.Contains("МБ")) return (long)(val * 1024 * 1024);
                    if (unit.Contains("KB") || unit.Contains("КБ")) return (long)(val * 1024);
                }
            }
            catch { }
            return 150L * 1024 * 1024;
        }

        private static string? FindStartMenuShortcut(string appName)
        {
            try
            {
                var paths = new[]
                {
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), "Programs"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs")
                };

                foreach (var p in paths)
                {
                    if (!Directory.Exists(p)) continue;
                    var lnks = Directory.EnumerateFiles(p, "*.lnk", SearchOption.AllDirectories);
                    var match = lnks.FirstOrDefault(f => Path.GetFileNameWithoutExtension(f).Contains(appName, StringComparison.OrdinalIgnoreCase) &&
                                                        !f.Contains("uninstall", StringComparison.OrdinalIgnoreCase));
                    if (match != null) return match;
                }
            }
            catch { }
            return null;
        }

        [RelayCommand]
        private void DismissQueueItem(QueueItem item)
        {
            if (item != null)
            {
                QueueService.RemoveFromQueue(item.App);
            }
        }

        private string GenerateErrorReport(QueueItem? item)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("=== ALLENS ERROR REPORT ===");
            sb.AppendLine($"Time: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine("App Version: v1.0.2");
            sb.AppendLine($"OS: {Environment.OSVersion.VersionString} ({(Environment.Is64BitOperatingSystem ? "64-bit" : "32-bit")})");

            try
            {
                using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
                var principal = new System.Security.Principal.WindowsPrincipal(identity);
                sb.AppendLine($"Is Admin: {principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator)}");
            }
            catch { }

            try
            {
                var systemDrive = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
                var drive = new DriveInfo(systemDrive);
                var freeGb = drive.AvailableFreeSpace / (1024.0 * 1024 * 1024);
                var totalGb = drive.TotalSize / (1024.0 * 1024 * 1024);
                sb.AppendLine($"Disk Free: {freeGb:F1} GB / {totalGb:F1} GB ({systemDrive})");
            }
            catch { }

            if (item != null)
            {
                sb.AppendLine($"Target App: {item.App.Name} (ID: {item.App.Id})");
                sb.AppendLine($"App Version: {item.App.Version}");
                sb.AppendLine($"Operation: {item.OperationType}");
                sb.AppendLine($"Status: {item.Status}");
                sb.AppendLine($"Status Message: {item.StatusMessage}");
                sb.AppendLine($"Download URL: {item.App.DownloadUrl}");
                sb.AppendLine($"WinGet ID: {item.App.WingetId}");
                sb.AppendLine($"Website: {item.App.Website}");
            }

            sb.AppendLine();
            sb.AppendLine("=== RECENT LOGS ===");
            try
            {
                var logPath = Path.Combine(Path.GetTempPath(), "Allens", "logs.txt");
                if (File.Exists(logPath))
                {
                    var lines = File.ReadAllLines(logPath);
                    var tail = lines.TakeLast(20);
                    foreach (var line in tail)
                    {
                        sb.AppendLine(line);
                    }
                }
                else
                {
                    sb.AppendLine("(No logs.txt found)");
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine($"(Could not read logs: {ex.Message})");
            }

            return sb.ToString();
        }

        [RelayCommand]
        private void CopyErrorReport(QueueItem? item)
        {
            try
            {
                var report = GenerateErrorReport(item);
                System.Windows.Clipboard.SetText(report);
                ShowNotification("✓ Технический отчёт скопирован в буфер обмена");
            }
            catch (Exception ex)
            {
                ShowNotification($"Не удалось скопировать: {ex.Message}");
            }
        }

        [RelayCommand]
        private void ReportErrorToTelegram(QueueItem? item)
        {
            try
            {
                var report = GenerateErrorReport(item);
                System.Windows.Clipboard.SetText(report);

                Process.Start(new ProcessStartInfo
                {
                    FileName = "https://t.me/AHKstop",
                    UseShellExecute = true
                });

                ShowNotification("Отчёт скопирован. Отправьте его в Telegram @AHKstop");
            }
            catch (Exception ex)
            {
                ShowNotification($"Ошибка открытия Telegram: {ex.Message}");
            }
        }

        [RelayCommand]
        public async Task CheckForAppUpdatesAsync(bool isManual = false)
        {
            try
            {
                var info = await _updateService.CheckForUpdateAsync();
                if (info != null && info.HasUpdate && !string.IsNullOrWhiteSpace(info.DownloadUrl))
                {
                    LatestAllensVersion = info.LatestVersion;
                    UpdateDownloadUrl = info.DownloadUrl;
                    UpdateReleaseNotes = info.ReleaseNotes;
                    IsUpdateAvailable = true;
                    ShowNotification($"Доступно обновление Allens v{LatestAllensVersion}!");
                }
                else if (isManual)
                {
                    ShowNotification("У вас установлена последняя версия Allens ✓");
                }
            }
            catch (Exception ex)
            {
                if (isManual) ShowNotification($"Ошибка проверки обновлений: {ex.Message}");
            }
        }

        [RelayCommand]
        public async Task ManualCheckForUpdatesAsync()
        {
            await CheckForAppUpdatesAsync(isManual: true);
        }

        [RelayCommand]
        private void DismissUpdateBanner()
        {
            IsUpdateAvailable = false;
        }

        [RelayCommand]
        private async Task ApplyAppUpdateAsync()
        {
            if (string.IsNullOrWhiteSpace(UpdateDownloadUrl))
            {
                ShowNotification("Ссылка на обновление не указана.");
                return;
            }

            try
            {
                IsUpdatingApp = true;
                UpdateStatusText = "Загрузка обновления...";
                ShowNotification($"Загрузка Allens v{LatestAllensVersion}...");

                var progress = new Progress<double>(pct =>
                {
                    UpdateStatusText = $"Загрузка обновления: {pct:F0}%";
                });

                var downloadedPath = await _updateService.DownloadUpdateAsync(UpdateDownloadUrl, progress);
                UpdateStatusText = "Перезапуск и применение обновления...";
                ShowNotification("Обновление готово! Перезапуск Allens...");

                await Task.Delay(600);
                _updateService.ApplyUpdateAndRestart(downloadedPath);
            }
            catch (Exception ex)
            {
                IsUpdatingApp = false;
                ShowNotification($"Сбой обновления: {ex.Message}");
            }
        }

        [RelayCommand]
        private async Task UninstallAsync(AppItem app)
        {
            if (app == null) return;

            var confirmed = await ShowDialogAsync(
                title: $"Удалить {app.Name}?",
                message: "Программа будет полностью удалена с вашего компьютера. Некоторые персональные файлы настроек могут быть сохранены.",
                confirmText: "Удалить",
                cancelText: "Отмена",
                isDanger: true,
                hasCancel: true
            );

            if (!confirmed) return;

            var queueItem = QueueService.Queue.FirstOrDefault(q => q.App.Id == app.Id);
            if (queueItem == null)
            {
                queueItem = new QueueItem(app, QueueOperationType.Uninstall);
                QueueService.Queue.Add(queueItem);
            }
            queueItem.OperationType = QueueOperationType.Uninstall;
            queueItem.Status = QueueItemStatus.Uninstalling;
            queueItem.StatusMessage = "Удаление...";
            queueItem.ProgressPercentage = 50;
            queueItem.ProgressText = string.Empty;
            queueItem.Opacity = 1.0;

            _logger.LogInfo($"Starting uninstallation of {app.Name}");

            var result = await _uninstallationService.UninstallAsync(app);

            if (result.IsSuccess)
            {
                app.IsInstalled = false; 
                app.HasUpdate = false;

                queueItem.Status = QueueItemStatus.Completed;
                queueItem.StatusMessage = "Удалено ✓";
                queueItem.ProgressPercentage = 100;
                _logger.LogInfo($"Successfully uninstalled {app.Name}");

                _ = QueueService.ScheduleAutoDismissAsync(queueItem);

                var leftovers = _cleanupService.GetExistingLeftovers(app).ToList();
                if (leftovers.Any())
                {
                    var cleanConfirm = await ShowDialogAsync(
                        title: "Очистить остаточные файлы?",
                        message: "Обнаружены оставшиеся временные файлы, кэш и настройки приложения. Удалить их для полной очистки?",
                        confirmText: "Очистить",
                        cancelText: "Оставить",
                        isDanger: false,
                        hasCancel: true
                    );

                    if (cleanConfirm)
                    {
                        await _cleanupService.CleanupAsync(leftovers);
                        _logger.LogInfo($"Cleaned up leftovers for {app.Name}");
                        ShowNotification("Остаточные файлы удалены.");
                    }
                }
                else
                {
                    ShowNotification($"{app.Name} успешно удалён.");
                }
            }
            else
            {
                queueItem.Status = QueueItemStatus.Error;
                queueItem.StatusMessage = string.IsNullOrWhiteSpace(result.ErrorMessage) 
                    ? $"Ошибка удаления (Code: {result.ExitCode})" 
                    : result.ErrorMessage;
                _logger.LogError($"Failed to uninstall {app.Name}. Code: {result.ExitCode}, Msg: {result.ErrorMessage}");
                ShowNotification($"Не удалось удалить {app.Name}");

                var forceOption = await ShowDialogAsync(
                    title: "Ошибка при штатном удалении",
                    message: $"Не удалось удалить {app.Name} штатным деинсталлятором (код: {result.ExitCode}).\n\nЖелаете выполнить принудительное удаление (завершить процессы, удалить файлы и очистить следы в реестре)?",
                    confirmText: "Принудительно удалить",
                    cancelText: "Отмена",
                    isDanger: true,
                    hasCancel: true
                );

                if (forceOption)
                {
                    await ForceUninstallAsync(app);
                }
            }
        }

        [RelayCommand]
        private async Task ForceUninstallAsync(AppItem app)
        {
            if (app == null) return;

            var confirmed = await ShowDialogAsync(
                title: $"Принудительно удалить {app.Name}?",
                message: "Принудительное удаление немедленно завершит процессы программы, полностью удалит файлы из папки установки, ярлыки, следы в реестре и временные файлы, не запуская штатный деинсталлятор.\n\nПродолжить?",
                confirmText: "Принудительно удалить",
                cancelText: "Отмена",
                isDanger: true,
                hasCancel: true
            );

            if (!confirmed) return;

            var queueItem = QueueService.Queue.FirstOrDefault(q => q.App.Id == app.Id);
            if (queueItem == null)
            {
                queueItem = new QueueItem(app, QueueOperationType.Uninstall);
                QueueService.Queue.Add(queueItem);
            }
            queueItem.OperationType = QueueOperationType.Uninstall;
            queueItem.Status = QueueItemStatus.Uninstalling;
            queueItem.StatusMessage = "Принудительное удаление...";
            queueItem.ProgressPercentage = 50;
            queueItem.ProgressText = string.Empty;
            queueItem.Opacity = 1.0;

            _logger.LogInfo($"Starting FORCE uninstallation of {app.Name}");

            var result = await _uninstallationService.ForceUninstallAsync(app);

            if (result.IsSuccess)
            {
                app.IsInstalled = false;
                app.HasUpdate = false;

                queueItem.Status = QueueItemStatus.Completed;
                queueItem.StatusMessage = "Удалено принудительно ✓";
                queueItem.ProgressPercentage = 100;
                _logger.LogInfo($"Successfully force-uninstalled {app.Name}");

                _ = QueueService.ScheduleAutoDismissAsync(queueItem);
                ShowNotification($"{app.Name} успешно очищен и удалён.");
            }
            else
            {
                queueItem.Status = QueueItemStatus.Error;
                queueItem.StatusMessage = string.IsNullOrWhiteSpace(result.ErrorMessage) 
                    ? "Ошибка принудительного удаления" 
                    : result.ErrorMessage;
                _logger.LogError($"Failed to force-uninstall {app.Name}: {result.ErrorMessage}");
                ShowNotification($"Не удалось принудительно удалить {app.Name}");

                await ShowDialogAsync(
                    title: "Ошибка принудительного удаления",
                    message: string.IsNullOrWhiteSpace(result.ErrorMessage) ? "Произошла неизвестная ошибка при зачистке." : result.ErrorMessage,
                    confirmText: "Закрыть",
                    cancelText: string.Empty,
                    isDanger: false,
                    hasCancel: false
                );
            }
        }

        public Task<bool> ShowDialogAsync(string title, string message, string confirmText = "Подтвердить", string cancelText = "Отмена", bool isDanger = false, bool hasCancel = true)
        {

            _dialogTcs?.TrySetResult(false);

            DialogTitle = title;
            DialogMessage = message;
            DialogConfirmText = confirmText;
            DialogCancelText = cancelText;
            DialogIsDanger = isDanger;
            DialogHasCancel = hasCancel;
            IsDialogOpen = true;

            _dialogTcs = new TaskCompletionSource<bool>();
            return _dialogTcs.Task;
        }

        [RelayCommand]
        private void DialogConfirm()
        {
            IsDialogOpen = false;
            _dialogTcs?.TrySetResult(true);
        }

        [RelayCommand]
        private void DialogCancel()
        {
            IsDialogOpen = false;
            _dialogTcs?.TrySetResult(false);
        }

        private System.Threading.CancellationTokenSource? _notificationCts;

        public async void ShowNotification(string message, int durationMs = 3000)
        {
            _notificationCts?.Cancel();
            _notificationCts?.Dispose();
            var cts = new System.Threading.CancellationTokenSource();
            _notificationCts = cts;

            SnackbarMessage = message;
            IsSnackbarVisible = true;
            try
            {
                await Task.Delay(durationMs, cts.Token);
                if (!cts.IsCancellationRequested)
                {
                    IsSnackbarVisible = false;
                }
            }
            catch (OperationCanceledException)
            {

            }
        }
    }
}
