using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Allens.Models;

namespace Allens.Services
{
    public class SettingsService : ISettingsService
    {
        private readonly string _settingsFilePath;
        private readonly ILoggerService _logger;

        public AppSettings Settings { get; private set; } = new AppSettings();

        public SettingsService(ILoggerService logger)
        {
            _logger = logger;

            var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var directory = Path.Combine(appDataPath, "Allens");

            if (!Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            _settingsFilePath = Path.Combine(directory, "settings.json");
        }

        public async Task LoadSettingsAsync()
        {
            try
            {
                if (File.Exists(_settingsFilePath))
                {
                    var json = await File.ReadAllTextAsync(_settingsFilePath);
                    var settings = JsonSerializer.Deserialize<AppSettings>(json);
                    if (settings != null)
                    {
                        Settings = settings;
                        _logger.LogInfo("Settings loaded successfully.");
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError("Failed to load settings.", ex);
            }

            Settings = new AppSettings();
        }

        public async Task SaveSettingsAsync()
        {
            try
            {
                var json = JsonSerializer.Serialize(Settings, new JsonSerializerOptions { WriteIndented = true });
                await File.WriteAllTextAsync(_settingsFilePath, json);
                _logger.LogInfo("Settings saved successfully.");
            }
            catch (Exception ex)
            {
                _logger.LogError("Failed to save settings.", ex);
            }
        }
    }
}
