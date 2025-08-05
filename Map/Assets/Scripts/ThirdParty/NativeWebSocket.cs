// Scripts/ThirdParty/NativeWebSocket.cs (最終修正版)
// A lightweight WebSocket client for Unity.
// By Endel Harris (https://github.com/endel/NativeWebSocket)
// With a fix for enum type casting.

using System;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NativeWebSocket
{
    public enum WebSocketState
    {
        Connecting,
        Open,
        Closing,
        Closed
    }

    public class WebSocket
    {
        private readonly ClientWebSocket _ws = new ClientWebSocket();
        private readonly Uri _uri;
        private readonly CancellationTokenSource _cancellationTokenSource = new CancellationTokenSource();
        private readonly Queue<byte[]> _messageQueue = new Queue<byte[]>();

        public event Action OnOpen;
        public event Action<byte[]> OnMessage;
        public event Action<string> OnError;
        public event Action<WebSocketCloseCode> OnClose;

        public WebSocket(string url)
        {
            _uri = new Uri(url);
        }

        public WebSocketState State
        {
            get
            {
                switch (_ws.State)
                {
                    case System.Net.WebSockets.WebSocketState.Connecting:
                        return WebSocketState.Connecting;
                    case System.Net.WebSockets.WebSocketState.Open:
                        return WebSocketState.Open;
                    case System.Net.WebSockets.WebSocketState.CloseSent:
                    case System.Net.WebSockets.WebSocketState.CloseReceived:
                        return WebSocketState.Closing;
                    case System.Net.WebSockets.WebSocketState.Closed:
                    case System.Net.WebSockets.WebSocketState.Aborted:
                    case System.Net.WebSockets.WebSocketState.None:
                    default:
                        return WebSocketState.Closed;
                }
            }
        }

        public async Task Connect()
        {
            try
            {
                await _ws.ConnectAsync(_uri, _cancellationTokenSource.Token);
                OnOpen?.Invoke();
                await Listen();
            }
            catch (Exception ex)
            {
                OnError?.Invoke(ex.Message);
            }
        }

        public async Task Send(byte[] data)
        {
            if (State != WebSocketState.Open)
            {
                OnError?.Invoke("WebSocket is not open.");
                return;
            }
            await _ws.SendAsync(new ArraySegment<byte>(data), WebSocketMessageType.Binary, true, _cancellationTokenSource.Token);
        }

        public async Task SendText(string text)
        {
            var data = Encoding.UTF8.GetBytes(text);
            await Send(data);
        }

        public async Task Close()
        {
            if (State != WebSocketState.Open) return;
            try
            {
                // ### 核心修正：將自定義枚舉顯式轉換為系統枚舉 ###
                var closeStatus = (WebSocketCloseStatus)WebSocketCloseCode.NormalClosure;
                await _ws.CloseAsync(closeStatus, "Closing", _cancellationTokenSource.Token);
            }
            catch (Exception ex)
            {
                OnError?.Invoke(ex.Message);
            }
        }

        public void DispatchMessageQueue()
        {
            lock (_messageQueue)
            {
                while (_messageQueue.Count > 0)
                {
                    OnMessage?.Invoke(_messageQueue.Dequeue());
                }
            }
        }

        private async Task Listen()
        {
            var buffer = new byte[1024 * 8]; // 增加緩衝區大小以處理更大的消息
            try
            {
                while (State == WebSocketState.Open && !_cancellationTokenSource.IsCancellationRequested)
                {
                    var result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), _cancellationTokenSource.Token);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        // ### 核心修正：將系統枚舉顯式轉換為自定義枚舉 ###
                        OnClose?.Invoke((WebSocketCloseCode)result.CloseStatus);
                        return;
                    }

                    using (var ms = new MemoryStream())
                    {
                        ms.Write(buffer, 0, result.Count);
                        while (!result.EndOfMessage)
                        {
                             if (_cancellationTokenSource.IsCancellationRequested) break;
                            result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), _cancellationTokenSource.Token);
                            ms.Write(buffer, 0, result.Count);
                        }
                        
                        lock (_messageQueue)
                        {
                            _messageQueue.Enqueue(ms.ToArray());
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // 正常關閉時會觸發，忽略即可
            }
            catch (Exception ex)
            {
                OnError?.Invoke(ex.Message);
            }
            finally
            {
                if (State != WebSocketState.Closed)
                {
                    OnClose?.Invoke(WebSocketCloseCode.AbnormalClosure);
                }
            }
        }
    }

    // Unity WebSocketCloseCode enum
    public enum WebSocketCloseCode
    {
        // 這些值與 System.Net.WebSockets.WebSocketCloseStatus 的值是匹配的
        NormalClosure = 1000,
        Away = 1001,
        ProtocolError = 1002,
        UnsupportedData = 1003,
        AbnormalClosure = 1006,
        InvalidFramePayloadData = 1007,
        PolicyViolation = 1008,
        MessageTooBig = 1009,
        MandatoryExt = 1010,
        InternalServerError = 1011,
        TlsHandshake = 1015
    }
}