using System;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Zink.Models;

namespace Zink.Services
{
    public sealed class NativeSignalingClient : IAsyncDisposable
    {
        private ClientWebSocket? _socket;
        private CancellationTokenSource? _cts;
        private Task? _receiveLoop;

        public string RoomId { get; private set; } = "";
        public string UserId { get; private set; } = "";

        public event Action<SignalEnvelope>? MessageReceived;
        public event Action<string>? StatusChanged;

        public async Task ConnectAsync(string wsUrl, string roomId, string userId)
        {
            if (_socket != null && _socket.State == WebSocketState.Open)
                return;

            RoomId = roomId;
            UserId = userId;

            _socket = new ClientWebSocket();
            _cts = new CancellationTokenSource();

            var url = $"{wsUrl}?room={Uri.EscapeDataString(roomId)}&user={Uri.EscapeDataString(userId)}";
            await _socket.ConnectAsync(new Uri(url), _cts.Token);

            StatusChanged?.Invoke("Connected to signaling.");

            _receiveLoop = Task.Run(ReceiveLoopAsync);
        }

        public async Task SendAsync(SignalEnvelope message)
        {
            if (_socket == null || _socket.State != WebSocketState.Open)
                throw new InvalidOperationException("WebSocket is not connected.");

            message.RoomId = RoomId;
            message.FromUser = UserId;

            var json = JsonSerializer.Serialize(message);
            var bytes = Encoding.UTF8.GetBytes(json);

            await _socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
        }

        private async Task ReceiveLoopAsync()
        {
            if (_socket == null)
                return;

            var buffer = new byte[64 * 1024];

            try
            {
                while (_socket.State == WebSocketState.Open)
                {
                    using var ms = new MemoryStream();
                    WebSocketReceiveResult result;

                    do
                    {
                        result = await _socket.ReceiveAsync(buffer, CancellationToken.None);

                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            StatusChanged?.Invoke("Signaling disconnected.");
                            return;
                        }

                        ms.Write(buffer, 0, result.Count);
                    }
                    while (!result.EndOfMessage);

                    var json = Encoding.UTF8.GetString(ms.ToArray());
                    var message = JsonSerializer.Deserialize<SignalEnvelope>(json);
                    if (message != null)
                    {
                        MessageReceived?.Invoke(message);
                    }
                }
            }
            catch (Exception ex)
            {
                StatusChanged?.Invoke("Signaling receive error: " + ex.Message);
            }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                _cts?.Cancel();

                if (_socket != null && _socket.State == WebSocketState.Open)
                {
                    await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
                }
            }
            catch
            {
            }
        }
    }
}