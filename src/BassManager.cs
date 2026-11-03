using System;
using System.Threading;

namespace Hd820GameBridge
{
    /// <summary>
    /// Adds an 80 Hz fourth-order low-passed equal-weight average of the five
    /// mains to existing LFE. Input is mapped, gain-applied, interleaved
    /// 48 kHz float PCM: FL, FR, FC, LFE, SL, SR. Main samples are never written.
    /// Apply and Reset belong to one audio thread; Enabled/Level may be updated
    /// from another thread. A new stream must use a new instance or Reset.
    /// </summary>
    public sealed class BassManager
    {
        public volatile bool Enabled = false;
        public volatile float Level = 0.5f;

        private readonly LowPassSection _first = new LowPassSection(0.5411961);
        private readonly LowPassSection _second = new LowPassSection(1.30656296);
        private readonly double _smoothingStep = 1.0 - Math.Exp(-1.0 / (0.050 * 48000.0));
        private double _smoothedLevel;
        private long _mixedFrames;
        private long _clippedSamples;
        private long _invalidSamples;
        private double _lastAddedBassPeak;
        private double _lastOutputLfePeak;
        private double _addedBassPeak;
        private double _outputLfePeak;

        public long MixedFrames { get { return Interlocked.Read(ref _mixedFrames); } }
        public long ClippedSamples { get { return Interlocked.Read(ref _clippedSamples); } }
        public long InvalidSamples { get { return Interlocked.Read(ref _invalidSamples); } }
        public double LastAddedBassPeak { get { return Interlocked.CompareExchange(ref _lastAddedBassPeak, 0, 0); } }
        public double LastOutputLfePeak { get { return Interlocked.CompareExchange(ref _lastOutputLfePeak, 0, 0); } }
        public double AddedBassPeak { get { return Interlocked.CompareExchange(ref _addedBassPeak, 0, 0); } }
        public double OutputLfePeak { get { return Interlocked.CompareExchange(ref _outputLfePeak, 0, 0); } }
        public double SmoothedLevel { get { return Interlocked.CompareExchange(ref _smoothedLevel, 0, 0); } }

        public void Reset()
        {
            _first.Reset();
            _second.Reset();
            _smoothedLevel = 0;
            Interlocked.Exchange(ref _mixedFrames, 0);
            Interlocked.Exchange(ref _clippedSamples, 0);
            Interlocked.Exchange(ref _invalidSamples, 0);
            Interlocked.Exchange(ref _lastAddedBassPeak, 0);
            Interlocked.Exchange(ref _lastOutputLfePeak, 0);
            Interlocked.Exchange(ref _addedBassPeak, 0);
            Interlocked.Exchange(ref _outputLfePeak, 0);
        }

        public void Apply(float[] samples)
        {
            if (samples == null) throw new ArgumentNullException("samples");
            Apply(samples, samples.Length);
        }

        public void Apply(float[] samples, int sampleCount)
        {
            if (samples == null) throw new ArgumentNullException("samples");
            if (sampleCount < 0 || sampleCount > samples.Length || sampleCount % 6 != 0)
                throw new ArgumentException("Bass management requires complete six-channel frames.", "samples");
            float requested = Level;
            double target = Enabled && !float.IsNaN(requested) && !float.IsInfinity(requested)
                ? Math.Max(0.0, Math.Min(1.0, requested)) : 0.0;
            double level = _smoothedLevel;
            double addedPeak = 0, outputPeak = 0;
            long mixed = 0, clipped = 0, invalid = 0;

            for (int at = 0; at < sampleCount; at += 6)
            {
                double mainAverage = 0;
                for (int channel = 0; channel < 6; channel++)
                {
                    if (channel == 3) continue;
                    float main = samples[at + channel];
                    if (float.IsNaN(main) || float.IsInfinity(main)) { invalid++; continue; }
                    mainAverage += main * 0.2;
                }
                // Keep state current even while disabled, preventing stale-filter
                // transients when enabling during an already running game.
                double lowBass = _second.Process(_first.Process(mainAverage));
                level += (target - level) * _smoothingStep;
                if (target == 0.0 && level <= 1e-7) level = 0.0;
                float nativeLfe = samples[at + 3];
                if (level == 0.0)
                {
                    // Do not assign: settled disabled mode preserves every bit,
                    // including signed zero and any native LFE representation.
                    if (!float.IsNaN(nativeLfe) && !float.IsInfinity(nativeLfe))
                        outputPeak = Math.Max(outputPeak, Math.Abs((double)nativeLfe));
                    continue;
                }
                double bass = lowBass * level;
                if (Double.IsNaN(bass) || Double.IsInfinity(bass))
                {
                    invalid++;
                    bass = 0;
                    _first.Reset();
                    _second.Reset();
                }
                double original = nativeLfe;
                if (float.IsNaN(nativeLfe) || float.IsInfinity(nativeLfe)) { invalid++; original = 0; }
                double combined = original + bass;
                if (combined > 1.0) { combined = 1.0; clipped++; }
                else if (combined < -1.0) { combined = -1.0; clipped++; }
                samples[at + 3] = bass == 0.0 && original == nativeLfe && nativeLfe >= -1f && nativeLfe <= 1f
                    ? nativeLfe : (float)combined;
                mixed++;
                addedPeak = Math.Max(addedPeak, Math.Abs(bass));
                outputPeak = Math.Max(outputPeak, Math.Abs(combined));
            }
            Interlocked.Exchange(ref _smoothedLevel, level);
            Interlocked.Add(ref _mixedFrames, mixed);
            Interlocked.Add(ref _clippedSamples, clipped);
            Interlocked.Add(ref _invalidSamples, invalid);
            Interlocked.Exchange(ref _lastAddedBassPeak, addedPeak);
            Interlocked.Exchange(ref _lastOutputLfePeak, outputPeak);
            Interlocked.Exchange(ref _addedBassPeak, Math.Max(AddedBassPeak, addedPeak));
            Interlocked.Exchange(ref _outputLfePeak, Math.Max(OutputLfePeak, outputPeak));
        }

        private sealed class LowPassSection
        {
            private readonly double _b0, _b1, _b2, _a1, _a2;
            private double _z1, _z2;

            public LowPassSection(double q)
            {
                double omega = 2.0 * Math.PI * 80.0 / 48000.0;
                double cosine = Math.Cos(omega);
                double alpha = Math.Sin(omega) / (2.0 * q);
                double a0 = 1.0 + alpha;
                _b0 = (1.0 - cosine) / (2.0 * a0);
                _b1 = (1.0 - cosine) / a0;
                _b2 = _b0;
                _a1 = -2.0 * cosine / a0;
                _a2 = (1.0 - alpha) / a0;
            }

            public double Process(double value)
            {
                // Long digital silence otherwise leaves subnormal feedback
                // values, which can make the same audio work tens of times
                // slower on CPUs without flush-to-zero enabled for this thread.
                // 1e-30 is below -600 dBFS and cannot retain audible information.
                double output = FlushTiny(_b0 * value + _z1);
                _z1 = FlushTiny(_b1 * value - _a1 * output + _z2);
                _z2 = FlushTiny(_b2 * value - _a2 * output);
                return output;
            }

            private static double FlushTiny(double value) { return Math.Abs(value) < 1e-30 ? 0.0 : value; }

            public void Reset() { _z1 = _z2 = 0.0; }
        }
    }
}
