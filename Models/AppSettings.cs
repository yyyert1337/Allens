namespace Allens.Models
{
    public class AppSettings
    {
        public bool IsDarkTheme { get; set; } = true;
        public string DownloadDirectory { get; set; } = string.Empty; 
        public bool DeleteInstallersAfterInstallation { get; set; } = true;
    }
}
