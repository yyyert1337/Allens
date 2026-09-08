namespace Allens.Models
{
    public class AppSettings
    {
        public bool IsDarkTheme { get; set; } = true;
        public string DownloadDirectory { get; set; } = string.Empty; // Empty means default %TEMP%
        public bool DeleteInstallersAfterInstallation { get; set; } = true;
    }
}
