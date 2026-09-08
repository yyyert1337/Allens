using System;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using Allens.Models;

namespace Allens.Services
{
    public interface IInstallationQueueService
    {
        ObservableCollection<QueueItem> Queue { get; }
        bool IsProcessing { get; }
        
        void AddToQueue(AppItem app, QueueOperationType operationType = QueueOperationType.Install);
        void RemoveFromQueue(AppItem app);
        void ClearQueue();
        
        Task ProcessQueueAsync(CancellationToken cancellationToken = default);
        Task ScheduleAutoDismissAsync(QueueItem item);
    }
}
