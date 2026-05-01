using System;
using System.Buffers.Binary;
using System.Text;

namespace Zink.Services.Social
{
    public static class BinaryScreenFrameProtocol
    {
        private const uint Magic = 0x3156425A;
        private const byte H264FrameKind = 1;

        public static byte[] CreateH264Frame(
            long userId,
            string callId,
            int width,
            int height,
            long timestamp,
            bool isKeyFrame,
            byte[] payload)
        {
            var callIdBytes = Encoding.UTF8.GetBytes(callId ?? "");
            int length = 4 + 1 + 8 + 4 + callIdBytes.Length + 4 + 4 + 8 + 1 + 4 + payload.Length;
            byte[] data = new byte[length];
            int offset = 0;

            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset, 4), Magic);
            offset += 4;
            data[offset++] = H264FrameKind;
            BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(offset, 8), userId);
            offset += 8;
            BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(offset, 4), callIdBytes.Length);
            offset += 4;
            callIdBytes.CopyTo(data.AsSpan(offset));
            offset += callIdBytes.Length;
            BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(offset, 4), width);
            offset += 4;
            BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(offset, 4), height);
            offset += 4;
            BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(offset, 8), timestamp);
            offset += 8;
            data[offset++] = isKeyFrame ? (byte)1 : (byte)0;
            BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(offset, 4), payload.Length);
            offset += 4;
            payload.CopyTo(data.AsSpan(offset));

            return data;
        }

        public static bool TryReadH264Frame(byte[] data, out BinaryScreenFrame frame)
        {
            frame = default;

            if (data.Length < 34)
                return false;

            int offset = 0;
            if (BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4)) != Magic)
                return false;

            offset += 4;
            if (data[offset++] != H264FrameKind)
                return false;

            long userId = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(offset, 8));
            offset += 8;

            int callIdLength = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset, 4));
            offset += 4;
            if (callIdLength < 0 || offset + callIdLength > data.Length)
                return false;

            string callId = Encoding.UTF8.GetString(data, offset, callIdLength);
            offset += callIdLength;

            if (offset + 21 > data.Length)
                return false;

            int width = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset, 4));
            offset += 4;
            int height = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset, 4));
            offset += 4;
            long timestamp = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(offset, 8));
            offset += 8;
            bool isKeyFrame = data[offset++] != 0;

            int payloadLength = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset, 4));
            offset += 4;
            if (payloadLength <= 0 || offset + payloadLength > data.Length)
                return false;

            byte[] payload = new byte[payloadLength];
            Buffer.BlockCopy(data, offset, payload, 0, payload.Length);

            frame = new BinaryScreenFrame(userId, callId, width, height, timestamp, isKeyFrame, payload);
            return true;
        }
    }

    public readonly struct BinaryScreenFrame
    {
        public BinaryScreenFrame(long userId, string callId, int width, int height, long timestamp, bool isKeyFrame, byte[] payload)
        {
            UserId = userId;
            CallId = callId;
            Width = width;
            Height = height;
            Timestamp = timestamp;
            IsKeyFrame = isKeyFrame;
            Payload = payload;
        }

        public long UserId { get; }
        public string CallId { get; }
        public int Width { get; }
        public int Height { get; }
        public long Timestamp { get; }
        public bool IsKeyFrame { get; }
        public byte[] Payload { get; }
    }
}
