using System;

namespace Beeswax
{
    /// <summary>
    /// Thread-safe FIFO of float samples that grows when full (a live line is never cut short).
    /// The main thread writes network chunks; the audio thread reads.
    /// </summary>
    public sealed class PcmRing
    {
        readonly object _lock = new object();
        float[] _buf;
        int _read, _count;
        long _totalWritten;

        public PcmRing(int capacity) { _buf = new float[Math.Max(16, capacity)]; }

        public int Count { get { lock (_lock) return _count; } }
        public long TotalWritten { get { lock (_lock) return _totalWritten; } }

        /// <summary>Append little-endian PCM16 samples. length must be even (carry odd bytes yourself).</summary>
        public void WritePcm16(byte[] data, int offset, int length)
        {
            int n = length / 2;
            lock (_lock)
            {
                EnsureFree(n);
                int w = (_read + _count) % _buf.Length;
                for (int i = 0; i < n; i++)
                {
                    int b = offset + i * 2;
                    _buf[w] = (short)(data[b] | (data[b + 1] << 8)) / 32768f;
                    if (++w == _buf.Length) w = 0;
                }
                _count += n;
                _totalWritten += n;
            }
        }

        public void Write(float[] src, int offset, int n)
        {
            lock (_lock)
            {
                EnsureFree(n);
                int w = (_read + _count) % _buf.Length;
                for (int i = 0; i < n; i++)
                {
                    _buf[w] = src[offset + i];
                    if (++w == _buf.Length) w = 0;
                }
                _count += n;
                _totalWritten += n;
            }
        }

        /// <summary>Read up to n samples into dst; returns how many were read.</summary>
        public int Read(float[] dst, int offset, int n)
        {
            lock (_lock)
            {
                n = Math.Min(n, _count);
                int first = Math.Min(n, _buf.Length - _read);
                Array.Copy(_buf, _read, dst, offset, first);
                if (n > first) Array.Copy(_buf, 0, dst, offset + first, n - first);
                _read = (_read + n) % _buf.Length;
                _count -= n;
                return n;
            }
        }

        public void Clear() { lock (_lock) { _read = 0; _count = 0; } }

        void EnsureFree(int n)
        {
            if (_count + n <= _buf.Length) return;
            int cap = _buf.Length;
            while (cap < _count + n) cap *= 2;
            var next = new float[cap];
            int first = Math.Min(_count, _buf.Length - _read);
            Array.Copy(_buf, _read, next, 0, first);
            if (_count > first) Array.Copy(_buf, 0, next, first, _count - first);
            _buf = next;
            _read = 0;
        }
    }
}
