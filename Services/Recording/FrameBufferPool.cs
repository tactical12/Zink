using System.Collections.Concurrent;
using System.Threading;

namespace Zink.Services.Recording
{
    public static class FrameBufferPool
    {
        private static readonly ConcurrentBag<byte[]> _pool = new();
        private static int _count;

        // Keep this small on purpose. We want reuse, not "cache every frame forever".
        private const int MaxBuffersToKeep = 12;

        public static byte[] Rent(int size)
        {
            while (_pool.TryTake(out var buffer))
            {
                Interlocked.Decrement(ref _count);

                if (buffer.Length == size)
                    return buffer;
            }

            return new byte[size];
        }

        public static void Return(byte[]? buffer)
        {
            if (buffer == null)
                return;

            // Do NOT keep unbounded amounts of memory in the pool.
            if (buffer.Length == 0)
                return;

            int newCount = Interlocked.Increment(ref _count);
            if (newCount <= MaxBuffersToKeep)
            {
                _pool.Add(buffer);
                return;
            }

            // Pool is full - let GC reclaim this one.
            Interlocked.Decrement(ref _count);
        }

        public static void Clear()
        {
            while (_pool.TryTake(out _))
            {
                Interlocked.Decrement(ref _count);
            }

            if (_count < 0)
                _count = 0;
        }
    }
}