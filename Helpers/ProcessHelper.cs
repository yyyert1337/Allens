using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Allens.Models;
using Allens.Services;

namespace Allens.Helpers
{
    public static class ProcessHelper
    {
        /// <summary>
        /// Attempts to gracefully close (and if needed kill) running instances of the specified app to prevent file locks.
        /// </summary>
        public static async Task CloseRunningAppProcessesAsync(AppItem app, ILoggerService? logger = null)
        {
            var candidateExeNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (!string.IsNullOrWhiteSpace(app.DetectionValue) && !app.DetectionValue.StartsWith("HKEY_", StringComparison.OrdinalIgnoreCase))
            {
                var expanded = Environment.ExpandEnvironmentVariables(app.DetectionValue);
                var fname = Path.GetFileNameWithoutExtension(expanded);
                if (!string.IsNullOrWhiteSpace(fname)) candidateExeNames.Add(fname);
            }

            if (!string.IsNullOrWhiteSpace(app.InstalledPath))
            {
                var expanded = Environment.ExpandEnvironmentVariables(app.InstalledPath);
                if (File.Exists(expanded))
                {
                    var fname = Path.GetFileNameWithoutExtension(expanded);
                    if (!string.IsNullOrWhiteSpace(fname)) candidateExeNames.Add(fname);
                }
            }

            if (!string.IsNullOrWhiteSpace(app.Id))
            {
                candidateExeNames.Add(app.Id);
                candidateExeNames.Add(app.Id.Replace("-", ""));
                candidateExeNames.Add(app.Id.Replace("-", "_"));
            }

            var matchingProcesses = new List<Process>();
            foreach (var proc in Process.GetProcesses())
            {
                bool isMatch = false;
                try
                {
                    // Never kill system or critical processes
                    if (proc.Id != Environment.ProcessId && 
                        !string.Equals(proc.ProcessName, "explorer", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(proc.ProcessName, "devenv", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(proc.ProcessName, "Allens", StringComparison.OrdinalIgnoreCase))
                    {
                        if (candidateExeNames.Contains(proc.ProcessName))
                        {
                            isMatch = true;
                        }
                        else if (!string.IsNullOrWhiteSpace(app.InstalledPath))
                        {
                            try
                            {
                                var modulePath = proc.MainModule?.FileName;
                                if (!string.IsNullOrWhiteSpace(modulePath) && modulePath.StartsWith(app.InstalledPath, StringComparison.OrdinalIgnoreCase))
                                {
                                    isMatch = true;
                                }
                            }
                            catch { /* Access denied on protected system processes is expected */ }
                        }
                    }
                }
                catch { }

                if (isMatch)
                {
                    matchingProcesses.Add(proc);
                }
                else
                {
                    proc.Dispose();
                }
            }

            if (!matchingProcesses.Any()) return;

            logger?.LogInfo($"Detected {matchingProcesses.Count} running process(es) for {app.Name}. Attempting graceful close...");

            // Step 1: Graceful close
            foreach (var p in matchingProcesses)
            {
                try
                {
                    p.CloseMainWindow();
                }
                catch { }
            }

            // Step 2: Wait up to 2.5 seconds
            var stopwatch = Stopwatch.StartNew();
            while (stopwatch.ElapsedMilliseconds < 2500)
            {
                if (matchingProcesses.All(p => p.HasExited)) break;
                await Task.Delay(200);
            }

            // Step 3: Terminate any remaining lingering processes
            foreach (var p in matchingProcesses)
            {
                try
                {
                    if (!p.HasExited)
                    {
                        logger?.LogInfo($"Forcefully terminating process {p.ProcessName} (PID {p.Id}) for {app.Name}.");
                        p.Kill(entireProcessTree: true);
                    }
                }
                catch { }
                finally
                {
                    p.Dispose();
                }
            }
        }

        /// <summary>
        /// Waits for a process to exit within a specified timeout. If cancelled or timed out, kills the entire process tree.
        /// </summary>
        public static async Task<bool> WaitForExitWithTimeoutAsync(Process process, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            using var timeoutCts = new CancellationTokenSource(timeout);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, cancellationToken);

            try
            {
                await process.WaitForExitAsync(linkedCts.Token);
                return true;
            }
            catch (OperationCanceledException)
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                    }
                }
                catch { /* Ignore kill errors */ }

                if (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                    throw new TimeoutException($"Время ожидания процесса ({timeout.TotalMinutes:F0} мин) превышено. Процесс был остановлен.");
                }

                throw;
            }
        }
    }
}
