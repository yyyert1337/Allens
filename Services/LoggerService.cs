using System;
using System.IO;

namespace Allens.Services
{
    public class LoggerService : ILoggerService
    {
        private readonly string _logFilePath;
        private readonly object _lockObj = new object();

        public LoggerService()
        {
            var tempPath = Path.GetTempPath();
            var directory = Path.Combine(tempPath, "Allens");

            if (!Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            _logFilePath = Path.Combine(directory, "logs.txt");
        }

        public void LogInfo(string message)
        {
            WriteLog("INFO", message);
        }

        public void LogError(string message, Exception? ex = null)
        {
            var fullMessage = ex == null ? message : $"{message} | Exception: {ex.Message}";
            WriteLog("ERROR", fullMessage);
        }

        private const long MaxLogFileSizeBytes = 5 * 1024 * 1024; 

        private void WriteLog(string level, string message)
        {
            lock (_lockObj)
            {
                try
                {
                    if (File.Exists(_logFilePath))
                    {
                        var fi = new FileInfo(_logFilePath);
                        if (fi.Length > MaxLogFileSizeBytes)
                        {
                            var backupPath = _logFilePath + ".old";
                            if (File.Exists(backupPath)) File.Delete(backupPath);
                            File.Move(_logFilePath, backupPath);
                        }
                    }

                    var logLine = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{level}] {message}{Environment.NewLine}";
                    File.AppendAllText(_logFilePath, logLine);
                }
                catch
                {

                }
            }
        }
    }
}
