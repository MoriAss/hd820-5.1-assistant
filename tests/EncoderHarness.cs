using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Hd820GameBridge;

internal static class EncoderHarness
{
    private static int Main(string[] args)
    {
        if (args.Length < 2) return 2;
        var times = new System.Collections.Generic.List<long>();
        string error = null;
        using (var output = File.Create(args[1]))
        using (var encoder = new RealtimeAc3Encoder(args[0]))
        {
            encoder.BurstReady += delegate(byte[] burst)
            {
                output.Write(burst, 0, burst.Length);
                times.Add(encoder.FirstBurstElapsedMs);
            };
            encoder.Error += delegate(string message) { error = message; };
            encoder.Start();
            var pace = Stopwatch.StartNew();
            double[] frequencies = { 401, 503, 607, 61, 701, 809 };
            const int blockFrames = 480;
            for (int block = 0; block < 600; block++)
            {
                var samples = new float[blockFrames * 6];
                for (int frame = 0; frame < blockFrames; frame++)
                {
                    int position = block * blockFrames + frame;
                    int channel = position / 48000;
                    samples[frame * 6 + channel] = (float)(0.15 * Math.Sin(2 * Math.PI * frequencies[channel] * position / 48000.0));
                }
                encoder.WriteSamples(samples, 0, samples.Length);
                long wait = (block + 1) * 10 - pace.ElapsedMilliseconds;
                if (wait > 0) Thread.Sleep((int)wait);
            }
            encoder.CompleteInput();
            bool completed = encoder.WaitForCompletion(4000);
            Console.WriteLine("{\"inputFrames\":" + encoder.InputFramesWritten +
                ",\"bursts\":" + encoder.BurstsProduced + ",\"burstBytes\":6144" +
                ",\"firstBurstElapsedMs\":" + encoder.FirstBurstElapsedMs +
                ",\"wallElapsedMs\":" + pace.ElapsedMilliseconds +
                ",\"completed\":" + completed.ToString().ToLowerInvariant() +
                ",\"error\":\"" + Escape(error ?? encoder.LastError ?? "") +
                "\",\"stderr\":\"" + Escape(encoder.StderrText) + "\"}");
            return completed && error == null && encoder.LastError == null && encoder.BurstsProduced > 0 ? 0 : 1;
        }
    }

    private static string Escape(string value)
    {
        return value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n");
    }
}
