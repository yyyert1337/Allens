using System;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using Allens.Models;

namespace Allens.Helpers
{
    public static class DiskSpaceHelper
    {
        public static bool CheckFreeSpace(string targetPath, long requiredBytes, out long availableFreeBytes)
        {
            availableFreeBytes = 0;
            try
            {
                var fullPath = Path.GetFullPath(Environment.ExpandEnvironmentVariables(targetPath));
                var root = Path.GetPathRoot(fullPath);
                if (string.IsNullOrWhiteSpace(root))
                {
                    return true;
                }

                var drive = new DriveInfo(root);
                if (drive.IsReady)
                {
                    availableFreeBytes = drive.AvailableFreeSpace;
                    return drive.AvailableFreeSpace >= requiredBytes;
                }

                return true;
            }
            catch
            {

                return true;
            }
        }

        public static long EstimateRequiredBytes(AppItem app)
        {
            long baseBytes = 0;

            if (!string.IsNullOrWhiteSpace(app.SizeDisplay))
            {
                baseBytes = ParseSizeToBytes(app.SizeDisplay);
            }

            if (baseBytes <= 0)
            {
                baseBytes = 300L * 1024 * 1024;
            }

            if (app.InstallerType.Equals("zip", StringComparison.OrdinalIgnoreCase) ||
                app.InstallerType.Equals("portable", StringComparison.OrdinalIgnoreCase))
            {
                return baseBytes * 3;
            }

            return (long)(baseBytes * 2.5);
        }

        public static long ParseSizeToBytes(string sizeStr)
        {
            try
            {
                var match = Regex.Match(sizeStr.Trim(), @"^([\d.,]+)\s*([A-Za-z]+)?$");
                if (!match.Success) return 0;

                var numberStr = match.Groups[1].Value.Replace(',', '.');
                if (!double.TryParse(numberStr, NumberStyles.Any, CultureInfo.InvariantCulture, out double value))
                {
                    return 0;
                }

                var unit = match.Groups[2].Value.ToUpperInvariant();
                return unit switch
                {
                    "GB" => (long)(value * 1024 * 1024 * 1024),
                    "MB" => (long)(value * 1024 * 1024),
                    "KB" => (long)(value * 1024),
                    "B" => (long)value,
                    _ => (long)(value * 1024 * 1024) 
                };
            }
            catch
            {
                return 0;
            }
        }

        public static string FormatBytes(long bytes)
        {
            if (bytes >= 1024L * 1024 * 1024)
            {
                return $"{(double)bytes / (1024 * 1024 * 1024):0.0} GB";
            }
            if (bytes >= 1024L * 1024)
            {
                return $"{(double)bytes / (1024 * 1024):0.0} MB";
            }
            if (bytes >= 1024L)
            {
                return $"{(double)bytes / 1024:0.0} KB";
            }
            return $"{bytes} B";
        }
    }
}
