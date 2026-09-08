using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Allens.Models
{
    public partial class AppItem : ObservableObject
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("description")]
        public string Description { get; set; } = string.Empty;

        [JsonPropertyName("category")]
        public string Category { get; set; } = string.Empty;

        [JsonPropertyName("publisher")]
        public string Publisher { get; set; } = string.Empty;

        [JsonPropertyName("website")]
        public string Website { get; set; } = string.Empty;

        [JsonPropertyName("downloadUrl")]
        public string DownloadUrl { get; set; } = string.Empty;

        [JsonPropertyName("version")]
        public string Version { get; set; } = string.Empty;

        [JsonPropertyName("architecture")]
        public string Architecture { get; set; } = "x64";

        [JsonPropertyName("installerType")]
        public string InstallerType { get; set; } = "exe";

        [JsonPropertyName("silentArguments")]
        public string SilentArguments { get; set; } = string.Empty;

        [JsonPropertyName("requiresAdmin")]
        public bool RequiresAdmin { get; set; }

        [JsonPropertyName("sha256")]
        public string Sha256 { get; set; } = string.Empty;

        [JsonPropertyName("wingetId")]
        public string? WingetId { get; set; }

        [JsonPropertyName("icon")]
        public string Icon { get; set; } = string.Empty;

        [JsonIgnore]
        public string IconPath
        {
            get
            {
                if (string.IsNullOrWhiteSpace(Icon)) return string.Empty;

                var localPath = System.IO.Path.Combine(System.AppDomain.CurrentDomain.BaseDirectory, "Assets", "Icons", Icon);
                if (System.IO.File.Exists(localPath))
                {
                    return localPath;
                }

                return $"pack://application:,,,/Assets/Icons/{Icon}";
            }
        }

        [JsonPropertyName("isPopular")]
        public bool IsPopular { get; set; }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(DisplaySize))]
        [JsonPropertyName("sizeDisplay")]
        private string _sizeDisplay = "—";

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(DisplaySize))]
        [JsonIgnore]
        private string _installedSizeDisplay = string.Empty;

        [JsonPropertyName("detectionMethod")]
        public string DetectionMethod { get; set; } = string.Empty;

        [JsonPropertyName("detectionValue")]
        public string DetectionValue { get; set; } = string.Empty;

        [JsonPropertyName("uninstallMethod")]
        public string UninstallMethod { get; set; } = string.Empty;

        [JsonPropertyName("uninstallValue")]
        public string UninstallValue { get; set; } = string.Empty;

        [JsonPropertyName("uninstallArguments")]
        public string UninstallArguments { get; set; } = string.Empty;

        [JsonPropertyName("cleanupPaths")]
        public string[] CleanupPaths { get; set; } = System.Array.Empty<string>();

        [ObservableProperty]
        [JsonIgnore]
        private bool _isSelected;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(DisplaySize))]
        [JsonIgnore]
        private bool _isInstalled;

        [ObservableProperty]
        [JsonIgnore]
        private string _installedVersion = string.Empty;

        [ObservableProperty]
        [JsonIgnore]
        private string _installedPath = string.Empty;

        [ObservableProperty]
        [JsonIgnore]
        private bool _hasUpdate;

        [JsonIgnore]
        public string DisplaySize => IsInstalled && !string.IsNullOrWhiteSpace(InstalledSizeDisplay)
            ? InstalledSizeDisplay
            : SizeDisplay;
    }
}
