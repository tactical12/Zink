using Microsoft.UI.Dispatching;
using System;
using System.Threading.Tasks;

namespace Zink
{
    public static class DispatcherQueueExtensions
    {
        public static Task EnqueueAsync(this DispatcherQueue dispatcherQueue, Action action)
        {
            var tcs = new TaskCompletionSource<object?>();

            if (!dispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    action();
                    tcs.SetResult(null);
                }
                catch (Exception ex)
                {
                    tcs.SetException(ex);
                }
            }))
            {
                tcs.SetException(new InvalidOperationException("Failed to enqueue action."));
            }

            return tcs.Task;
        }
    }
}