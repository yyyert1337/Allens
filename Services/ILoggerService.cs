using System;

namespace Allens.Services
{
    public interface ILoggerService
    {
        void LogInfo(string message);
        void LogError(string message, Exception? ex = null);
    }
}
