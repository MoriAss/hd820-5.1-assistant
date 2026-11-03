using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Web.Script.Serialization;

namespace Hd820GameBridge {
 internal sealed class BridgeEngine : IDisposable {
  readonly string sourceId,outputId,ffmpegPath;
  readonly byte[] silence;
  readonly bool channelTest;
  volatile bool disposed;
  readonly object lifecycleGate=new object(),stopGate=new object();
  readonly ManualResetEvent startupFinished=new ManualResetEvent(true),stopRequested=new ManualResetEvent(false);
  bool startAttempted,starting;Thread startupThread;
  readonly Stopwatch lifetime= new Stopwatch();
  long startedUtcTicks;
  int startGc0,startGc1,startGc2;
  long finalEncodedBursts;
  long finalFirstBurstMs,finalDroppedInputFrames;
  int finalMaxInputQueueBytes;
  readonly object burstLock=new object();
  readonly ConcurrentQueue<byte[]> bursts=new ConcurrentQueue<byte[]>();
  readonly ManualResetEvent captureReady=new ManualResetEvent(false),outputReady=new ManualResetEvent(false);
  volatile bool stopping;
  Thread captureThread,outputThread;
  RealtimeAc3Encoder encoder;
  readonly BassManager bass=new BassManager();
  readonly ChannelMeter meter=new ChannelMeter();
  readonly PcmClockSynchronizer synchronizer=new PcmClockSynchronizer();
  long acceptedPcmFrames,audioBurstsRendered;
  int outputClockVersion;
  long outputClockRenderedAudio;
  uint outputClockPadding;
  public volatile uint OutputPaddingFrames;
  public long LateBurstWaits,EmergencyQueueDrops,MaxCaptureGapMs,MaxOutputGapMs;
  public long CaptureLongGapEvents,OutputLongGapEvents,LastCaptureGapMs,LastOutputGapMs,LastCaptureGapUtcTicks,LastOutputGapUtcTicks;
  public long OutputClockSnapshotMisses;
  public double BufferedAudioMs;
  public ChannelMeterSnapshot MeterSnapshot {get{return meter.Snapshot;}}
  public double ClockCorrectionPpm {get{return synchronizer.CorrectionPpm;}}
  public volatile float Gain=0.7f;
  public volatile bool BassEnabled;
  public volatile float BassLevel=0.5f;
  public volatile bool LowLatencyEnabled=true;
  public uint OutputCapacityFrames,CaptureCapacityFrames;
  public uint MaxCapturePacketFrames;
  public string OutputScheduler,CaptureScheduler;
  public long PaddingSamples,PaddingSumFrames,PaddingMinFrames=Int64.MaxValue,PaddingMaxFrames,OutputEmptyEvents;
  public double MeanPaddingMs {get{long samples=Interlocked.Read(ref PaddingSamples),sum=Interlocked.Read(ref PaddingSumFrames);return samples<=0?0:Math.Max(0,sum)/(double)samples/48.0;}}
  public double EncoderInputQueueMs {get{var current=encoder;return current==null?0:current.QueuedInputBytes/(24.0*48.0);}}
  public string Error,SourceFormat,OutputInfo,CurrentTestChannel;
  public long CapturedFrames,RenderedBursts,DroppedBursts,InputOverflows,Underruns,Discontinuities;
  public string AuditBurstPath;
  FileStream audit;
  public long EncodedBursts { get { var current=encoder;return current==null?finalEncodedBursts:current.BurstsProduced; } }
  public bool Running { get { return !stopping && outputThread!=null && outputThread.IsAlive; } }
  public string BassInfo {get{return BassEnabled?"补充低音：80Hz / "+(BassLevel*100).ToString("F0")+"%；低音输出 "+PeakDb(bass.LastOutputLfePeak)+"；限幅 "+bass.ClippedSamples:"补充低音已关闭，保留游戏原始低音轨";}}
  static string PeakDb(double value){return value<=0?"暂无低频信号":(20*Math.Log10(value)).ToString("F1")+"dBFS";}
  public long DroppedInputFrames {get{var current=encoder;return current==null?finalDroppedInputFrames:current.DroppedInputFrames;}}
  public string MetricsJson(){var current=encoder;double uptime=lifetime.Elapsed.TotalMilliseconds;long utc=DateTime.UtcNow.Ticks;return new JavaScriptSerializer().Serialize(new{UpdatedAt=DateTime.Now.ToString("o"),Running=Running,Error=Error,SourceFormat=SourceFormat,OutputInfo=OutputInfo,Gain=Gain,BassEnabled=BassEnabled,BassLevel=BassLevel,AddedBassPeak=bass.AddedBassPeak,OutputLfePeak=bass.OutputLfePeak,LastAddedBassPeak=bass.LastAddedBassPeak,LastOutputLfePeak=bass.LastOutputLfePeak,BassMixedFrames=bass.MixedFrames,BassClippedSamples=bass.ClippedSamples,LowLatencyEnabled=LowLatencyEnabled,OutputCapacityFrames=OutputCapacityFrames,CaptureCapacityFrames=CaptureCapacityFrames,MaxCapturePacketFrames=MaxCapturePacketFrames,OutputScheduler=OutputScheduler,CaptureScheduler=CaptureScheduler,ObservedMeanPaddingMs=MeanPaddingMs,ObservedMinPaddingMs=PaddingSamples==0?0:PaddingMinFrames/48.0,ObservedMaxPaddingMs=PaddingMaxFrames/48.0,PaddingSamples=PaddingSamples,OutputEmptyEvents=OutputEmptyEvents,EncoderInputQueueMs=EncoderInputQueueMs,EncoderMaxInputQueueMs=(current==null?finalMaxInputQueueBytes:current.MaxQueuedInputBytes)/(24.0*48.0),EncoderInFlightInputMs=current==null?0:current.InFlightInputFrames/48.0,DroppedInputFrames=DroppedInputFrames,InputOverflows=InputOverflows,PendingAc3Bursts=bursts.Count,EncoderFirstBurstMs=current==null?finalFirstBurstMs:current.FirstBurstElapsedMs,CapturedFrames=CapturedFrames,EncodedBursts=EncodedBursts,RenderedBursts=RenderedBursts,Underruns=Underruns,DroppedBursts=DroppedBursts,Discontinuities=Discontinuities,LateBurstWaits=Interlocked.Read(ref LateBurstWaits),EmergencyQueueDrops=Interlocked.Read(ref EmergencyQueueDrops),MaxCaptureGapMs=Interlocked.Read(ref MaxCaptureGapMs),MaxOutputGapMs=Interlocked.Read(ref MaxOutputGapMs),AcceptedPcmFrames=Interlocked.Read(ref acceptedPcmFrames),AudioBurstsRendered=Interlocked.Read(ref audioBurstsRendered),BufferedAudioMs=BufferedAudioMs,ClockCorrectionPpm=ClockCorrectionPpm,ClockTargetCorrectionPpm=synchronizer.TargetCorrectionPpm,FilteredBufferedAudioMs=synchronizer.FilteredBufferFrames/48.0,OutputPaddingMs=OutputPaddingFrames/48.0,MeterSnapshot=MeterSnapshot,CaptureLongGapEvents=Interlocked.Read(ref CaptureLongGapEvents),OutputLongGapEvents=Interlocked.Read(ref OutputLongGapEvents),LastCaptureGapMs=Interlocked.Read(ref LastCaptureGapMs),LastOutputGapMs=Interlocked.Read(ref LastOutputGapMs),LastCaptureGapUtcTicks=Interlocked.Read(ref LastCaptureGapUtcTicks),LastOutputGapUtcTicks=Interlocked.Read(ref LastOutputGapUtcTicks),OutputClockSnapshotMisses=Interlocked.Read(ref OutputClockSnapshotMisses),AudioUptimeMs=uptime,UtcElapsedMinusStopwatchMs=startedUtcTicks==0?0:(utc-Interlocked.Read(ref startedUtcTicks))/(double)TimeSpan.TicksPerMillisecond-uptime,GcGen0Collections=startAttempted?GC.CollectionCount(0)-startGc0:0,GcGen1Collections=startAttempted?GC.CollectionCount(1)-startGc1:0,GcGen2Collections=startAttempted?GC.CollectionCount(2)-startGc2:0});}
  public event Action<string> Log;
  public BridgeEngine(string source,string output,string ffmpeg,byte[] silentBurst,bool test){sourceId=source;outputId=output;ffmpegPath=ffmpeg;silence=silentBurst;channelTest=test;ValidateBurst(silence);}
  public static void ValidateBurst(byte[] b){
   RealtimeAc3Encoder.ValidateBurst(b);
  }
  void Say(string s){var handler=Log;if(handler!=null)try{handler(s);}catch{}}
  void Fail(Exception e){bool first=Interlocked.CompareExchange(ref Error,e.Message,null)==null;stopping=true;stopRequested.Set();captureReady.Set();outputReady.Set();if(first)Say("停止："+e.Message);}
  public void Start(){Start(null);}
  static void CheckCancellation(WaitHandle cancellation){if(cancellation!=null && cancellation.WaitOne(0))throw new OperationCanceledException("已取消5.1连接。");}
  static void WaitUntilReady(WaitHandle ready,WaitHandle cancellation,string timeoutMessage){
   CheckCancellation(cancellation);
   int result=cancellation==null?(ready.WaitOne(6000)?0:WaitHandle.WaitTimeout):WaitHandle.WaitAny(new[]{ready,cancellation},6000);
   CheckCancellation(cancellation);if(result==WaitHandle.WaitTimeout)throw new TimeoutException(timeoutMessage);
  }
  public void Start(WaitHandle cancellation){
   lock(lifecycleGate){
    if(disposed)throw new ObjectDisposedException("BridgeEngine");
    if(stopping || startAttempted)throw new InvalidOperationException("已启动或停止的音频引擎不能重复使用，请创建新实例。");
    CheckCancellation(cancellation);startAttempted=true;starting=true;startupThread=Thread.CurrentThread;startupFinished.Reset();
    startedUtcTicks=DateTime.UtcNow.Ticks;startGc0=GC.CollectionCount(0);startGc1=GC.CollectionCount(1);startGc2=GC.CollectionCount(2);lifetime.Start();
   }
   try{StartCore(cancellation);}
   finally{lock(lifecycleGate){starting=false;startupThread=null;startupFinished.Set();}}
  }
  void CheckStartupCancellation(WaitHandle cancellation){CheckCancellation(cancellation);if(stopping){if(Error!=null)throw new InvalidOperationException(Error);throw new OperationCanceledException("5.1引擎正在停止，已取消连接。");}}
  void WaitForReady(WaitHandle ready,WaitHandle cancellation,string message){
   CheckStartupCancellation(cancellation);
   var handles=cancellation==null?new WaitHandle[]{ready,stopRequested}:new WaitHandle[]{ready,stopRequested,cancellation};
   int result=WaitHandle.WaitAny(handles,6000);CheckStartupCancellation(cancellation);if(result==WaitHandle.WaitTimeout)throw new TimeoutException(message);
  }
  void StartCore(WaitHandle cancellation){
   CheckStartupCancellation(cancellation);
   if(!String.IsNullOrEmpty(AuditBurstPath))audit=new FileStream(AuditBurstPath,FileMode.Create,FileAccess.Write,FileShare.Read);
   encoder=new RealtimeAc3Encoder(ffmpegPath,640,96);
   encoder.DropOldInputOnOverflow=LowLatencyEnabled;
   encoder.BurstReady+=delegate(byte[] b){try{ValidateBurst(b);if(audit!=null)audit.Write(b,0,b.Length);lock(burstLock){int maximum=LowLatencyEnabled?4:8;while(bursts.Count>=maximum){byte[] old;if(bursts.TryDequeue(out old)){Interlocked.Increment(ref DroppedBursts);Interlocked.Increment(ref EmergencyQueueDrops);}else break;}bursts.Enqueue(b);}}catch(Exception e){Fail(e);}};
   encoder.Error+=delegate(string error){if(!stopping)Fail(new InvalidOperationException("AC3编码器："+error));};encoder.Start();
   CheckStartupCancellation(cancellation);
   outputThread=new Thread(OutputLoop){IsBackground=true,Name="HD820 AC3 output",Priority=ThreadPriority.AboveNormal};outputThread.SetApartmentState(ApartmentState.STA);outputThread.Start();
   WaitForReady(outputReady,cancellation,"HD820输出初始化超时。");if(Error!=null)throw new InvalidOperationException(Error);
   CheckStartupCancellation(cancellation);
   captureThread=new Thread(channelTest?(ThreadStart)TestLoop:CaptureLoop){IsBackground=true,Name="5.1 game capture",Priority=ThreadPriority.AboveNormal};captureThread.SetApartmentState(ApartmentState.STA);captureThread.Start();
   WaitForReady(captureReady,cancellation,"游戏入口初始化超时。");if(Error!=null)throw new InvalidOperationException(Error);
  }
  void PublishOutputClock(uint padding){
   Interlocked.Increment(ref outputClockVersion);
   Interlocked.Exchange(ref outputClockRenderedAudio,Interlocked.Read(ref audioBurstsRendered));outputClockPadding=padding;
   Interlocked.Increment(ref outputClockVersion);OutputPaddingFrames=padding;
  }
  bool TryReadOutputClock(out long audio,out uint padding){
   audio=0;padding=0;
   for(int attempt=0;attempt<8;attempt++){
    int before=Interlocked.CompareExchange(ref outputClockVersion,0,0);if((before&1)!=0)continue;
    long count=Interlocked.Read(ref outputClockRenderedAudio);uint pending=outputClockPadding;
    if(before==Interlocked.CompareExchange(ref outputClockVersion,0,0)){audio=count;padding=pending;return true;}
   }return false;
  }
  void ObserveThreadGap(bool capture,long milliseconds){
   if(capture)MaxCaptureGapMs=Math.Max(MaxCaptureGapMs,milliseconds);else MaxOutputGapMs=Math.Max(MaxOutputGapMs,milliseconds);
   if(milliseconds<100)return;long utc=DateTime.UtcNow.Ticks;
   if(capture){Interlocked.Increment(ref CaptureLongGapEvents);Interlocked.Exchange(ref LastCaptureGapMs,milliseconds);Interlocked.Exchange(ref LastCaptureGapUtcTicks,utc);}
   else{Interlocked.Increment(ref OutputLongGapEvents);Interlocked.Exchange(ref LastOutputGapMs,milliseconds);Interlocked.Exchange(ref LastOutputGapUtcTicks,utc);}
  }
  void OutputLoop(){
   IMMDevice device=null;IAudioClient client=null;IAudioRenderClient render=null;
   var scheduler=new AudioScheduler();OutputScheduler=scheduler.Status;Say("输出调度："+OutputScheduler);
   try{
    device=AudioApi.Device(outputId);client=AudioApi.Activate<IAudioClient>(device);
    var format=AudioFormat.AC3();IntPtr p=format.Allocate();
    try{AudioApi.Check(client.Initialize(1,0,640000,0,p,IntPtr.Zero),"初始化HD820独占AC3输出");}finally{Marshal.FreeCoTaskMem(p);}
    uint capacity;AudioApi.Check(client.GetBufferSize(out capacity),"读取HD820缓冲区");
    OutputCapacityFrames=capacity;
    if(capacity<1536)throw new InvalidOperationException("HD820缓冲小于一个AC3帧。");
    render=AudioApi.Service<IAudioRenderClient>(client);long latency;client.GetStreamLatency(out latency);
    OutputInfo="AC3 5.1 / 640kbps / 48kHz；设备缓冲 "+capacity+"帧（"+(capacity/48.0).ToString("F1")+"ms），驱动延迟 "+(latency/10000.0).ToString("F1")+"ms";
    // Only validated, fully encoded AC3 bursts enter the physical output. Never play PCM test bytes here.
    uint prefill=LowLatencyEnabled?1536:capacity;
    uint prefilled=0;for(uint position=0;position+1536<=prefill;position+=1536){WriteBurst(render,silence);prefilled+=1536;}PublishOutputClock(prefilled);
    AudioApi.Check(client.Start(),"开始HD820输出");Say(OutputInfo);outputReady.Set();
    bool haveEncodedAudio=false,wasEmpty=false,lastMode=LowLatencyEnabled,waitingForBurst=false;
    var outputWatch=Stopwatch.StartNew();long previousOutputPoll=0;
    while(!stopping){
     long now=outputWatch.ElapsedMilliseconds;ObserveThreadGap(false,now-previousOutputPoll);previousOutputPoll=now;
     uint padding;AudioApi.Check(client.GetCurrentPadding(out padding),"读取输出缓冲状态");
     PublishOutputClock(padding);
     if(padding>capacity)throw new InvalidOperationException("HD820缓冲状态无效。");
     if(lastMode!=LowLatencyEnabled){lastMode=LowLatencyEnabled;PaddingSamples=PaddingSumFrames=PaddingMaxFrames=OutputEmptyEvents=0;PaddingMinFrames=Int64.MaxValue;wasEmpty=false;}
     if(haveEncodedAudio){Interlocked.Increment(ref PaddingSamples);Interlocked.Add(ref PaddingSumFrames,padding);PaddingMinFrames=Math.Min(PaddingMinFrames,padding);PaddingMaxFrames=Math.Max(PaddingMaxFrames,padding);if(padding==0 && !wasEmpty)Interlocked.Increment(ref OutputEmptyEvents);wasEmpty=padding==0;}
     uint free=capacity-padding;
     if(LowLatencyEnabled && padding>960){Thread.Sleep(2);continue;}
     while(free>=1536 && !stopping){
      byte[] b;bool found;lock(burstLock){found=bursts.TryDequeue(out b);}
      if(found){haveEncodedAudio=true;waitingForBurst=false;}
      else{
       // Keep the existing audio playing while a slightly late encoded frame arrives.
       // A whole 32ms silent frame is only necessary close to actual starvation.
       if(haveEncodedAudio && padding>480){if(!waitingForBurst){Interlocked.Increment(ref LateBurstWaits);waitingForBurst=true;}break;}
       b=silence;waitingForBurst=false;if(haveEncodedAudio)Interlocked.Increment(ref Underruns);
      }
      WriteBurst(render,b);if(found)Interlocked.Increment(ref audioBurstsRendered);
      PublishOutputClock(padding+1536);Interlocked.Increment(ref RenderedBursts);free-=1536;
      if(LowLatencyEnabled)break;
      padding+=1536;
     }
     Thread.Sleep(2);
    }
   }catch(Exception e){Fail(e);}finally{if(client!=null)client.Stop();AudioApi.Release(render);AudioApi.Release(client);AudioApi.Release(device);scheduler.Dispose();outputReady.Set();}
  }
  static void WriteBurst(IAudioRenderClient render,byte[] b){ValidateBurst(b);IntPtr p;AudioApi.Check(render.GetBuffer(1536,out p),"取得AC3输出缓冲");try{Marshal.Copy(b,0,p,6144);}finally{AudioApi.Check(render.ReleaseBuffer(1536,0),"提交AC3输出帧");}}
  void CaptureLoop(){
   IMMDevice device=null;IAudioClient client=null,clock=null;IAudioCaptureClient capture=null;IAudioRenderClient clockRender=null;
   var scheduler=new AudioScheduler();CaptureScheduler=scheduler.Status;Say("采集调度："+CaptureScheduler);
   try{
    device=AudioApi.Device(sourceId);client=AudioApi.Activate<IAudioClient>(device);IntPtr p;AudioApi.Check(client.GetMixFormat(out p),"读取游戏入口格式");AudioFormat format;
    try{format=AudioFormat.Read(p);if(format.Rate!=48000 || format.Channels<6 || format.Channels>8)throw new InvalidOperationException("游戏入口当前是"+format+"。请先在声音面板设为48kHz 5.1/7.1。");
     ChannelMapper.ConvertPacket(new byte[format.BlockAlign],1,format,1);
     AudioApi.Check(client.Initialize(0,0x00020000,400000,0,p,IntPtr.Zero),"初始化游戏多声道采集");
     // A silent shared-mode stream keeps the virtual endpoint clock alive even before a game launches.
     clock=AudioApi.Activate<IAudioClient>(device);AudioApi.Check(clock.Initialize(0,0,400000,0,p,IntPtr.Zero),"初始化虚拟入口时钟");
    }finally{Marshal.FreeCoTaskMem(p);}
    SourceFormat=format.ToString();capture=AudioApi.Service<IAudioCaptureClient>(client);clockRender=AudioApi.Service<IAudioRenderClient>(clock);AudioApi.Check(client.GetBufferSize(out CaptureCapacityFrames),"读取采集缓冲容量");
    uint clockCapacity;AudioApi.Check(clock.GetBufferSize(out clockCapacity),"读取虚拟时钟缓冲");
    FillSilent(clockRender,clockCapacity);AudioApi.Check(client.Start(),"开始游戏入口采集");AudioApi.Check(clock.Start(),"开始虚拟入口时钟");Say("采集入口："+SourceFormat);captureReady.Set();
    var watch=Stopwatch.StartNew();long lastPacket=0,previousCapturePoll=0;double lastControllerTime=0,controllerReadyAt=3;
    long lastInputDrops=0,lastBurstDrops=0,lastUnderruns=0;bool controllerMode=LowLatencyEnabled;
    while(!stopping){
     long now=watch.ElapsedMilliseconds;ObserveThreadGap(true,now-previousCapturePoll);previousCapturePoll=now;
     uint padding;AudioApi.Check(clock.GetCurrentPadding(out padding),"读取虚拟时钟状态");if(padding<clockCapacity)FillSilent(clockRender,clockCapacity-padding);
     uint frames;AudioApi.Check(capture.GetNextPacketSize(out frames),"读取游戏音频包");
     while(frames>0 && !stopping){IntPtr data;uint count,flags;ulong position,qpc;AudioApi.Check(capture.GetBuffer(out data,out count,out flags,out position,out qpc),"取得游戏六声道数据");
      try{
       MaxCapturePacketFrames=Math.Max(MaxCapturePacketFrames,count);
       if((flags&1)!=0)Interlocked.Increment(ref Discontinuities);
       byte[] bytes=new byte[checked((int)count*format.BlockAlign)];if((flags&2)==0)Marshal.Copy(data,bytes,0,bytes.Length);
       var samples=ChannelMapper.ConvertPacket(bytes,(int)count,format,Gain);
       bass.Enabled=BassEnabled;bass.Level=BassLevel;bass.Apply(samples);
       meter.Process(samples);
       long inputDrops=encoder.DroppedInputFrames,burstDrops=Interlocked.Read(ref DroppedBursts),underruns=Interlocked.Read(ref Underruns);
       long renderedAudio;uint renderedPadding;bool clockSnapshotReady=TryReadOutputClock(out renderedAudio,out renderedPadding);
       double buffered=Interlocked.Read(ref acceptedPcmFrames)-inputDrops-(renderedAudio+burstDrops)*1536.0+renderedPadding;
       if(clockSnapshotReady)BufferedAudioMs=Math.Max(0,buffered)/48.0;else Interlocked.Increment(ref OutputClockSnapshotMisses);
       double controllerTime=watch.Elapsed.TotalSeconds,dt=controllerTime-lastControllerTime;lastControllerTime=controllerTime;
       if((flags&1)!=0 || dt>=0.25 || controllerMode!=LowLatencyEnabled || inputDrops!=lastInputDrops || burstDrops!=lastBurstDrops || underruns!=lastUnderruns){
        synchronizer.ResetController();controllerReadyAt=Math.Max(controllerReadyAt,Math.Max(3,controllerTime+1));
       }
       controllerMode=LowLatencyEnabled;lastInputDrops=inputDrops;lastBurstDrops=burstDrops;lastUnderruns=underruns;
       // USB and the virtual device have separate clocks. Correct the PCM rate
       // continuously instead of letting their drift force periodic 32ms skips.
       if(clockSnapshotReady && controllerTime>=controllerReadyAt && renderedAudio>0 && buffered>=0 && buffered<48000 && dt>0 && dt<0.25)
        synchronizer.UpdateBuffer(buffered,LowLatencyEnabled?4608:7680,dt);
       samples=synchronizer.Process(samples);
       byte[] raw=new byte[samples.Length*4];Buffer.BlockCopy(samples,0,raw,0,raw.Length);
       encoder.DropOldInputOnOverflow=LowLatencyEnabled;
       if(!encoder.TryWriteOwnedPcmBytes(raw)){Interlocked.Increment(ref InputOverflows);throw new InvalidOperationException("编码器未及时接收音频，已停止以防延迟累积。");}
       Interlocked.Add(ref acceptedPcmFrames,samples.Length/6);
       Interlocked.Add(ref CapturedFrames,count);lastPacket=watch.ElapsedMilliseconds;
      }finally{AudioApi.Check(capture.ReleaseBuffer(count),"释放游戏采集缓冲");}
      AudioApi.Check(capture.GetNextPacketSize(out frames),"读取后续游戏音频包");
     }
     if(watch.ElapsedMilliseconds-lastPacket>3000)throw new InvalidOperationException("虚拟入口没有音频时钟。请保留日志，暂用声道测试确认HD820输出。");
     Thread.Sleep(2);
    }
   }catch(Exception e){Fail(e);}finally{if(clock!=null)clock.Stop();if(client!=null)client.Stop();AudioApi.Release(clockRender);AudioApi.Release(capture);AudioApi.Release(clock);AudioApi.Release(client);AudioApi.Release(device);scheduler.Dispose();captureReady.Set();}
  }
  static void FillSilent(IAudioRenderClient render,uint frames){if(frames==0)return;IntPtr p;AudioApi.Check(render.GetBuffer(frames,out p),"取得虚拟时钟缓冲");AudioApi.Check(render.ReleaseBuffer(frames,2),"提交虚拟时钟静音");}
  void TestLoop(){
   try{
    SourceFormat="逐声道测试（70%音量时约-27dBFS）";captureReady.Set();
    string[] names={"左前 FL","右前 FR","中置 C","低音 LFE","左环绕 SL","右环绕 SR"};double[] frequencies={440,554,659,60,784,988};
    int frames=0;var watch=Stopwatch.StartNew();
    while(!stopping){
     float[] data=new float[480*6];int channel=(frames/48000/3)%6;CurrentTestChannel=names[channel];
     for(int f=0;f<480;f++){int position=frames+f;double local=(position%144000)/48000.0;double envelope=local<2.0?Math.Min(1,local/0.03)*Math.Min(1,(2-local)/0.03):0;
      data[f*6+channel]=(float)(0.0630957*Gain*Math.Sin(2*Math.PI*frequencies[channel]*position/48000.0)*Math.Max(0,envelope));}
     meter.Process(data);
     if(!encoder.TryWriteOwnedPcmBytes(ToBytes(data)))throw new InvalidOperationException("测试编码队列拥塞。");
     Interlocked.Add(ref acceptedPcmFrames,data.Length/6);
     frames+=480;Interlocked.Add(ref CapturedFrames,480);long until=frames/48;
     while(!stopping && watch.ElapsedMilliseconds<until)Thread.Sleep(1);
    }
   }catch(Exception e){Fail(e);}finally{captureReady.Set();}
  }
  static byte[] ToBytes(float[] samples){byte[] b=new byte[samples.Length*4];Buffer.BlockCopy(samples,0,b,0,b.Length);return b;}
  public void Stop(){
   lock(stopGate)StopCore();
  }
  void StopCore(){
   bool waitForStartup;Thread owner;
   lock(lifecycleGate){if(disposed)return;stopping=true;stopRequested.Set();waitForStartup=starting;owner=startupThread;}
   if(waitForStartup && (owner==Thread.CurrentThread || !startupFinished.WaitOne(4000)))throw new TimeoutException("音频初始化尚未结束，保留资源以便稍后重试停止。");
   bool captureStopped=captureThread==null || !captureThread.IsAlive || (captureThread!=Thread.CurrentThread && captureThread.Join(4000));
   bool outputStopped=outputThread==null || !outputThread.IsAlive || (outputThread!=Thread.CurrentThread && outputThread.Join(4000));
   if(!captureStopped || !outputStopped)throw new TimeoutException("USB或虚拟声卡驱动未及时释放音频线程。请关闭助手后重开，暂不允许重复启动。");
   if(encoder!=null){encoder.Stop();finalEncodedBursts=encoder.BurstsProduced;finalFirstBurstMs=encoder.FirstBurstElapsedMs;finalDroppedInputFrames=encoder.DroppedInputFrames;finalMaxInputQueueBytes=encoder.MaxQueuedInputBytes;encoder.Dispose();encoder=null;}
   if(audit!=null){audit.Dispose();audit=null;}
   byte[] pending;lock(burstLock){while(bursts.TryDequeue(out pending)){}}
  }
  public void Dispose(){lock(stopGate){if(disposed)return;Stop();lock(lifecycleGate){disposed=true;captureReady.Dispose();outputReady.Dispose();startupFinished.Dispose();stopRequested.Dispose();}}}
  public static string DiagnoseOutput(string id){
   IMMDevice d=null;IAudioClient a=null;IAudioRenderClient render=null;
   try{d=AudioApi.Device(id);a=AudioApi.Activate<IAudioClient>(d);IntPtr p=AudioFormat.AC3().Allocate();try{AudioApi.Check(a.Initialize(1,0,640000,0,p,IntPtr.Zero),"独占AC3初始化");}finally{Marshal.FreeCoTaskMem(p);}uint frames;long latency;AudioApi.Check(a.GetBufferSize(out frames),"缓冲大小");a.GetStreamLatency(out latency);render=AudioApi.Service<IAudioRenderClient>(a);return "Initialize成功；未调用Start；buffer="+frames+"帧；latency="+(latency/10000.0).ToString("F1")+"ms";}finally{AudioApi.Release(render);AudioApi.Release(a);AudioApi.Release(d);}
  }
 }
}
