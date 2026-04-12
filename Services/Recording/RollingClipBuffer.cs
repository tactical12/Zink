using System;
using System.Collections.Generic;
using System.Linq;

namespace Zink.Services.Recording
{
    public sealed class RollingClipBuffer : IDisposable
    {
        private readonly object _gate = new();
        private readonly LinkedList<VideoFramePacket> _frames = new();
        private readonly TimeSpan _duration;

        // Hard byte budget for raw BGRA replay frames.
        // This is much safer than a frame-count cap because 4K frames are vastly larger than 1080p frames.
        private const long MaxBufferedBytes = 768L * 1024L * 1024L; // 768 MB

        private long _totalBufferedBytes;

        public RollingClipBuffer(TimeSpan duration)
        {
            _duration = duration;
        }

        public int Count
        {
            get
            {
                lock (_gate)
                {
                    return _frames.Count;
                }
            }
        }

        public long TotalBufferedBytes
        {
            get
            {
                lock (_gate)
                {
                    return _totalBufferedBytes;
                }
            }
        }

        public void Add(VideoFramePacket packet)
        {
            lock (_gate)
            {
                _frames.AddLast(packet);
                _totalBufferedBytes += GetPacketBytes(packet);

                TrimByTimeNoLock();
                TrimByMemoryNoLock();
            }
        }

        public List<VideoFramePacket> Snapshot()
        {
            lock (_gate)
            {
                return _frames.ToList();
            }
        }

        public void Clear()
        {
            lock (_gate)
            {
                while (_frames.Count > 0)
                {
                    var frame = _frames.First!.Value;
                    _frames.RemoveFirst();
                    _totalBufferedBytes -= GetPacketBytes(frame);
                    frame.Dispose();
                }

                if (_totalBufferedBytes < 0)
                    _totalBufferedBytes = 0;
            }
        }

        private void TrimByTimeNoLock()
        {
            if (_frames.Count == 0)
                return;

            var newest = _frames.Last!.Value.Timestamp;

            while (_frames.Count > 0)
            {
                var oldest = _frames.First!.Value.Timestamp;

                if ((newest - oldest) <= _duration)
                    break;

                RemoveFirstNoLock();
            }
        }

        private void TrimByMemoryNoLock()
        {
            while (_frames.Count > 0 && _totalBufferedBytes > MaxBufferedBytes)
            {
                RemoveFirstNoLock();
            }
        }

        private void RemoveFirstNoLock()
        {
            var old = _frames.First!.Value;
            _frames.RemoveFirst();
            _totalBufferedBytes -= GetPacketBytes(old);
            old.Dispose();

            if (_totalBufferedBytes < 0)
                _totalBufferedBytes = 0;
        }

        private static int GetPacketBytes(VideoFramePacket packet)
        {
            return packet.Bgra32Bytes?.Length ?? 0;
        }

        public void Dispose()
        {
            Clear();
        }
    }
}