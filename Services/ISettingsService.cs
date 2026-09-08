using System.Threading.Tasks;
using Allens.Models;

namespace Allens.Services
{
    public interface ISettingsService
    {
        AppSettings Settings { get; }
        Task LoadSettingsAsync();
        Task SaveSettingsAsync();
    }
}
