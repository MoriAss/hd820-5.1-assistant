using System;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using Hd820GameBridge;

internal static class EncoderLifecycleHarness
{
    private static int LiveWorkers(RealtimeAc3Encoder encoder)
    {
        int count = 0;
        foreach (string name in new[] { "_writerThread", "_readerThread", "_stderrThread" })
        {
            var worker = (Thread)typeof(RealtimeAc3Encoder).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(encoder);
            if (worker != null && worker.IsAlive) count++;
        }
        return count;
    }

    private static int Main(string[] args)
    {
        if (args.Length != 1) return 2;
        long maximumStopMs = 0;
        for (int run = 0; run < 3; run++)
        {
            using (var encoder = new RealtimeAc3Encoder(args[0]))
            {
                encoder.Start();
                for (int block = 0; block < 30; block++)
                {
                    encoder.WriteSamples(new float[480 * 6], 0, 480 * 6);
                    Thread.Sleep(10);
                }
                var watch = Stopwatch.StartNew();
                encoder.Stop();
                maximumStopMs = Math.Max(maximumStopMs, watch.ElapsedMilliseconds);
                if (LiveWorkers(encoder) != 0 || encoder.IsRunning || encoder.LastError != null || encoder.BurstsProduced < 5) return 3;
                if (encoder.TryWritePcmBytes(new byte[24], 0, 24)) return 4;
            }
        }
        int faultEvents = 0;
        using (var encoder = new RealtimeAc3Encoder(args[0], 640, 32))
        {
            encoder.Error += delegate(string error) { Interlocked.Increment(ref faultEvents); };
            encoder.Start();
            if (encoder.TryWritePcmBytes(new byte[36888], 0, 36888)) return 5;
            var process = (Process)typeof(RealtimeAc3Encoder).GetField("_process", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(encoder);
            process.Kill();
            process.WaitForExit(1500);
            for (int wait = 0; wait < 100 && faultEvents == 0; wait++) Thread.Sleep(10);
            encoder.Stop();
            if (faultEvents == 0 || LiveWorkers(encoder) != 0 || encoder.IsRunning) return 6;
        }
        bool timeoutPreservedReferences = false;
        using (var encoder = new RealtimeAc3Encoder(args[0]))
        using (var blockedWorkerGate = new ManualResetEvent(false))
        {
            encoder.Start();
            encoder.Stop();
            // Inject a deliberately blocked worker after the real workers stop.
            // This tests the timeout path without touching audio hardware.
            var blockedWorker = new Thread(delegate() { blockedWorkerGate.WaitOne(); });
            blockedWorker.IsBackground = true;
            var writerField = typeof(RealtimeAc3Encoder).GetField("_writerThread", BindingFlags.Instance | BindingFlags.NonPublic);
            var disposedField = typeof(RealtimeAc3Encoder).GetField("_disposed", BindingFlags.Instance | BindingFlags.NonPublic);
            writerField.SetValue(encoder, blockedWorker);
            blockedWorker.Start();
            try { encoder.Dispose(); return 7; }
            catch (TimeoutException)
            {
                timeoutPreservedReferences = !(bool)disposedField.GetValue(encoder) && writerField.GetValue(encoder) == blockedWorker && blockedWorker.IsAlive;
            }
            finally { blockedWorkerGate.Set(); blockedWorker.Join(1000); }
            if (!timeoutPreservedReferences) return 8;
            encoder.Dispose();
        }
        Console.WriteLine("{\"normalStartStopRuns\":3,\"activeWorkersAfterStop\":0,\"maximumStopElapsedMs\":" + maximumStopMs +
            ",\"writesAfterStopRejected\":true,\"oversizedQueueInputRejected\":true,\"unexpectedProcessExitErrorEvents\":" + faultEvents +
            ",\"stopTimeoutThrowsAndPreservesWorkerReferences\":" + timeoutPreservedReferences.ToString().ToLowerInvariant() +
            ",\"verification\":\"passed\"}");
        return 0;
    }
}
