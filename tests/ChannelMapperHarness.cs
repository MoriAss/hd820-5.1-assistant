using System;

namespace Hd820GameBridge
{
    internal static class ChannelMapperHarness
    {
        static int passed;
        static void Assert(bool condition, string label)
        {
            if (!condition) throw new InvalidOperationException("FAILED: " + label);
            passed++;
        }
        static byte[] Pcm(float[] values)
        {
            byte[] bytes = new byte[values.Length * 4];
            Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
            return bytes;
        }
        static bool Near(float x, float y) { return Math.Abs(x - y) < 0.00001f; }
        static void Identity(uint mask)
        {
            float[] input = new float[36];
            for (int frame = 0; frame < 6; frame++) input[frame * 6 + frame] = 0.25f;
            float[] actual = ChannelMapper.ConvertPacket(Pcm(input), 6, AudioFormat.Float(6, mask), 1f);
            for (int i = 0; i < input.Length; i++) Assert(Near(input[i], actual[i]), "6-channel identity mask=" + mask + " sample=" + i);
        }
        static int Main()
        {
            Identity(0x3F);
            Identity(0x60F);
            float[] seven = new float[64];
            for (int frame = 0; frame < 8; frame++) seven[frame * 8 + frame] = 0.25f;
            float[] mapped = ChannelMapper.ConvertPacket(Pcm(seven), 8, AudioFormat.Float(8, 0x63F), 1f);
            for (int frame = 0; frame < 8; frame++)
            {
                int destination = frame < 6 ? frame : frame - 2;
                float expected = frame < 4 ? 0.25f : 0.25f * 0.5f;
                for (int channel = 0; channel < 6; channel++)
                    Assert(Near(mapped[frame * 6 + channel], channel == destination ? expected : 0f), "7.1 impulse frame=" + frame + " channel=" + channel);
            }
            bool rejected = false;
            try { ChannelMapper.ConvertPacket(new byte[24], 1, AudioFormat.Float(6, 0), 1); }
            catch (InvalidOperationException) { rejected = true; }
            Assert(rejected, "empty channel mask rejected");
            bool extraRejected = false;
            try { ChannelMapper.ConvertPacket(new byte[24], 1, AudioFormat.Float(6, 0x63F), 1); }
            catch (InvalidOperationException) { extraRejected = true; }
            Console.WriteLine("PASS assertions=" + passed);
            Console.WriteLine("EXTRA_MASK_BITS_REJECTED=" + extraRejected);
            return extraRejected ? 0 : 2;
        }
    }
}
