using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Allens.Models;

namespace Allens.Services
{
    public class CatalogService : ICatalogService
    {
        private readonly string _catalogPath;
        private readonly string _cachePath;
        private const string RemoteCatalogUrl = "https://raw.githubusercontent.com/yyyert/Allens/main/Data/apps.json";
        private static readonly HttpClient _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };

        public CatalogService()
        {
            _catalogPath = Path.Combine(AppContext.BaseDirectory, "Data", "apps.json");
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            _cachePath = Path.Combine(localAppData, "Allens", "apps_cache.json");
        }

        public async Task<IEnumerable<AppItem>> GetCatalogAsync()
        {
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

            // Trigger background sync with remote GitHub raw catalog to keep cache fresh
            _ = TryUpdateRemoteCatalogAsync();

            // 1. Check local cache from previous remote updates
            if (File.Exists(_cachePath))
            {
                try
                {
                    using var stream = File.OpenRead(_cachePath);
                    var apps = await JsonSerializer.DeserializeAsync<List<AppItem>>(stream, options);
                    if (apps != null && apps.Count > 0)
                    {
                        return apps;
                    }
                }
                catch { }
            }

            // 2. Prioritize Embedded Resource compiled directly inside the executable
            try
            {
                var assembly = typeof(CatalogService).Assembly;
                using var resStream = assembly.GetManifestResourceStream("Allens.Data.apps.json");
                if (resStream != null)
                {
                    var apps = await JsonSerializer.DeserializeAsync<List<AppItem>>(resStream, options);
                    if (apps != null && apps.Count > 0)
                    {
                        return apps;
                    }
                }
            }
            catch { }

            // 3. Fallback to WPF Pack URI resource
            try
            {
                var uri = new Uri("pack://application:,,,/Data/apps.json");
                var streamInfo = System.Windows.Application.GetResourceStream(uri);
                if (streamInfo?.Stream != null)
                {
                    using var stream = streamInfo.Stream;
                    var apps = await JsonSerializer.DeserializeAsync<List<AppItem>>(stream, options);
                    if (apps != null && apps.Count > 0)
                    {
                        return apps;
                    }
                }
            }
            catch { }

            // 4. Fallback to external file on disk
            if (File.Exists(_catalogPath))
            {
                try
                {
                    using var stream = File.OpenRead(_catalogPath);
                    var apps = await JsonSerializer.DeserializeAsync<List<AppItem>>(stream, options);
                    if (apps != null && apps.Count > 0)
                    {
                        return apps;
                    }
                }
                catch { }
            }

            return new List<AppItem>();
        }

        private async Task TryUpdateRemoteCatalogAsync()
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, RemoteCatalogUrl);
                request.Headers.Add("User-Agent", "AllensApp/1.0");

                using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
                if (response.IsSuccessStatusCode)
                {
                    var json = await response.Content.ReadAsStringAsync();
                    var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                    var apps = JsonSerializer.Deserialize<List<AppItem>>(json, options);
                    if (apps != null && apps.Count > 0)
                    {
                        var dir = Path.GetDirectoryName(_cachePath);
                        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                        {
                            Directory.CreateDirectory(dir);
                        }
                        await File.WriteAllTextAsync(_cachePath, json);
                    }
                }
            }
            catch
            {
                // Silently ignore network or parsing failures during background sync
            }
        }
    }
}
