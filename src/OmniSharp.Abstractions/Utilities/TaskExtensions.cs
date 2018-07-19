using Microsoft.Extensions.Logging;
using System.Threading;
using System.Threading.Tasks;

namespace OmniSharp.Utilities
{
    public static class TaskExtensions
    {
        public static T WaitAndGetResult<T>(this Task<T> task, CancellationToken cancellationToken)
        {
            task.Wait(cancellationToken);
            return task.Result;
        }

        public static void FireAndForget(this Task task, ILogger logger)
        {
            task.ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    if (t.IsCanceled)
                    {
                        logger.LogDebug(t.Exception, $"Cancellation of fire and forget task: {t.Exception?.Message}");
                    }
                    else
                    {
                        logger.LogError(t.Exception, $"Unhandled exception in fire and forget task: {t.Exception?.Message}");
                    }
                }
            });
        }
    }
}
