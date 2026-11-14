using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using NativeWebSocket;
using UnityEngine;
using Debug = UnityEngine.Debug;

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
    ///  * SendText()/Send() block the calling thread until the frame is handed to the OS. On a half-open link
    ///    (Wi-Fi drop) that blocks for minutes once the kernel send buffer is full, which mic streaming can do in
    ///    a second or two. So sends are queued and written by one pool thread per socket, in order (text and binary
    ///    share the queue, so listen.start → MIC frames → listen.end stay ordered). SendStallSeconds tells the
    ///    owner when a write has been stuck; Abort() cancels it.
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
        Outbox _outbox;
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
            _outbox = new Outbox(ws);
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

        /// <summary>Queue a text frame. False (dropped) when not open.</summary>
        public bool SendText(string text) => text != null && Enqueue(text, text.Length);

        /// <summary>Queue a binary frame. The array must not be modified afterwards. False (dropped) when not open.</summary>
        public bool SendBytes(byte[] data) => data != null && Enqueue(data, data.Length);

        /// <summary>Bytes queued or being written right now (not yet handed to the OS).</summary>
        public int QueuedSendBytes
        {
            get { var box = _outbox; if (box == null) return 0; lock (box) return box.Bytes; }
        }

        /// <summary>How long the oldest pending send has waited without any write completing (0 when idle).</summary>
        public float SendStallSeconds
        {
            get
            {
                var box = _outbox;
                if (box == null) return 0f;
                long since;
                lock (box) since = box.PendingSince;
                return since == 0 ? 0f : (float)((Stopwatch.GetTimestamp() - since) / (double)Stopwatch.Frequency);
            }
        }

        bool Enqueue(object item, int size)
        {
            if (!IsOpen) return false;
#if UNITY_WEBGL && !UNITY_EDITOR
            // The browser socket never blocks and must be used from the main thread.
            try { Observe(item is string t ? _ws.SendText(t) : _ws.Send((byte[])item)); return true; }
            catch (Exception e) { Debug.LogWarning("[Beeswax] send failed: " + e.Message); return false; }
#else
            var box = _outbox;
            if (box == null) return false;
            bool start;
            lock (box)
            {
                if (box.Closed) return false;
                box.Items.Enqueue(item);
                box.Bytes += size;
                if (box.PendingSince == 0) box.PendingSince = Stopwatch.GetTimestamp();
                start = !box.Running;
                if (start) box.Running = true;
            }
            if (start) ThreadPool.QueueUserWorkItem(Pump, box);
            return true;
#endif
        }

        sealed class Outbox
        {
            public readonly WebSocket Ws;
            public readonly Queue<object> Items = new Queue<object>(); // string (text frame) or byte[] (binary frame)
            public int Bytes;
            public long PendingSince; // Stopwatch timestamp; 0 = nothing pending
            public bool Running, Closed;
            public Outbox(WebSocket ws) { Ws = ws; }
        }

        // Pool thread: writes one frame at a time, in queue order, until the queue is empty or the socket is dropped.
        static void Pump(object state)
        {
            var box = (Outbox)state;
            while (true)
            {
                object item;
                lock (box)
                {
                    if (box.Closed || box.Items.Count == 0)
                    {
                        box.Running = false;
                        box.PendingSince = 0;
                        if (box.Closed) { box.Items.Clear(); box.Bytes = 0; }
                        return;
                    }
                    item = box.Items.Peek();
                }
                int size = item is string s ? s.Length : ((byte[])item).Length;
                try
                {
                    // NativeWebSocket writes synchronously before returning the task (see above); faults are observed.
                    Observe(item is string text ? box.Ws.SendText(text) : box.Ws.Send((byte[])item));
                }
                catch (Exception e)
                {
                    bool closed;
                    lock (box) closed = box.Closed;
                    if (!closed) Debug.LogWarning("[Beeswax] send failed: " + e.GetBaseException().Message);
                }
                lock (box)
                {
                    if (box.Items.Count > 0 && ReferenceEquals(box.Items.Peek(), item)) box.Items.Dequeue();
                    box.Bytes = Math.Max(0, box.Bytes - size);
                    box.PendingSince = box.Items.Count > 0 ? Stopwatch.GetTimestamp() : 0;
                }
            }
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
            var box = _outbox;
            _outbox = null;
            if (box != null) lock (box) { box.Closed = true; if (!box.Running) { box.Items.Clear(); box.Bytes = 0; box.PendingSince = 0; } }
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
