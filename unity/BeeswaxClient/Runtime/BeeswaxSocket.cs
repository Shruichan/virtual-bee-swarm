using System;
using System.Threading.Tasks;
using NativeWebSocket;
using UnityEngine;

namespace Beeswax
{
    /// <summary>
    /// One NativeWebSocket connection with a connect timeout. Call Tick() every frame from a MonoBehaviour:
    /// it dispatches messages and raises Opened / Message / Closed on the main thread, in that order.
    /// Reconnect policy lives in the owner (BeeswaxBus, OrbisWindow).
    ///
    /// NativeWebSocket notes (com.endel.nativewebsocket 1.1.6, non-WebGL, read from its source):
    ///  * Connect() completes only when the socket closes, and has no timeout of its own.
    ///  * OnOpen/OnError/OnClose run on the main thread (sync-context continuations); the receive loop runs on a
    ///    pool thread and only queues frames, which DispatchMessageQueue() hands to OnMessage on the main thread.
    ///  * Close() is a graceful handshake that can hang on a dead link; CancelConnection() aborts.
    ///  * Text and binary frames both arrive as OnMessage(byte[]).
    ///  * SendText()/Send() block the calling thread until the frame is handed to the OS (fine for small frames).
    ///  * Server pings are answered by ClientWebSocket itself; they never reach OnMessage.
    /// Callbacks from a socket we already abandoned are dropped (generation counter).
    /// </summary>
    public sealed class BeeswaxSocket
    {
        public enum Status { Idle, Connecting, Open }

        public event Action Opened;
        /// <summary>(wasOpen, reason). Not raised for Close()/Abort() you called yourself.</summary>
        public event Action<bool, string> Closed;
        public event Action<byte[]> Message;

        public Status State { get; private set; } = Status.Idle;
        public string Url { get; private set; } = "";
        public float ConnectTimeoutSeconds = 6f;
        /// <summary>Realtime (Time.realtimeSinceStartup) when the socket opened.</summary>
        public float OpenedAt { get; private set; }

        readonly object _lock = new object();
        WebSocket _ws;
        int _gen;
        float _connectStartedAt;
        bool _openedFlag, _closedFlag;
        string _closeReason = "", _lastError = "";

        public void Open(string url)
        {
            Abort();
            int gen;
            lock (_lock) { gen = ++_gen; _openedFlag = _closedFlag = false; _closeReason = _lastError = ""; }
            Url = url;
            WebSocket ws;
            try { ws = new WebSocket(url); }
            catch (Exception e) { MarkClosed(gen, "bad url: " + e.Message); State = Status.Connecting; return; }

            _ws = ws;
            State = Status.Connecting;
            _connectStartedAt = Time.realtimeSinceStartup;
            ws.OnOpen += () => { lock (_lock) if (gen == _gen) _openedFlag = true; };
            ws.OnError += msg => { lock (_lock) if (gen == _gen) _lastError = msg ?? ""; };
            ws.OnClose += code => MarkClosed(gen, "closed " + (int)code + " " + code);
            ws.OnMessage += data => { if (gen == _gen) Message?.Invoke(data); };
            try { Observe(ws.Connect()); }
            catch (Exception e) { MarkClosed(gen, "connect threw: " + e.Message); }
        }

        public void Tick()
        {
            if (State == Status.Idle) return;
            var ws = _ws;

            bool opened, closed;
            string reason;
            lock (_lock)
            {
                opened = _openedFlag; _openedFlag = false;
                closed = _closedFlag;
                reason = string.IsNullOrEmpty(_lastError) ? _closeReason : _closeReason + " (" + _lastError + ")";
            }

            if (State == Status.Connecting && !opened && !closed &&
                Time.realtimeSinceStartup - _connectStartedAt > ConnectTimeoutSeconds)
            {
                Abort();
                Closed?.Invoke(false, "connect timeout after " + ConnectTimeoutSeconds + " s");
                return;
            }

            if (opened && State == Status.Connecting && !closed)
            {
                State = Status.Open;
                OpenedAt = Time.realtimeSinceStartup;
                Opened?.Invoke();
            }
#if !UNITY_WEBGL || UNITY_EDITOR
            if (ws != null && ws == _ws) ws.DispatchMessageQueue();
#endif
            if (closed && ws == _ws && State != Status.Idle)
            {
                bool wasOpen = State == Status.Open;
                Drop();
                Closed?.Invoke(wasOpen, reason);
            }
        }

        public bool IsOpen => State == Status.Open && _ws != null && _ws.State == WebSocketState.Open;

        public bool SendText(string text)
        {
            if (!IsOpen) return false;
            try { Observe(_ws.SendText(text)); return true; }
            catch (Exception e) { Debug.LogWarning("[Beeswax] send failed: " + e.Message); return false; }
        }

        public bool SendBytes(byte[] data)
        {
            if (!IsOpen) return false;
            try { Observe(_ws.Send(data)); return true; }
            catch (Exception e) { Debug.LogWarning("[Beeswax] send failed: " + e.Message); return false; }
        }

        /// <summary>Graceful close (no Closed event).</summary>
        public void Close()
        {
            var ws = Drop();
            if (ws == null) return;
            try
            {
                if (ws.State == WebSocketState.Open) Observe(ws.Close());
                else ws.CancelConnection();
            }
            catch (Exception) { /* already gone */ }
        }

        /// <summary>Hard abort, for dead or abandoned links (no Closed event).</summary>
        public void Abort()
        {
            var ws = Drop();
            if (ws == null) return;
            try { ws.CancelConnection(); } catch (Exception) { /* already gone */ }
        }

        WebSocket Drop()
        {
            var ws = _ws;
            _ws = null;
            lock (_lock) _gen++;
            State = Status.Idle;
            return ws;
        }

        void MarkClosed(int gen, string reason)
        {
            lock (_lock)
            {
                if (gen != _gen || _closedFlag) return;
                _closedFlag = true;
                _closeReason = reason;
            }
        }

        static void Observe(Task t)
        {
            t?.ContinueWith(x => { _ = x.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
        }
    }
}
