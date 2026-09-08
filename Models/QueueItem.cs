using CommunityToolkit.Mvvm.ComponentModel;
using System;

namespace Allens.Models
{
    public enum QueueOperationType
    {
        Install,
        Reinstall,
        Update,
        Uninstall
    }

    public enum QueueItemStatus
    {
        Pending,
        Downloading,
        Verifying,
        Installing,
        Reinstalling,
        Uninstalling,
        Completed,
        Error
    }

    public partial class QueueItem : ObservableObject
    {
        public AppItem App { get; }

        [ObservableProperty]
        private QueueOperationType _operationType = QueueOperationType.Install;

        [ObservableProperty]
        private QueueItemStatus _status = QueueItemStatus.Pending;

        [ObservableProperty]
        private string _statusMessage = "Ожидание...";

        [ObservableProperty]
        private double _progressPercentage = 0;

        [ObservableProperty]
        private string _progressText = string.Empty;

        [ObservableProperty]
        private double _opacity = 1.0;

        public System.Threading.CancellationTokenSource? Cts { get; set; }

        public void Cancel()
        {
            try
            {
                Cts?.Cancel();
            }
            catch { }
        }

        public QueueItem(AppItem app, QueueOperationType operationType = QueueOperationType.Install)
        {
            App = app;
            OperationType = operationType;
        }
    }
}
