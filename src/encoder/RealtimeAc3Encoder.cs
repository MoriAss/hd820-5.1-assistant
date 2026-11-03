using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace Hd820GameBridge
{
    /// <summary>
    /// Encodes 48 kHz interleaved float PCM in Windows 5.1 order
    /// (FL, FR, FC, LFE, BL, BR) to little-endian IEC 61937 AC-3 bursts.
    /// This class does not open audio hardware or change Windows settings.
    /// BurstReady runs on the output reader thread; handlers must not block.
    /// </summary>
    public sealed class RealtimeAc3Encoder : IDisposable
    {
        public const int Channels = 6;
        public const int SampleRate = 48000;
        public const int FramesPerBurst = 1536;
        public const int BurstBytes = 6144;
        public const int BytesPerInputFrame = 24;
        private static readonly int[] Ac3BitratesKbps = {32,40,48,56,64,80,96,112,128,160,192,224,256,320,384,448,512,576,640};

        private readonly string _ffmpegPath;
        private readonly int _bitrateKbps;
        private readonly object _queueLock = new object();
        private readonly object _errorLock = new object();
        private readonly object _lifecycleLock = new object();
        private readonly object _stopLock = new object();
        private readonly Queue<byte[]> _inputQueue = new Queue<byte[]>();
        private readonly StringBuilder _stderr = new StringBuilder();
        private readonly int _maxQueuedBytes;
        private Process _process;
        private Thread _writerThread;
        private Thread _readerThread;
        private Thread _stderrThread;
        private Stopwatch _elapsed;
        private int _queuedBytes;
        private volatile bool _inputComplete;
        private volatile bool _running;
        private volatile bool _stopping;
        private bool _disposed;
        private long _inputSamplesWritten;
        private long _burstsProduced;
        private long _firstBurstElapsedMs = -1;
        private long _droppedInputFrames;
        private long _inFlightInputFrames;
        private int _maxQueuedInputBytes;
        private string _lastError;

        // Optional FPS behavior. Default keeps the original strict overflow
        // contract. Only not-yet-written PCM may be discarded.
        public volatile bool DropOldInputOnOverflow = false;

        public event Action<byte[]> BurstReady;
        public event Action<string> Error;

        public RealtimeAc3Encoder(string ffmpegPath, int bitrateKbps = 640)
            : this(ffmpegPath, bitrateKbps, 500) { }

        public RealtimeAc3Encoder(string ffmpegPath, int bitrateKbps, int maximumQueueMilliseconds)
        {
            if (String.IsNullOrWhiteSpace(ffmpegPath) || !File.Exists(ffmpegPath))
                throw new FileNotFoundException("FFmpeg executable was not found.", ffmpegPath);
            if (bitrateKbps != 384 && bitrateKbps != 448 && bitrateKbps != 640)
                throw new ArgumentOutOfRangeException("bitrateKbps", "Use 384, 448, or 640 kbps.");
            if (maximumQueueMilliseconds < 32 || maximumQueueMilliseconds > 2000)
                throw new ArgumentOutOfRangeException("maximumQueueMilliseconds");
            _ffmpegPath = Path.GetFullPath(ffmpegPath);
            _bitrateKbps = bitrateKbps;
            _maxQueuedBytes = SampleRate * BytesPerInputFrame * maximumQueueMilliseconds / 1000;
        }

        public bool IsRunning { get { return _running; } }
        public long InputSamplesWritten { get { return Interlocked.Read(ref _inputSamplesWritten); } }
        public long InputFramesWritten { get { return InputSamplesWritten / Channels; } }
        public long BurstsProduced { get { return Interlocked.Read(ref _burstsProduced); } }
        public long FirstBurstElapsedMs { get { return Interlocked.Read(ref _firstBurstElapsedMs); } }
        public int QueuedInputBytes { get { lock (_queueLock) return _queuedBytes; } }
        public long DroppedInputFrames { get { return Interlocked.Read(ref _droppedInputFrames); } }
        public long InFlightInputFrames { get { return Interlocked.Read(ref _inFlightInputFrames); } }
        public int MaxQueuedInputBytes { get { return Interlocked.CompareExchange(ref _maxQueuedInputBytes, 0, 0); } }
        public string LastError { get { lock (_errorLock) return _lastError; } }
        public string StderrText { get { lock (_errorLock) return _stderr.ToString(); } }

        public void Start()
        {
            lock (_lifecycleLock) StartCore();
        }

        private void StartCore()
        {
            if (_disposed) throw new ObjectDisposedException("RealtimeAc3Encoder");
            if (_stopping) throw new InvalidOperationException("Create a new encoder instance after Stop.");
            if (_process != null) throw new InvalidOperationException("Create a new encoder instance for each run.");
            var info = new ProcessStartInfo();
            info.FileName = _ffmpegPath;
            // Small probing and bounded pipe reads avoid FFmpeg's normal multi-second startup buffering.
            // Explicit layout is essential: six interleaved samples alone do not identify channel order.
            info.Arguments = "-hide_banner -loglevel warning -nostats -probesize 32 -analyzeduration 0 " +
                "-f f32le -ar 48000 -ac 6 -channel_layout 5.1 -blocksize 36864 -i pipe:0 " +
                "-map 0:a:0 -c:a ac3 -b:a " + _bitrateKbps.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                "k -ar 48000 -ac 6 -flush_packets 1 -f spdif pipe:1";
            info.UseShellExecute = false;
            info.CreateNoWindow = true;
            info.RedirectStandardInput = true;
            info.RedirectStandardOutput = true;
            info.RedirectStandardError = true;
            _process = new Process();
            _process.StartInfo = info;
            _process.EnableRaisingEvents = true;
            _process.Exited += delegate
            {
                try
                {
                    if (!_stopping && _process.ExitCode != 0)
                        ReportError("FFmpeg exited unexpectedly with code " + _process.ExitCode + ". " + StderrText);
                }
                catch (InvalidOperationException) { }
            };
            _elapsed = Stopwatch.StartNew();
            try
            {
                if (!_process.Start()) throw new InvalidOperationException("Could not start FFmpeg.");
                _running = true;
                _writerThread = StartWorker(WriteInput, "HD820 AC3 input",true);
                _readerThread = StartWorker(ReadBursts, "HD820 AC3 output",true);
                _stderrThread = StartWorker(ReadStderr, "HD820 AC3 diagnostic",false);
            }
            catch
            {
                _running = false;
                _stopping = true;
                CompleteInput();
                // A late Thread.Start/pipe failure must not orphan a process
                // that was already created. Retain resources if a callback is
                // still running so Stop/Dispose can safely be retried.
                bool exited = false;
                try { if (_process != null) { if (!_process.HasExited) _process.Kill(); exited = _process.WaitForExit(1000); } }
                catch (InvalidOperationException) { exited = true; }
                bool writer = JoinWorker(_writerThread, 1000);
                bool reader = JoinWorker(_readerThread, 1000);
                bool diagnostic = JoinWorker(_stderrThread, 1000);
                if (exited && writer && reader && diagnostic && _process != null) _process.Dispose();
                throw;
            }
        }

        private static Thread StartWorker(ThreadStart entry, string name,bool audio)
        {
            var worker = new Thread(delegate(){
                if(audio){using(var scheduler=new AudioScheduler())entry();}
                else entry();
            });
            worker.IsBackground = true;
            worker.Name = name;
            if(audio)worker.Priority=ThreadPriority.AboveNormal;
            worker.Start();
            return worker;
        }

        public void WriteSamples(float[] samples, int offset, int count)
        {
            if (samples == null) throw new ArgumentNullException("samples");
            if (offset < 0 || count < 0 || offset > samples.Length - count)
                throw new ArgumentOutOfRangeException("count");
            if (count % Channels != 0) throw new ArgumentException("Sample count must contain complete six-channel frames.");
            var pcm = new byte[checked(count * sizeof(float))];
            Buffer.BlockCopy(samples, checked(offset * sizeof(float)), pcm, 0, pcm.Length);
            if (!EnqueueOwnedBuffer(pcm)) throw new InvalidOperationException("Encoder stopped or input queue exceeded its latency limit.");
        }

        public void WritePcmBytes(byte[] bytes, int offset, int count)
        {
            if (!TryWritePcmBytes(bytes, offset, count))
                throw new InvalidOperationException("Encoder stopped or input queue exceeded its latency limit.");
        }

        public bool TryWritePcmBytes(byte[] bytes, int offset, int count)
        {
            if (bytes == null) throw new ArgumentNullException("bytes");
            if (offset < 0 || count < 0 || offset > bytes.Length - count)
                throw new ArgumentOutOfRangeException("count");
            if (count % BytesPerInputFrame != 0)
                throw new ArgumentException("Byte count must contain complete float six-channel frames.");
            if (count == 0) return _running && !_inputComplete && !_stopping;
            var copy = new byte[count];
            Buffer.BlockCopy(bytes, offset, copy, 0, count);
            return EnqueueOwnedBuffer(copy);
        }

        // The bridge has just allocated this PCM block and never mutates it
        // after transfer. Avoid an extra full PCM allocation/copy per packet;
        // the public API above retains defensive-copy semantics for callers.
        internal bool TryWriteOwnedPcmBytes(byte[] bytes)
        {
            if (bytes == null) throw new ArgumentNullException("bytes");
            if (bytes.Length % BytesPerInputFrame != 0)
                throw new ArgumentException("Byte count must contain complete float six-channel frames.");
            return EnqueueOwnedBuffer(bytes);
        }

        public static void ValidateBurst(byte[] burst)
        {
            if (burst == null || burst.Length != BurstBytes || burst[0] != 0x72 || burst[1] != 0xF8 ||
                burst[2] != 0x1F || burst[3] != 0x4E || (burst[4] & 31) != 1 || (burst[5] & 0x80) != 0)
                throw new InvalidDataException("Invalid IEC 61937 AC-3 carrier header.");
            int payloadBits = burst[6] | (burst[7] << 8);
            if (payloadBits < 56 || payloadBits > (BurstBytes - 8) * 8 || payloadBits % 16 != 0 ||
                burst[8] != 0x77 || burst[9] != 0x0B)
                throw new InvalidDataException("Invalid AC-3 payload length or byte-swapped sync word.");
            // IEC 61937 words are byte-swapped. Original AC-3 header byte 4
            // (fscod/frmsizecod) is at carrier byte 13; bsid is at byte 12.
            int header = burst[13], sizeCode = header & 63;
            if ((header >> 6) != 0 || sizeCode > 37 || (burst[12] >> 3) > 8)
                throw new InvalidDataException("This bridge requires a complete 48 kHz AC-3 frame.");
            int payloadBytes = Ac3BitratesKbps[sizeCode >> 1] * 4;
            if (payloadBits != payloadBytes * 8)
                throw new InvalidDataException("AC-3 frame header and IEC payload size disagree.");
            for (int i = 8 + payloadBytes; i < BurstBytes; i++)
                if (burst[i] != 0) throw new InvalidDataException("Nonzero data after the AC-3 carrier payload.");
        }

        private bool EnqueueOwnedBuffer(byte[] copy)
        {
            lock (_queueLock)
            {
                if (!_running || _inputComplete || _stopping) return false;
                if (copy.Length == 0) return true;
                if (DropOldInputOnOverflow)
                {
                    if (copy.Length > _maxQueuedBytes)
                    {
                        // All lengths and offsets are complete 24-byte PCM frames.
                        // Retain the newest part of a large packet, never shift
                        // the six-channel interleave by dropping arbitrary bytes.
                        int retainedBytes = _maxQueuedBytes - _maxQueuedBytes % BytesPerInputFrame;
                        var newest = new byte[retainedBytes];
                        Buffer.BlockCopy(copy, copy.Length - retainedBytes, newest, 0, retainedBytes);
                        Interlocked.Add(ref _droppedInputFrames, (copy.Length - retainedBytes) / BytesPerInputFrame);
                        copy = newest;
                    }
                    while (_queuedBytes > _maxQueuedBytes - copy.Length && _inputQueue.Count > 0)
                    {
                        byte[] discarded = _inputQueue.Dequeue();
                        _queuedBytes -= discarded.Length;
                        Interlocked.Add(ref _droppedInputFrames, discarded.Length / BytesPerInputFrame);
                    }
                }
                if (_queuedBytes > _maxQueuedBytes - copy.Length) return false;
                _inputQueue.Enqueue(copy);
                _queuedBytes += copy.Length;
                if (_queuedBytes > _maxQueuedInputBytes)
                    Interlocked.Exchange(ref _maxQueuedInputBytes, _queuedBytes);
                Monitor.PulseAll(_queueLock);
                return true;
            }
        }

        public void CompleteInput()
        {
            lock (_queueLock)
            {
                _inputComplete = true;
                Monitor.PulseAll(_queueLock);
            }
        }

        private void WriteInput()
        {
            try
            {
                Stream input = _process.StandardInput.BaseStream;
                while (true)
                {
                    byte[] pcm;
                    lock (_queueLock)
                    {
                        while (_inputQueue.Count == 0 && !_inputComplete)
                            Monitor.Wait(_queueLock);
                        if (_inputQueue.Count == 0 && _inputComplete) break;
                        pcm = _inputQueue.Dequeue();
                        _queuedBytes -= pcm.Length;
                        Interlocked.Exchange(ref _inFlightInputFrames, pcm.Length / BytesPerInputFrame);
                    }
                    input.Write(pcm, 0, pcm.Length);
                    input.Flush();
                    Interlocked.Add(ref _inputSamplesWritten, pcm.Length / sizeof(float));
                    Interlocked.Exchange(ref _inFlightInputFrames, 0);
                }
            }
            catch (Exception ex)
            {
                if (!_stopping) ReportError("FFmpeg input failed: " + ex.Message);
            }
            finally
            {
                Interlocked.Exchange(ref _inFlightInputFrames, 0);
                try { _process.StandardInput.Close(); } catch { }
            }
        }

        private void ReadBursts()
        {
            try
            {
                Stream output = _process.StandardOutput.BaseStream;
                while (true)
                {
                    var burst = new byte[BurstBytes];
                    int used = 0;
                    while (used < burst.Length)
                    {
                        int count = output.Read(burst, used, burst.Length - used);
                        if (count == 0) break;
                        used += count;
                    }
                    if (used == 0)
                    {
                        if (!_stopping && !_inputComplete)
                            throw new EndOfStreamException("FFmpeg closed its output before input was completed.");
                        break;
                    }
                    if (used != BurstBytes) throw new InvalidDataException("Truncated IEC 61937 burst: " + used + " bytes.");
                    ValidateBurst(burst);
                    Interlocked.CompareExchange(ref _firstBurstElapsedMs, _elapsed.ElapsedMilliseconds, -1);
                    Interlocked.Increment(ref _burstsProduced);
                    Action<byte[]> handler = BurstReady;
                    if (handler != null) handler(burst);
                }
            }
            catch (Exception ex)
            {
                if (!_stopping) ReportError("FFmpeg output failed: " + ex.Message);
            }
            finally
            {
                _running = false;
                CompleteInput();
            }
        }

        private void ReadStderr()
        {
            try
            {
                string line;
                while ((line = _process.StandardError.ReadLine()) != null)
                {
                    lock (_errorLock)
                    {
                        if (_stderr.Length > 65536) _stderr.Remove(0, _stderr.Length - 32768);
                        _stderr.AppendLine(line);
                    }
                }
            }
            catch (Exception ex)
            {
                if (!_stopping) ReportError("FFmpeg diagnostic reader failed: " + ex.Message);
            }
        }

        private void ReportError(string message)
        {
            lock (_errorLock)
            {
                if (_lastError != null) return;
                _lastError = message;
            }
            _running = false;
            CompleteInput();
            Action<string> handler = Error;
            try { if (handler != null) handler(message); } catch { }
        }

        /// <summary>Call CompleteInput first. Useful for draining a finite diagnostic stream.</summary>
        public bool WaitForCompletion(int milliseconds)
        {
            if (_process == null) return true;
            bool exited = _process.WaitForExit(milliseconds);
            if (exited)
            {
                bool writer = JoinWorker(_writerThread, 1000);
                bool reader = JoinWorker(_readerThread, 1000);
                bool diagnostic = JoinWorker(_stderrThread, 1000);
                if (_process.ExitCode != 0 && !_stopping)
                    ReportError("FFmpeg exited with code " + _process.ExitCode + ". " + StderrText);
                return writer && reader && diagnostic;
            }
            return false;
        }

        public void Stop()
        {
            lock (_stopLock) StopCore();
        }

        private void StopCore()
        {
            lock (_lifecycleLock)
            {
                if (_disposed) return;
                _stopping = true;
                CompleteInput();
                if (_process == null) return;
            }
            bool processExited = false;
            try
            {
                processExited = _process.WaitForExit(1500);
                if (!processExited)
                {
                    _process.Kill();
                    processExited = _process.WaitForExit(1000);
                }
            }
            catch (InvalidOperationException) { processExited = true; }
            bool writerStopped = JoinWorker(_writerThread, 1000);
            bool readerStopped = JoinWorker(_readerThread, 1000);
            bool stderrStopped = JoinWorker(_stderrThread, 1000);
            if (!processExited || !writerStopped || !readerStopped || !stderrStopped)
            {
                // Keep the process/thread references and do not let Dispose mark this
                // instance disposed: the bridge must retain its audit/event resources
                // while a producer or callback may still be using them.
                throw new TimeoutException("AC3 encoder did not fully stop: process=" + processExited +
                    ", input=" + writerStopped + ", output=" + readerStopped + ", diagnostic=" + stderrStopped + ".");
            }
            _running = false;
            lock (_queueLock) { _inputQueue.Clear(); _queuedBytes = 0; }
        }

        private static bool JoinWorker(Thread worker, int milliseconds)
        {
            if (worker == null || !worker.IsAlive) return true;
            if (worker == Thread.CurrentThread) return false;
            return worker.Join(milliseconds);
        }

        public void Dispose()
        {
            lock (_stopLock)
            {
                if (_disposed) return;
                Stop();
                lock (_lifecycleLock)
                {
                    if (_process != null) _process.Dispose();
                    _disposed = true;
                }
            }
        }
    }
}
