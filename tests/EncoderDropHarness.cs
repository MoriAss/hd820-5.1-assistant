using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Threading;
using Hd820GameBridge;

internal static class EncoderDropHarness
{
    static void Require(bool okay, string name) { if (!okay) throw new Exception(name); }
    static byte[] Packet(int frames, int tag)
    {
        var samples = new float[frames * 6];
        for (int frame = 0; frame < frames; frame++) for (int channel = 0; channel < 6; channel++)
            samples[frame * 6 + channel] = tag * 100000 + frame * 10 + channel;
        var bytes = new byte[samples.Length * 4];
        Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
        return bytes;
    }
    static void ActiveFixture(RealtimeAc3Encoder encoder)
    {
        typeof(RealtimeAc3Encoder).GetField("_running", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(encoder, true);
    }
    static byte[][] Pending(RealtimeAc3Encoder encoder)
    {
        return encoder.SnapshotQueuedInput();
    }
    static int Main(string[] args)
    {
        if (args.Length != 2) return 2;
        const int cap = 73728; // 64 ms / 3072 complete six-channel frames.
        using (var strict = new RealtimeAc3Encoder(args[0], 640, 64))
        {
            Require(!strict.DropOldInputOnOverflow, "Drop must default off");
            Require(!strict.TryWritePcmBytes(Packet(1, 1), 0, 24), "Stopped write accepted");
            ActiveFixture(strict);
            var huge = Packet(3073, 1);
            Require(!strict.TryWritePcmBytes(huge, 0, huge.Length), "Strict oversized packet accepted");
            var full = Packet(3072, 1);
            Require(strict.TryWritePcmBytes(full, 0, full.Length), "Strict full packet rejected");
            Require(!strict.TryWritePcmBytes(Packet(1, 2), 0, 24), "Strict overflow accepted");
            Require(strict.DroppedInputFrames == 0 && strict.QueuedInputBytes == cap && strict.MaxQueuedInputBytes == cap, "Strict counters changed");
        }
        long fixtureDropped;
        using (var drop = new RealtimeAc3Encoder(args[0], 640, 64))
        {
            int events = 0;
            drop.Error += delegate(string message) { events++; };
            drop.BurstReady += delegate(byte[] block) { events++; };
            drop.DropOldInputOnOverflow = true;
            ActiveFixture(drop);
            int tag = 0;
            foreach (int frames in new int[] { 1000, 1000, 1600 })
            {
                var bytes = Packet(frames, ++tag);
                Require(drop.TryWritePcmBytes(bytes, 0, bytes.Length), "Drop enqueue rejected");
            }
            Require(drop.DroppedInputFrames == 1000 && drop.QueuedInputBytes == 2600 * 24, "Oldest packet accounting");
            byte[][] pending = Pending(drop);
            Require(pending.Length == 2 && pending[0].Length == 1000 * 24 && pending[1].Length == 1600 * 24, "Wrong old packet removed");
            Require(BitConverter.ToSingle(pending[0], 0) == 200000 && BitConverter.ToSingle(pending[1], 0) == 300000, "Did not drop oldest packet");
            var oversized = Packet(4000, 9);
            Require(drop.TryWritePcmBytes(oversized, 0, oversized.Length), "Oversized drop enqueue rejected");
            pending = Pending(drop);
            Require(pending.Length == 1 && pending[0].Length == cap, "Oversized tail retention length");
            Array.Clear(oversized, 0, oversized.Length);
            for (int channel = 0; channel < 6; channel++)
            {
                Require(BitConverter.ToSingle(pending[0], channel * 4) == 900000 + 928 * 10 + channel, "Tail first frame/channel alignment");
                Require(BitConverter.ToSingle(pending[0], cap - 24 + channel * 4) == 900000 + 3999 * 10 + channel, "Tail last frame/channel alignment");
            }
            Require(drop.DroppedInputFrames == 4528 && drop.QueuedInputBytes == cap && drop.MaxQueuedInputBytes == cap, "Oversized accounting");
            bool invalidRejected = false;
            try { drop.TryWritePcmBytes(new byte[25], 0, 25); } catch (ArgumentException) { invalidRejected = true; }
            Require(invalidRejected, "Partial six-channel frame was accepted");
            drop.CompleteInput();
            long oldDropped = drop.DroppedInputFrames;
            Require(!drop.TryWritePcmBytes(oversized, 0, oversized.Length) && drop.DroppedInputFrames == oldDropped, "CompleteInput accepted/dropped new data");
            Require(events == 0, "Queue operation fired an event");
            fixtureDropped = drop.DroppedInputFrames;
        }
        long liveDropped, written, bursts;
        int maximum;
        using (var file = File.Create(args[1]))
        using (var encoder = new RealtimeAc3Encoder(args[0], 640, 64))
        {
            encoder.DropOldInputOnOverflow = true;
            string error = null;
            encoder.Error += delegate(string message) { error = message; };
            encoder.BurstReady += delegate(byte[] burst) { file.Write(burst, 0, burst.Length); };
            encoder.Start();
            // Each 1536-frame tone packet contains an integer number of cycles,
            // so deleting whole packets gives valid, continuous test frequencies.
            double[] frequencies = { 125, 250, 375, 62.5, 500, 625 };
            var tone = new float[1536 * 6];
            for (int frame = 0; frame < 1536; frame++) for (int channel = 0; channel < 6; channel++)
                tone[frame * 6 + channel] = (float)(0.01 * Math.Sin(2 * Math.PI * frequencies[channel] * frame / 48000));
            for (int group = 0; group < 6; group++)
            {
                for (int block = 0; block < 20; block++) encoder.WriteSamples(tone, 0, tone.Length);
                Thread.Sleep(50);
            }
            encoder.CompleteInput();
            Require(encoder.WaitForCompletion(4000), "Live drop stream did not drain");
            Require(error == null && encoder.LastError == null, "Live drop stream fault");
            liveDropped = encoder.DroppedInputFrames;
            written = encoder.InputFramesWritten;
            bursts = encoder.BurstsProduced;
            maximum = encoder.MaxQueuedInputBytes;
            Require(liveDropped > 0 && liveDropped + written == 120 * 1536, "Live accepted/dropped accounting");
            Require(written == bursts * 1536 && maximum <= cap && encoder.InFlightInputFrames == 0, "Live frame/cap/inflight alignment");
            encoder.Stop();
            Require(!encoder.TryWritePcmBytes(new byte[24], 0, 24), "Stopped drop encoder accepted data");
        }
        Console.WriteLine("{\"verification\":\"passed\",\"queueLimitMs\":64,\"capacityBytes\":" + cap +
            ",\"defaultStrictModePreserved\":true,\"dropsOldestPacket\":true,\"oversizedRetainsNewestAlignedTail\":true," +
            "\"fixtureDroppedFrames\":" + fixtureDropped + ",\"partialFrameRejected\":true,\"stopRejectsWithoutCountersChanging\":true," +
            "\"queueOperationsDoNotFireEvents\":true,\"liveDroppedFrames\":" + liveDropped + ",\"liveWrittenFrames\":" + written +
            ",\"liveEncodedBursts\":" + bursts + ",\"liveMaximumQueueBytes\":" + maximum + "}");
        return 0;
    }
}
