using System;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Zink.Services.Calling
{
    public sealed class SignalingClient : IAsyncDisposable
    {
        private readonly SemaphoreSlim _sendLock = new(1, 1);
        private ClientWebSocket? _socket;
        private CancellationTokenSource? _receiveLoopCts;
        private Task? _receiveLoopTask;

        public bool IsConnected => _socket?.State == WebSocketState.Open;

        public event EventHandler<string>? MessageReceived;
        public event EventHandler<string>? StatusChanged;
        public event EventHandler<Exception>? ErrorOccurred;

        public async Task ConnectAsync(string token, CancellationToken cancellationToken = default)
        {
            await DisconnectAsync();

            _socket = new ClientWebSocket();
            _receiveLoopCts = new CancellationTokenSource();

            var wsUrl = CallServerConfig.GetWebSocketUrl(token);

            StatusChanged?.Invoke(this, $"Connecting to {wsUrl}");

            try
            {
                await _socket.ConnectAsync(new Uri(wsUrl), cancellationToken);
                StatusChanged?.Invoke(this, "Connected.");

                _receiveLoopTask = Task.Run(() => ReceiveLoopAsync(_receiveLoopCts.Token));
            }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke(this, ex);
                throw;
            }
        }

        public async Task SendTextAsync(string text, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(text))
                throw new ArgumentException("Message cannot be empty.", nameof(text));

            if (_socket == null || _socket.State != WebSocketState.Open)
                throw new InvalidOperationException("WebSocket is not connected.");

            var bytes = Encoding.UTF8.GetBytes(text);

            await _sendLock.WaitAsync(cancellationToken);
            try
            {
                await _socket.SendAsync(
                    new ArraySegment<byte>(bytes),
                    WebSocketMessageType.Text,
                    true,
                    cancellationToken);
            }
            finally
            {
                _sendLock.Release();
            }
        }

        private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
        {
            if (_socket == null)
                return;

            var buffer = new byte[8192];

            try
            {
                while (!cancellationToken.IsCancellationRequested &&
                       _socket.State == WebSocketState.Open)
                {
                    using var ms = new System.IO.MemoryStream();

                    WebSocketReceiveResult result;
                    do
                    {
                        result = await _socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);

                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            StatusChanged?.Invoke(this, "Server closed the WebSocket.");
                            await DisconnectAsync();
                            return;
                        }

                        ms.Write(buffer, 0, result.Count);

                    } while (!result.EndOfMessage);

                    var message = Encoding.UTF8.GetString(ms.ToArray());
                    MessageReceived?.Invoke(this, message);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke(this, ex);
            }
        }

        public async Task DisconnectAsync()
        {
            try
            {
                _receiveLoopCts?.Cancel();
            }
            catch
            {
            }

            if (_socket != null)
            {
                try
                {
                    if (_socket.State == WebSocketState.Open || _socket.State == WebSocketState.CloseReceived)
                    {
                        await _socket.CloseAsync(
                            WebSocketCloseStatus.NormalClosure,
                            "Client disconnect",
                            CancellationToken.None);
                    }
                }
                catch
                {
                }

                _socket.Dispose();
                _socket = null;
            }

            _receiveLoopCts?.Dispose();
            _receiveLoopCts = null;
            _receiveLoopTask = null;

            StatusChanged?.Invoke(this, "Disconnected.");
        }

        public async ValueTask DisposeAsync()
        {
            await DisconnectAsync();
            _sendLock.Dispose();
        }
    }
}