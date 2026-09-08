using System;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Allens.ViewModels;
using Allens.Services;

namespace Allens
{
    public partial class App : Application
    {
        private readonly IHost _host;

        public App()
        {
            DispatcherUnhandledException += (s, e) =>
            {
                var crashLog = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Allens", "crash.txt");
                try
                {
                    System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(crashLog)!);
                    System.IO.File.AppendAllText(crashLog, $"[{DateTime.Now}] [Dispatcher] {e.Exception}\n");
                }
                catch { }
            };

            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                var crashLog = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Allens", "crash.txt");
                try
                {
                    System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(crashLog)!);
                    System.IO.File.AppendAllText(crashLog, $"[{DateTime.Now}] [AppDomain] {e.ExceptionObject}\n");
                }
                catch { }
            };

            _host = Host.CreateDefaultBuilder()
                .ConfigureServices((context, services) =>
                {
                    ConfigureServices(services);
                })
                .Build();
        }

        private void ConfigureServices(IServiceCollection services)
        {

            services.AddSingleton<System.Net.Http.HttpClient>(sp =>
            {
                var handler = new System.Net.Http.HttpClientHandler
                {
                    AllowAutoRedirect = true,
                    AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate | System.Net.DecompressionMethods.Brotli
                };
                var client = new System.Net.Http.HttpClient(handler);
                client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36");
                client.DefaultRequestHeaders.Accept.ParseAdd("*/*");
                client.Timeout = System.Threading.Timeout.InfiniteTimeSpan;
                return client;
            });

            services.AddSingleton<ILoggerService, LoggerService>();
            services.AddSingleton<ISettingsService, SettingsService>();

            services.AddSingleton<ICatalogService, CatalogService>();
            services.AddSingleton<IDownloadService, DownloadService>();
            services.AddSingleton<IHashVerificationService, HashVerificationService>();
            services.AddSingleton<IInstallationService, InstallationService>();
            services.AddSingleton<IApplicationDetectionService, ApplicationDetectionService>();
            services.AddSingleton<IInstallationQueueService, InstallationQueueService>();
            services.AddSingleton<IUninstallationService, UninstallationService>();
            services.AddSingleton<IApplicationCleanupService, ApplicationCleanupService>();
            services.AddSingleton<IUpdateService, UpdateService>();

            services.AddSingleton<MainWindowViewModel>();

            services.AddSingleton<MainWindow>();
        }

        private static System.Threading.Mutex? _appMutex;
        private const string AppMutexName = "Global\\Allens_SingleInstance_App_Mutex_b19d";

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);

        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr FindWindow(string? lpClassName, string lpWindowName);

        private const int SW_RESTORE = 9;

        private static void BringExistingWindowToFront()
        {
            try
            {
                var hWnd = FindWindow(null, "Allens");
                if (hWnd != IntPtr.Zero)
                {
                    ShowWindowAsync(hWnd, SW_RESTORE);
                    SetForegroundWindow(hWnd);
                }
            }
            catch { }
        }

        private static bool IsAdministrator()
        {
            try
            {
                using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
                var principal = new System.Security.Principal.WindowsPrincipal(identity);
                return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            if (!IsAdministrator())
            {
                try
                {
                    var exePath = Environment.ProcessPath ?? System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
                    if (!string.IsNullOrEmpty(exePath))
                    {
                        var startInfo = new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = exePath,
                            UseShellExecute = true,
                            Verb = "runas"
                        };
                        System.Diagnostics.Process.Start(startInfo);
                        Shutdown(0);
                        return;
                    }
                }
                catch
                {
                    MessageBox.Show(
                        "Для корректной установки и удаления программ приложению требуются права администратора.\nПожалуйста, подтвердите запрос UAC при запуске.",
                        "Требуются права администратора",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    Shutdown(0);
                    return;
                }
            }

            bool isNewInstance;
            try
            {
                _appMutex = new System.Threading.Mutex(true, AppMutexName, out isNewInstance);
            }
            catch
            {
                isNewInstance = true;
            }

            if (!isNewInstance)
            {
                BringExistingWindowToFront();
                Shutdown();
                return;
            }

            ShutdownMode = ShutdownMode.OnMainWindowClose;
            base.OnStartup(e);

            try
            {
                _host.Start();

                var mainWindow = _host.Services.GetRequiredService<MainWindow>();
                MainWindow = mainWindow;
                mainWindow.Show();
            }
            catch (Exception ex)
            {
                var crashLog = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Allens", "crash.txt");
                try
                {
                    System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(crashLog)!);
                    System.IO.File.AppendAllText(crashLog, $"[{DateTime.Now}] [Startup] {ex}\n");
                }
                catch { }
                MessageBox.Show($"Ошибка при запуске приложения:\n{ex.Message}", "Allens Error", MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown(-1);
            }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            try
            {
                _host.StopAsync().GetAwaiter().GetResult();
                _host.Dispose();
            }
            catch { }

            if (_appMutex != null)
            {
                try { _appMutex.ReleaseMutex(); } catch { }
                _appMutex.Dispose();
                _appMutex = null;
            }

            base.OnExit(e);
        }
    }
}
