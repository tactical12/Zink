using System;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Zink.Services.Social
{
    public sealed class IncomingCallEventArgs : EventArgs
    {
        public string CallId { get; set; } = "";
        public long FromUserId { get; set; }
        public string FromUsername { get; set; } = "";
        public string FromDisplayName { get; set; } = "";
    }

    public sealed class WebRtcOfferEventArgs : EventArgs
    {
        public string CallId { get; set; } = "";
        public long FromUserId { get; set; }
        public string Sdp { get; set; } = "";
        public string SdpType { get; set; } = "";
    }

    public sealed class WebRtcAnswerEventArgs : EventArgs
    {
        public string CallId { get; set; } = "";
        public long FromUserId { get; set; }
        public string Sdp { get; set; } = "";
        public string SdpType { get; set; } = "";
    }

    public sealed class WebRtcIceEventArgs : EventArgs
    {
        public string CallId { get; set; } = "";
        public long FromUserId { get; set; }
        public string Candidate { get; set; } = "";
        public string? Mid { get; set; }
        public int? MLineIndex { get; set; }
    }

    public sealed class AudioChunkEventArgs : EventArgs
    {
        public string CallId { get; set; } = "";
        public long FromUserId { get; set; }
        public byte[] AudioData { get; set; } = Array.Empty<byte>();
    }

    public sealed class RealtimeService
    {
        private ClientWebSocket? _ws;
        private CancellationTokenSource? _cts;
        private bool _connected;

        public bool IsConnected => _connected && _ws != null && _ws.State == WebSocketState.Open;

        public event EventHandler<IncomingCallEventArgs>? IncomingCall;
        public event EventHandler<(string CallId, long FromUserId)>? CallAnswered;
        public event EventHandler<(string CallId, long FromUserId)>? CallRejected;
        public event EventHandler<(string CallId, long FromUserId)>? CallEnded;

        public event EventHandler<WebRtcOfferEventArgs>? WebRtcOfferReceived;
        public event EventHandler<WebRtcAnswerEventArgs>? WebRtcAnswerReceived;
        public event EventHandler<WebRtcIceEventArgs>? WebRtcIceReceived;

        public event EventHandler<AudioChunkEventArgs>? AudioChunkReceived;

        public async Task ConnectAsync()
        {
            if (IsConnected)
                return;

            var token = await Calling.TokenStore.Instance.GetTokenAsync();
            if (string.IsNullOrWhiteSpace(token))
                throw new Exception("No auth token found.");

            await DisconnectAsync();

            _cts = new CancellationTokenSource();
            _ws = new ClientWebSocket();

            var uri = new Uri($"wss://calls.zinkapp.net/ws?token={Uri.EscapeDataString(token)}");

            try
            {
                await _ws.ConnectAsync(uri, CancellationToken.None);
                _connected = true;
                _ = ReceiveLoopAsync(_ws, _cts.Token);
            }
            catch (WebSocketException ex)
            {
                _connected = false;
                throw new Exception($"Realtime websocket failed: {ex.Message}", ex);
            }
        }

        public async Task DisconnectAsync()
        {
            _connected = false;

            try
            {
                _cts?.Cancel();
            }
            catch
            {
            }

            if (_ws != null)
            {
                try
                {
                    if (_ws.State == WebSocketState.Open || _ws.State == WebSocketState.CloseReceived)
                    {
                        await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
                    }
                }
                catch
                {
                }

                _ws.Dispose();
                _ws = null;
            }

            _cts?.Dispose();
            _cts = null;
        }

        private async Task ReceiveLoopAsync(ClientWebSocket socket, CancellationToken cancellationToken)
        {
            var buffer = new byte[64 * 1024];

            try
            {
                while (!cancellationToken.IsCancellationRequested && socket.State == WebSocketState.Open)
                {
                    using var ms = new System.IO.MemoryStream();

                    WebSocketReceiveResult result;
                    do
                    {
                        result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);

                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            _connected = false;
                            return;
                        }

                        ms.Write(buffer, 0, result.Count);
                    }
                    while (!result.EndOfMessage);

                    var json = Encoding.UTF8.GetString(ms.ToArray());

                    SignalEnvelope? msg;
                    try
                    {
                        msg = JsonSerializer.Deserialize<SignalEnvelope>(json);
                    }
                    catch
                    {
                        continue;
                    }

                    if (msg == null || string.IsNullOrWhiteSpace(msg.Type))
                        continue;

                    switch (msg.Type)
                    {
                        case "incoming-call":
                            IncomingCall?.Invoke(this, new IncomingCallEventArgs
                            {
                                CallId = msg.CallId ?? "",
                                FromUserId = msg.FromUserId,
                                FromUsername = msg.FromUsername ?? "",
                                FromDisplayName = msg.FromDisplayName ?? ""
                            });
                            break;

                        case "call-accepted":
                            CallAnswered?.Invoke(this, (msg.CallId ?? "", msg.FromUserId));
                            break;

                        case "call-declined":
                            CallRejected?.Invoke(this, (msg.CallId ?? "", msg.FromUserId));
                            break;

                        case "call-ended":
                            CallEnded?.Invoke(this, (msg.CallId ?? "", msg.FromUserId));
                            break;

                        case "webrtc-offer":
                            WebRtcOfferReceived?.Invoke(this, new WebRtcOfferEventArgs
                            {
                                CallId = msg.CallId ?? "",
                                FromUserId = msg.FromUserId,
                                Sdp = msg.Sdp ?? "",
                                SdpType = msg.SdpType ?? "offer"
                            });
                            break;

                        case "webrtc-answer":
                            WebRtcAnswerReceived?.Invoke(this, new WebRtcAnswerEventArgs
                            {
                                CallId = msg.CallId ?? "",
                                FromUserId = msg.FromUserId,
                                Sdp = msg.Sdp ?? "",
                                SdpType = msg.SdpType ?? "answer"
                            });
                            break;

                        case "webrtc-ice":
                            WebRtcIceReceived?.Invoke(this, new WebRtcIceEventArgs
                            {
                                CallId = msg.CallId ?? "",
                                FromUserId = msg.FromUserId,
                                Candidate = msg.Candidate ?? "",
                                Mid = msg.Mid,
                                MLineIndex = msg.MLineIndex
                            });
                            break;

                        case "audio-chunk":
                            if (!string.IsNullOrWhiteSpace(msg.AudioBase64))
                            {
                                try
                                {
                                    var bytes = Convert.FromBase64String(msg.AudioBase64);
                                    AudioChunkReceived?.Invoke(this, new AudioChunkEventArgs
                                    {
                                        CallId = msg.CallId ?? "",
                                        FromUserId = msg.FromUserId,
                                        AudioData = bytes
                                    });
                                }
                                catch
                                {
                                }
                            }
                            break;
                    }
                }
            }
            catch
            {
                _connected = false;
            }
        }

        public async Task CallUserAsync(long userId, string callId)
        {
            if (!IsConnected)
                await ConnectAsync();

            await SendAsync(new SignalEnvelope
            {
                Type = "call-invite",
                TargetUserId = userId,
                CallId = callId
            });
        }

        public async Task AcceptCallAsync(long userId, string callId)
        {
            if (!IsConnected)
                await ConnectAsync();

            await SendAsync(new SignalEnvelope
            {
                Type = "call-accept",
                TargetUserId = userId,
                CallId = callId
            });
        }

        public async Task RejectCallAsync(long userId, string callId)
        {
            if (!IsConnected)
                await ConnectAsync();

            await SendAsync(new SignalEnvelope
            {
                Type = "call-decline",
                TargetUserId = userId,
                CallId = callId
            });
        }

        public async Task EndCallAsync(long userId, string callId)
        {
            if (!IsConnected)
                return;

            await SendAsync(new SignalEnvelope
            {
                Type = "call-ended",
                TargetUserId = userId,
                CallId = callId
            });
        }

        public async Task SendOfferAsync(long userId, string callId, string sdp, string sdpType)
        {
            if (!IsConnected)
                await ConnectAsync();

            await SendAsync(new SignalEnvelope
            {
                Type = "webrtc-offer",
                TargetUserId = userId,
                CallId = callId,
                Sdp = sdp,
                SdpType = sdpType
            });
        }

        public async Task SendAnswerAsync(long userId, string callId, string sdp, string sdpType)
        {
            if (!IsConnected)
                await ConnectAsync();

            await SendAsync(new SignalEnvelope
            {
                Type = "webrtc-answer",
                TargetUserId = userId,
                CallId = callId,
                Sdp = sdp,
                SdpType = sdpType
            });
        }

        public async Task SendIceCandidateAsync(long userId, string callId, string candidate, string? mid, int? mLineIndex)
        {
            if (!IsConnected)
                await ConnectAsync();

            await SendAsync(new SignalEnvelope
            {
                Type = "webrtc-ice",
                TargetUserId = userId,
                CallId = callId,
                Candidate = candidate,
                Mid = mid,
                MLineIndex = mLineIndex
            });
        }

        public async Task SendAudioChunkAsync(long userId, string callId, byte[] audioData)
        {
            if (!IsConnected)
                await ConnectAsync();

            if (audioData == null || audioData.Length == 0)
                return;

            await SendAsync(new SignalEnvelope
            {
                Type = "audio-chunk",
                TargetUserId = userId,
                CallId = callId,
                AudioBase64 = Convert.ToBase64String(audioData)
            });
        }

        private async Task SendAsync(SignalEnvelope obj)
        {
            if (_ws == null || _ws.State != WebSocketState.Open)
                throw new Exception("Realtime connection is not open.");

            var json = JsonSerializer.Serialize(obj);
            var bytes = Encoding.UTF8.GetBytes(json);

            await _ws.SendAsync(
                new ArraySegment<byte>(bytes),
                WebSocketMessageType.Text,
                true,
                CancellationToken.None);
        }
    }

    public sealed class SignalEnvelope
    {
        public string Type { get; set; } = "";
        public string? CallId { get; set; }
        public long FromUserId { get; set; }
        public string? FromUsername { get; set; }
        public string? FromDisplayName { get; set; }
        public long TargetUserId { get; set; }

        public string? Sdp { get; set; }
        public string? SdpType { get; set; }
        public string? Candidate { get; set; }
        public string? Mid { get; set; }
        public int? MLineIndex { get; set; }

        public string? AudioBase64 { get; set; }
    }
}