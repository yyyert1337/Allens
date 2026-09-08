using System;

namespace Allens.Models
{
    public class DownloadProgressInfo
    {
        public long BytesReceived { get; set; }
        public long TotalBytesToReceive { get; set; }
        public double ProgressPercentage { get; set; }
        public double BytesPerSecond { get; set; }
        public TimeSpan EstimatedTimeRemaining { get; set; }
    }
}
