using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using Hd820GameBridge;

internal static class BassManagerHarness
{
    static void Require(bool condition, string name) { if (!condition) throw new Exception(name); }
    static string Number(double value) { return value.ToString("R", CultureInfo.InvariantCulture); }
    static float[] Tone(double frequency, int frames, double amplitude)
    {
        var data = new float[frames * 6];
        for (int frame = 0; frame < frames; frame++)
        {
            float sample = (float)(amplitude * Math.Sin(2 * Math.PI * frequency * frame / 48000.0));
            for (int channel = 0; channel < 6; channel++) if (channel != 3) data[frame * 6 + channel] = sample;
        }
        return data;
    }
    static void CheckMains(float[] before, float[] after)
    {
        for (int index = 0; index < before.Length; index++)
            if (index % 6 != 3) Require(BitConverter.ToInt32(BitConverter.GetBytes(before[index]), 0) == BitConverter.ToInt32(BitConverter.GetBytes(after[index]), 0), "Main sample changed");
    }
    static double Rms(float[] samples, int channel, int startFrame, int endFrame)
    {
        double sum = 0;
        for (int frame = startFrame; frame < endFrame; frame++) { double sample = samples[frame * 6 + channel]; sum += sample * sample; }
        return Math.Sqrt(sum / (endFrame - startFrame));
    }
    static int Main(string[] args)
    {
        var frequencies = new List<string>();
        foreach (double frequency in new double[] { 20, 40, 80, 200, 1000 })
        {
            var data = Tone(frequency, 144000, 0.2);
            var before = (float[])data.Clone();
            var bass = new BassManager { Enabled = true, Level = 1 };
            bass.Apply(data);
            CheckMains(before, data);
            double gain = Rms(data, 3, 96000, 144000) / Rms(before, 0, 96000, 144000);
            double expected = 1.0 / Math.Sqrt(1 + Math.Pow(frequency / 80.0, 8));
            Require(Math.Abs(gain / expected - 1) < 0.02, "Butterworth gain " + frequency);
            frequencies.Add("{\"frequencyHz\":" + Number(frequency) + ",\"measuredAmplitudeGain\":" + Number(gain) + ",\"referenceGain\":" + Number(expected) + "}");
        }
        var originalLfe = new float[6000 * 6];
        for (int frame = 0; frame < 6000; frame++) originalLfe[frame * 6 + 3] = (float)(0.37 * Math.Sin(frame * 0.031));
        originalLfe[3] = BitConverter.ToSingle(BitConverter.GetBytes(unchecked((int)0x80000000)), 0);
        var originalCopy = (float[])originalLfe.Clone();
        new BassManager { Enabled = true, Level = 1 }.Apply(originalLfe);
        for (int i = 0; i < originalLfe.Length; i++) Require(BitConverter.ToInt32(BitConverter.GetBytes(originalLfe[i]), 0) == BitConverter.ToInt32(BitConverter.GetBytes(originalCopy[i]), 0), "Native LFE not preserved");
        var singleMain = Tone(40, 144000, 0.2);
        for (int i = 0; i < singleMain.Length; i++) if (i % 6 != 0) singleMain[i] = 0;
        double sourceRms = Rms(singleMain, 0, 96000, 144000);
        new BassManager { Enabled = true, Level = 0.5f }.Apply(singleMain);
        double singleMainGain = Rms(singleMain, 3, 96000, 144000) / sourceRms;
        Require(Math.Abs(singleMainGain / 0.09980525784828885 - 1) < 0.001, "Single main 0.2 average / level scale");
        var random = new Random(4411);
        var packetData = new float[9600 * 6];
        for (int i = 0; i < packetData.Length; i++) packetData[i] = (float)(random.NextDouble() * 0.2 - 0.1);
        var full = (float[])packetData.Clone();
        var fullDsp = new BassManager { Enabled = true, Level = 0.5f };
        fullDsp.Apply(full);
        var split = (float[])packetData.Clone();
        var splitDsp = new BassManager { Enabled = true, Level = 0.5f };
        int position = 0;
        int[] packetSizes = { 1, 79, 480, 13, 1536, 160, 1024, 3 };
        int packet = 0;
        while (position < 9600)
        {
            int count = Math.Min(packetSizes[packet++ % packetSizes.Length], 9600 - position);
            var part = new float[count * 6];
            Array.Copy(split, position * 6, part, 0, part.Length);
            splitDsp.Apply(part);
            Array.Copy(part, 0, split, position * 6, part.Length);
            position += count;
        }
        for (int i = 0; i < full.Length; i++) Require(BitConverter.ToInt32(BitConverter.GetBytes(full[i]), 0) == BitConverter.ToInt32(BitConverter.GetBytes(split[i]), 0), "Packet boundary changed DSP output");
        // Warm filters under disabled mode, then verify the requested 50 ms ramp.
        var smooth = new BassManager { Level = 0.5f };
        var warm = new float[48000 * 6];
        for (int i = 0; i < warm.Length; i++) if (i % 6 != 3) warm[i] = 0.2f;
        smooth.Apply(warm);
        smooth.Enabled = true;
        var ramp = (float[])warm.Clone();
        smooth.Apply(ramp);
        double firstStep = ramp[3];
        double at50ms = ramp[2399 * 6 + 3];
        Require(Math.Abs(at50ms - 0.1 * (1 - Math.Exp(-1))) < 1e-6, "50 ms smoothing time constant");
        Require(firstStep < 0.00005, "Enable discontinuity");
        smooth.Level = 1;
        var levelRamp = (float[])warm.Clone();
        double previous = ramp[ramp.Length - 3];
        smooth.Apply(levelRamp);
        Require(Math.Abs(levelRamp[3] - previous) < 0.00005, "Level change discontinuity");
        smooth.Enabled = false;
        var stopRamp = (float[])warm.Clone();
        previous = levelRamp[levelRamp.Length - 3];
        smooth.Apply(stopRamp);
        Require(Math.Abs(stopRamp[3] - previous) < 0.0001, "Disable discontinuity");
        Require(smooth.SmoothedLevel == 0, "Disable did not settle to exact zero");
        var transparent = (float[])packetData.Clone();
        transparent[3] = BitConverter.ToSingle(BitConverter.GetBytes(unchecked((int)0x80000000)), 0);
        var transparentBefore = (float[])transparent.Clone();
        smooth.Apply(transparent);
        for (int i = 0; i < transparent.Length; i++) Require(BitConverter.ToInt32(BitConverter.GetBytes(transparent[i]), 0) == BitConverter.ToInt32(BitConverter.GetBytes(transparentBefore[i]), 0), "Disabled mode is not bit transparent");
        var clipDsp = new BassManager { Enabled = true, Level = 1 };
        var extreme = new float[48000 * 6];
        for (int i = 0; i < extreme.Length; i++) extreme[i] = i % 6 == 3 ? 0.9f : float.MaxValue;
        clipDsp.Apply(extreme);
        Require(clipDsp.ClippedSamples > 0, "Clipping was not counted");
        for (int i = 3; i < extreme.Length; i += 6) Require(!float.IsNaN(extreme[i]) && !float.IsInfinity(extreme[i]) && Math.Abs(extreme[i]) <= 1, "Extreme LFE output invalid");
        var invalid = new float[480 * 6];
        for (int i = 0; i < invalid.Length; i++) invalid[i] = i % 2 == 0 ? float.NaN : float.PositiveInfinity;
        clipDsp.Apply(invalid);
        for (int i = 3; i < invalid.Length; i += 6) Require(!float.IsNaN(invalid[i]) && !float.IsInfinity(invalid[i]) && Math.Abs(invalid[i]) <= 1, "Nonfinite input produced invalid LFE");
        Require(clipDsp.InvalidSamples > 0, "Invalid samples not counted");
        // Real encoder roundtrip test fixture: each main has a 60 Hz bass tone
        // plus its own midrange tone. Native LFE contains a separate 30 Hz tone.
        if (args.Length == 2)
        {
            double[] mids = { 401, 503, 607, 0, 809, 907 };
            var roundDsp = new BassManager { Enabled = true, Level = 0.5f };
            using (var output = File.Create(args[1]))
            using (var encoder = new RealtimeAc3Encoder(args[0]))
            {
                string error = null;
                encoder.BurstReady += delegate(byte[] burst) { output.Write(burst, 0, burst.Length); };
                encoder.Error += delegate(string message) { error = message; };
                encoder.Start();
                for (int block = 0; block < 300; block++)
                {
                    var input = new float[480 * 6];
                    for (int frame = 0; frame < 480; frame++)
                    {
                        int index = block * 480 + frame;
                        double time = index / 48000.0;
                        for (int channel = 0; channel < 6; channel++) input[frame * 6 + channel] = (float)(channel == 3
                            ? 0.03 * Math.Sin(2 * Math.PI * 30 * time)
                            : 0.10 * Math.Sin(2 * Math.PI * 60 * time) + 0.04 * Math.Sin(2 * Math.PI * mids[channel] * time));
                    }
                    var pre = (float[])input.Clone();
                    roundDsp.Apply(input);
                    CheckMains(pre, input);
                    encoder.WriteSamples(input, 0, input.Length);
                    Thread.Sleep(10);
                }
                encoder.CompleteInput();
                Require(encoder.WaitForCompletion(4000) && error == null, "Bass AC3 roundtrip encode failed");
            }
        }
        Console.WriteLine("{\"verification\":\"passed\",\"frequencyResponse\":[" + String.Join(",", frequencies.ToArray()) +
            "],\"mainChannelsBitIdentical\":true,\"nativeLfePreserved\":true,\"singleMain40HzLevelHalfGain\":" + Number(singleMainGain) + ",\"packetBoundaryBitIdentical\":true," +
            "\"smoothing50msLfeAtTimeConstant\":" + Number(at50ms) + ",\"enableFirstSampleStep\":" + Number(firstStep) +
            ",\"disabledSettledBitTransparent\":true,\"extremeLfeFiniteAndBounded\":true,\"clippingCount\":" + clipDsp.ClippedSamples +
            ",\"invalidInputCount\":" + clipDsp.InvalidSamples + "}");
        return 0;
    }
}
