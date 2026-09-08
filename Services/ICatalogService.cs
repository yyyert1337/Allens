using System.Collections.Generic;
using System.Threading.Tasks;
using Allens.Models;

namespace Allens.Services
{
    public interface ICatalogService
    {
        Task<IEnumerable<AppItem>> GetCatalogAsync();
    }
}
