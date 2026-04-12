using System;

namespace Zink.Services.Recording
{
    public sealed class VideoFramePacket : IDisposable
    {
        public TimeSpan Timestamp { get; }
        public int Width { get; }
        public int Height { get; }

        public byte[]? Bgra32Bytes { get; private set; }

        // True when this packet owns the pooled frame buffer and must return it.
        private readonly bool _ownsBuffer;
        private bool _disposed;

        public VideoFramePacket(TimeSpan timestamp, int width, int height, byte[] bgraBytes)
            : this(timestamp, width, height, bgraBytes, ownsBuffer: true)
        {
        }

        public VideoFramePacket(TimeSpan timestamp, int width, int height, byte[] bgraBytes, bool ownsBuffer)
        {
            Timestamp = timestamp;
            Width = width;
            Height = height;
            Bgra32Bytes = bgraBytes;
            _ownsBuffer = ownsBuffer;
        }

        public VideoFramePacket CreateShifted(TimeSpan timestamp)
        {
            if (Bgra32Bytes == null)
                throw new ObjectDisposedException(nameof(VideoFramePacket));

            // Shares the same underlying bytes, but does not own the buffer.
            return new VideoFramePacket(timestamp, Width, Height, Bgra32Bytes, ownsBuffer: false);
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;

            if (_ownsBuffer && Bgra32Bytes != null)
            {
                FrameBufferPool.Return(Bgra32Bytes);
            }

            Bgra32Bytes = null;
            GC.SuppressFinalize(this);
        }
    }
}