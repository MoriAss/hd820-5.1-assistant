using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Web.Script.Serialization;
using Hd820GameBridge;

// No production Main, device activation, recording, playback or routing calls.
// With FFmpeg-shaped arguments this same test EXE acts as a controlled pipe
// peer, so clean-EOF and blocked-callback cases need no external executable.
internal static class DeepAudioRegressionHarness {
 const BindingFlags Private=BindingFlags.Instance|BindingFlags.NonPublic;
 const string Mode="HD820_OFFLINE_FAKE_ENCODER_MODE",Carrier="HD820_OFFLINE_FAKE_ENCODER_CARRIER";
 static int assertions;
 static readonly List<string> cases=new List<string>();
 static void Check(bool value,string name){if(!value)throw new InvalidOperationException(name);assertions++;}
 static FieldInfo Field(Type type,string name){return type.GetField(name,Private);}
 static object Invoke(object value,string name,params object[] args){return value.GetType().GetMethod(name,Private).Invoke(value,args);}
 static void Case(string name,Action test){test();cases.Add(name);}
 static bool Finite(float value){return !Single.IsNaN(value)&&!Single.IsInfinity(value);}
 static byte[] Bytes(float[] samples){byte[] bytes=new byte[samples.Length*4];Buffer.BlockCopy(samples,0,bytes,0,bytes.Length);return bytes;}
 static BridgeEngine Engine(byte[] carrier){return new BridgeEngine("synthetic-no-device","synthetic-no-device","unused",carrier,false);}
 static void Reject(Action action,string label){bool rejected=false;try{action();}catch(ArgumentException){rejected=true;}catch(InvalidOperationException){rejected=true;}catch(InvalidDataException){rejected=true;}Check(rejected,label);}
 static int FakeEncoder(){
  if(Environment.GetEnvironmentVariable(Mode)=="clean-eof")return 0;
  Stream input=Console.OpenStandardInput();byte[] bytes=new byte[4096];while(input.Read(bytes,0,bytes.Length)>0){}
  byte[] carrier=File.ReadAllBytes(Environment.GetEnvironmentVariable(Carrier));Stream output=Console.OpenStandardOutput();output.Write(carrier,0,carrier.Length);output.Flush();return 0;
 }
 static void MainMath(){
  var clock=new PcmClockSynchronizer();float[] invalid=new float[24];invalid[0]=Single.NaN;invalid[7]=Single.PositiveInfinity;invalid[12]=-.25f;
  float[] output=clock.Process(invalid);foreach(float value in output)Check(Finite(value),"unity path finite");
  Check(Single.IsNaN(invalid[0]),"defensive sanitization does not mutate caller");Check(output[12]==-.25f,"finite samples preserved");
  var active=new PcmClockSynchronizer();active.SetCorrectionPpm(1000);
  for(int block=0;block<4;block++){float[] extreme=new float[480*6];for(int i=0;i<extreme.Length;i++)extreme[i]=((i/6)%2==0?Single.MaxValue:-Single.MaxValue);foreach(float value in active.Process(extreme))Check(Finite(value),"finite extrema stay finite");}
  var bass=new BassManager();float[] impulse=new float[4800*6];impulse[0]=.5f;bass.Apply(impulse);float[] silence=new float[4800*6];for(int i=0;i<100;i++)bass.Apply(silence);
  foreach(string section in new[]{"_first","_second"}){object filter=Field(typeof(BassManager),section).GetValue(bass);foreach(string state in new[]{"_z1","_z2"})Check((double)Field(filter.GetType(),state).GetValue(filter)==0,"long silence flushes inaudible filter state");}
  var format=AudioFormat.Float(8,0x63F);float[] maximum=new float[8];for(int i=0;i<8;i++)maximum[i]=Single.MaxValue;
  foreach(float value in ChannelMapper.ConvertPacket(Bytes(maximum),1,format,1))Check(value==1,"surround sum cannot overflow to silence");
  Reject(delegate{ChannelMapper.ConvertPacket(new byte[23],1,AudioFormat.Float(6,0x3F),1);},"short PCM rejected before mapping");
  Reject(delegate{ChannelMapper.ConvertPacket(new byte[24],-1,AudioFormat.Float(6,0x3F),1);},"negative frames rejected");
  Reject(delegate{AudioFormat.Read(IntPtr.Zero);},"null format rejected without unmanaged access");
  Check(ChannelMapper.IsSupportedLayout(AudioFormat.Float(6,0x3F)) && ChannelMapper.IsSupportedLayout(AudioFormat.Float(6,0x60F)) && ChannelMapper.IsSupportedLayout(AudioFormat.Float(8,0x63F)),"ordinary 5.1 and 7.1 remain supported");
  Reject(delegate{ChannelMapper.ConvertPacket(new byte[7*4],1,AudioFormat.Float(7,0x13F),1);},"6.1 back-center is not silently discarded");
  Reject(delegate{ChannelMapper.ConvertPacket(new byte[8*4],1,AudioFormat.Float(8,0xFF),1);},"7.1 wide front channels are not silently discarded");
 }
 static void CarrierChecks(byte[] silence){
  RealtimeAc3Encoder.ValidateBurst(silence);BridgeEngine.ValidateBurst(silence);
  foreach(string mutation in new[]{"tiny-length","unaligned-length","header-size","sample-rate","bsid","padding","error-flag"}){
   byte[] bad=(byte[])silence.Clone();
   if(mutation=="tiny-length"){bad[6]=16;bad[7]=0;}
   if(mutation=="unaligned-length")bad[6]=1;
   if(mutation=="header-size")bad[13]=0;
   if(mutation=="sample-rate")bad[13]|=64;
   if(mutation=="bsid")bad[12]=72;
   if(mutation=="padding")bad[6143]=1;
   if(mutation=="error-flag")bad[5]|=128;
   Reject(delegate{RealtimeAc3Encoder.ValidateBurst(bad);},"encoder rejects "+mutation);
   Reject(delegate{BridgeEngine.ValidateBurst(bad);},"bridge rejects "+mutation);
  }
 }
 static void EngineLifecycle(byte[] silence){
  var stopped=Engine(silence);stopped.Stop();Reject(delegate{stopped.Start();},"stop-then-start rejected before hardware");stopped.Dispose();Reject(delegate{stopped.Start();},"disposed start rejected before hardware");
  using(var cancelled=Engine(silence))using(var cancel=new ManualResetEvent(true)){bool rejected=false;try{cancelled.Start(cancel);}catch(OperationCanceledException){rejected=true;}Check(rejected,"pre-cancelled start opens no hardware");Check(!(bool)Field(typeof(BridgeEngine),"startAttempted").GetValue(cancelled),"pre-cancel preserves unstarted state");}
  using(var duplicate=Engine(silence)){Field(typeof(BridgeEngine),"startAttempted").SetValue(duplicate,true);Reject(delegate{duplicate.Start();},"duplicate start rejected before hardware");}
  var pending=Engine(silence);object lifecycle=Field(typeof(BridgeEngine),"lifecycleGate").GetValue(pending);
  var requested=(ManualResetEvent)Field(typeof(BridgeEngine),"stopRequested").GetValue(pending);var finished=(ManualResetEvent)Field(typeof(BridgeEngine),"startupFinished").GetValue(pending);
  Exception failure=null;using(var entered=new ManualResetEvent(false)){
   Thread producer=new Thread(delegate(){try{entered.Set();requested.WaitOne();Thread.Sleep(60);lock(lifecycle){Field(typeof(BridgeEngine),"starting").SetValue(pending,false);finished.Set();}}catch(Exception e){failure=e;}});producer.IsBackground=true;
   Field(typeof(BridgeEngine),"starting").SetValue(pending,true);Field(typeof(BridgeEngine),"startupThread").SetValue(pending,producer);finished.Reset();producer.Start();entered.WaitOne();
   var elapsed=Stopwatch.StartNew();pending.Dispose();Check(producer.Join(1000)&&failure==null,"startup producer exits before handles disposed");Check(elapsed.ElapsedMilliseconds>=40,"dispose waits for resource publication to finish");
  }
  using(var engine=Engine(silence)){
   Invoke(engine,"ObserveThreadGap",true,99L);Invoke(engine,"ObserveThreadGap",true,1237L);Invoke(engine,"ObserveThreadGap",false,1238L);
   var state=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(engine.MetricsJson());
   Check(Convert.ToInt64(state["CaptureLongGapEvents"])==1 && Convert.ToInt64(state["OutputLongGapEvents"])==1,"long-gap counters are separate and thresholded");
   Check(Convert.ToInt64(state["LastCaptureGapUtcTicks"])>DateTime.UtcNow.AddSeconds(-5).Ticks,"long gaps include UTC correlation marker");
  }
 }
 static void OutputClockSnapshot(byte[] silence){
  using(var engine=Engine(silence)){
   var publish=typeof(BridgeEngine).GetMethod("PublishOutputClock",Private);var read=typeof(BridgeEngine).GetMethod("TryReadOutputClock",Private);var audio=Field(typeof(BridgeEngine),"audioBurstsRendered");
   Exception failure=null;int done=0;Thread writer=new Thread(delegate(){try{for(long i=1;i<=12000;i++){audio.SetValue(engine,i);publish.Invoke(engine,new object[]{(uint)(i&2047)});}}catch(Exception e){failure=e;}finally{Interlocked.Exchange(ref done,1);}});writer.IsBackground=true;writer.Start();
   int consistent=0;do{object[] args={0L,(uint)0};if((bool)read.Invoke(engine,args)){long count=(long)args[0];Check((uint)args[1]==(uint)(count&2047),"rendered count and padding belong to same snapshot");consistent++;}}while(Interlocked.CompareExchange(ref done,0,0)==0);
   Check(writer.Join(1000)&&failure==null&&consistent>0,"clock snapshot reader survives concurrent publication");
  }
 }
 static void ConcurrentPaddingReset(byte[] silence){
  using(var engine=Engine(silence)){
   int stop=0;Thread writer=new Thread(delegate(){while(Interlocked.CompareExchange(ref stop,0,0)==0){Interlocked.Exchange(ref engine.PaddingSamples,1);Interlocked.Exchange(ref engine.PaddingSumFrames,2400);Interlocked.Exchange(ref engine.PaddingSamples,0);Interlocked.Exchange(ref engine.PaddingSumFrames,0);}});writer.IsBackground=true;writer.Start();
   try{for(int i=0;i<30000;i++){double mean=engine.MeanPaddingMs;Check(!Double.IsNaN(mean)&&!Double.IsInfinity(mean)&&mean>=0,"mode-reset padding mean remains finite");}}
   finally{Interlocked.Exchange(ref stop,1);Check(writer.Join(2000),"padding-reset writer terminates");}
  }
 }
 static void EncoderLifecycle(string self,string silencePath){
  Environment.SetEnvironmentVariable(Carrier,silencePath);Environment.SetEnvironmentVariable(Mode,"clean-eof");
  using(var exited=new RealtimeAc3Encoder(self))using(var error=new ManualResetEvent(false)){
   int events=0;exited.Error+=delegate(string message){Interlocked.Increment(ref events);error.Set();};exited.Start();Check(error.WaitOne(3000),"clean early EOF is an error");exited.Stop();Check(events==1 && !exited.IsRunning,"first failure emitted once and stops acceptance");
  }
  Environment.SetEnvironmentVariable(Mode,"wait-input");
  using(var completion=new RealtimeAc3Encoder(self))using(var entered=new ManualResetEvent(false))using(var release=new ManualResetEvent(false)){
   completion.BurstReady+=delegate(byte[] b){entered.Set();release.WaitOne(5000);};completion.Start();completion.CompleteInput();Check(entered.WaitOne(3000),"fake carrier reached callback");
   try{Check(!completion.WaitForCompletion(3000),"process exit alone is not reader completion");}finally{release.Set();}
   completion.Stop();Check(completion.LastError==null,"intentional drain is not an unexpected EOF");
  }
  using(var stopped=new RealtimeAc3Encoder(self)){stopped.Stop();Reject(delegate{stopped.Start();},"encoder stop-before-start is terminal");}
  using(var concurrent=new RealtimeAc3Encoder(self))using(var go=new ManualResetEvent(false)){
   int successes=0,rejected=0;var errors=new ConcurrentQueue<Exception>();ThreadStart start=delegate{go.WaitOne();try{concurrent.Start();Interlocked.Increment(ref successes);}catch(InvalidOperationException){Interlocked.Increment(ref rejected);}catch(Exception e){errors.Enqueue(e);}};
   Thread first=new Thread(start),second=new Thread(start);first.IsBackground=second.IsBackground=true;first.Start();second.Start();go.Set();Check(first.Join(5000)&&second.Join(5000),"concurrent starts finish");Check(successes==1&&rejected==1&&errors.IsEmpty,"exactly one encoder start publishes workers");
   Thread stop=new Thread(delegate(){try{concurrent.Stop();}catch(Exception e){errors.Enqueue(e);}}),dispose=new Thread(delegate(){try{concurrent.Dispose();}catch(Exception e){errors.Enqueue(e);}});stop.IsBackground=dispose.IsBackground=true;stop.Start();dispose.Start();Check(stop.Join(5000)&&dispose.Join(5000)&&errors.IsEmpty&&!concurrent.IsRunning,"concurrent stop/dispose release once");
  }
 }
 static int Main(string[] args){
  if(args.Length>0 && args[0]=="-hide_banner")return FakeEncoder();
  if(args.Length!=1){Console.Error.WriteLine("Usage: DeepAudioRegressionHarness <valid-48k-ac3-carrier>");return 2;}
  string oldMode=Environment.GetEnvironmentVariable(Mode),oldCarrier=Environment.GetEnvironmentVariable(Carrier);
  try{
   byte[] silence=File.ReadAllBytes(args[0]);
   Case("finite-math-and-denormal-silence",MainMath);Case("complete-carrier-rejection",delegate{CarrierChecks(silence);});Case("engine-lifecycle-and-gap-telemetry",delegate{EngineLifecycle(silence);});Case("consistent-output-clock-snapshot",delegate{OutputClockSnapshot(silence);});Case("concurrent-padding-mode-reset",delegate{ConcurrentPaddingReset(silence);});Case("encoder-lifecycle-and-pipe-completion",delegate{EncoderLifecycle(Assembly.GetExecutingAssembly().Location,Path.GetFullPath(args[0]));});
   Console.WriteLine(new JavaScriptSerializer().Serialize(new{Passed=true,Assertions=assertions,Cases=cases,Scope="Offline only; no production Main, audio endpoint, playback, capture, route or startup setter invoked."}));return 0;
  }catch(Exception e){Console.Error.WriteLine(e);return 1;}
  finally{Environment.SetEnvironmentVariable(Mode,oldMode);Environment.SetEnvironmentVariable(Carrier,oldCarrier);}
 }
}
