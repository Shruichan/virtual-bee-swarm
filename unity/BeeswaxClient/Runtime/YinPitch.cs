using System;

namespace Beeswax
{
    /// <summary>YIN pitch estimator (de Cheveigné &amp; Kawahara, 2002). Allocation-free; no Unity dependency.</summary>
    public static class YinPitch
    {
        /// <summary>
        /// Pitch in Hz of x[start .. start+window+tauMax), or 0 when there is no clear pitch in [minHz, maxHz].
        /// clarity is 1 - the normalized difference at the chosen lag (1 = perfectly periodic).
        /// scratch must hold at least sampleRate/minHz + 2 floats.
        /// </summary>
        public static float Detect(float[] x, int start, int window, int sampleRate, float minHz, float maxHz,
            float threshold, float[] scratch, out float clarity)
        {
            clarity = 0f;
            int tauMin = Math.Max(2, (int)(sampleRate / maxHz));
            int tauMax = (int)Math.Ceiling(sampleRate / minHz);
            tauMax = Math.Min(tauMax, Math.Min(scratch.Length - 2, x.Length - start - window - 1));
            if (tauMax <= tauMin + 1) return 0f;

            // Cumulative mean normalized difference d'(tau), stored in scratch.
            scratch[0] = 1f;
            double running = 0;
            for (int tau = 1; tau <= tauMax + 1; tau++)
            {
                double d = 0;
                for (int j = 0; j < window; j++)
                {
                    float delta = x[start + j] - x[start + j + tau];
                    d += delta * delta;
                }
                running += d;
                scratch[tau] = running > 0 ? (float)(d * tau / running) : 1f;
            }

            // First dip under the threshold, then walk down to its local minimum.
            int best = -1;
            for (int tau = tauMin; tau <= tauMax; tau++)
            {
                if (scratch[tau] >= threshold) continue;
                while (tau + 1 <= tauMax && scratch[tau + 1] < scratch[tau]) tau++;
                best = tau;
                break;
            }
            if (best < 0) return 0f;

            // Parabolic interpolation around the minimum for sub-sample accuracy.
            float a = scratch[best - 1], b = scratch[best], c = scratch[best + 1];
            float denom = a - 2f * b + c;
            float shift = Math.Abs(denom) > 1e-9f ? 0.5f * (a - c) / denom : 0f;
            if (shift < -1f || shift > 1f) shift = 0f;
            clarity = 1f - Math.Max(0f, Math.Min(1f, b));
            float hz = sampleRate / (best + shift);
            return hz >= minHz && hz <= maxHz ? hz : 0f;
        }
    }
}
