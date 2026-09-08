using System.Collections.Generic;
using Allens.Models;

namespace Allens.Services
{
    public interface IApplicationDetectionService
    {
        bool IsInstalled(AppItem app);
        void UpdateInstallationStatus(IEnumerable<AppItem> apps);
        void InspectApp(AppItem app);
        void MeasureFolderSizesBackground(IEnumerable<AppItem> apps);
    }
}
