using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using Hd820GameBridge;

// Offline only. No device interfaces, production Main, routing or settings.
internal static class AudioBufferHarness {
 const string Mode="HD820_BUFFER_TEST_MODE",Capture="HD820_BUFFER_TEST_CAPTURE",Carrier="HD820_BUFFER_TEST_CARRIER";
 static int checks;
 static void Check(bool value,string message){checks++;if(!value)throw new InvalidOperationException(message);}
 [StructLayout(LayoutKind.Explicit)] struct FloatBits {[FieldOffset(0)]public float Value;[FieldOffset(0)]public int Bits;}
 static int Bits(float value){return new FloatBits{Value=value}.Bits;}
 static void Active(RealtimeAc3Encoder value){typeof(RealtimeAc3Encoder).GetField("_running",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(value,true);}
 static int Fake(){
  string mode=Environment.GetEnvironmentVariable(Mode);Thread.Sleep(250);if(mode=="fail")return 7;
  if(mode=="flood"){Console.Error.Write(new string('x',262144));Console.Error.Flush();}
  using(var input=Console.OpenStandardInput())using(var output=File.Create(Environment.GetEnvironmentVariable(Capture)))input.CopyTo(output);
  byte[] carrier=File.ReadAllBytes(Environment.GetEnvironmentVariable(Carrier));
  using(var output=Console.OpenStandardOutput()){output.Write(carrier,0,carrier.Length);output.Flush();}return 0;
 }
 static object ResampleExact(Assembly baseline){
  int[] sizes={0,1,2,7,127,480,960,1536,31};
  foreach(double ppm in new[]{0.0,1000.0,-1000.0,5000.0,-5000.0}){
   object reference=baseline==null?(object)new PcmClockSynchronizer():Activator.CreateInstance(baseline.GetType("Hd820GameBridge.PcmClockSynchronizer"),true);
   var sourceMethod=reference.GetType().GetMethod("Process",new[]{typeof(float[])});
   var original=(Func<float[],float[]>)Delegate.CreateDelegate(typeof(Func<float[],float[]>),reference,sourceMethod);
   reference.GetType().GetMethod("SetCorrectionPpm").Invoke(reference,new object[]{ppm});
   var current=new PcmClockSynchronizer();current.SetCorrectionPpm(ppm);
   float[] input=new float[1536*6+48],output=new float[PcmClockSynchronizer.MaximumOutputSamples(1536*6)];long position=0;
   for(int block=0;block<270;block++){
    int count=sizes[block%sizes.Length]*6;var compact=new float[count];
    for(int i=0;i<input.Length;i++)input[i]=Single.PositiveInfinity;
    for(int i=0;i<count;i++)compact[i]=input[i]=(float)(Math.Sin((position+i/6)*.013+i%6)*.2);
    if(count>6&&block%19==0)compact[2]=input[2]=Single.NaN;
    for(int i=0;i<output.Length;i++)output[i]=1234;
    float[] expected=original(compact);int written=current.Process(input,count,output);
    Check(written==expected.Length,"resampler valid count");
    for(int i=0;i<written;i++)Check(Bits(expected[i])==Bits(output[i]),"resampler PCM differs from baseline");
    for(int i=written;i<output.Length;i++)Check(output[i]==1234,"resampler touched output capacity tail");
    position+=count/6;
   }
  }
  bool alias=false;try{var c=new PcmClockSynchronizer();var p=new float[12];c.Process(p,12,p);}catch(ArgumentException){alias=true;}
  Check(alias,"resampler rejects aliased buffers");return new{Name="resampler-effective-count-and-baseline-exactness",Passed=true,HistoricalBaseline=baseline!=null};
 }
 static object BassRange(Assembly baseline){
  object reference=baseline==null?(object)new BassManager():Activator.CreateInstance(baseline.GetType("Hd820GameBridge.BassManager"),true);
  reference.GetType().GetField("Enabled").SetValue(reference,true);reference.GetType().GetField("Level").SetValue(reference,.65f);
  var original=(Action<float[]>)Delegate.CreateDelegate(typeof(Action<float[]>),reference,reference.GetType().GetMethod("Apply",new[]{typeof(float[])}));
  var current=new BassManager{Enabled=true,Level=.65f};var padded=new float[480*6+60];
  for(int block=0;block<120;block++){
   int count=(block%7+1)*60;var compact=new float[count];
   for(int i=0;i<padded.Length;i++)padded[i]=1234;
   for(int i=0;i<count;i++)compact[i]=padded[i]=(float)(.1*Math.Sin((block*480+i/6)*.02+i%6));
   original(compact);current.Apply(padded,count);
   for(int i=0;i<count;i++)Check(Bits(compact[i])==Bits(padded[i]),"bass valid PCM differs");
   for(int i=count;i<padded.Length;i++)Check(padded[i]==1234,"bass touched capacity tail");
  }
  bool rejected=false;try{current.Apply(padded,7);}catch(ArgumentException){rejected=true;}Check(rejected,"bass partial frame rejected");
  return new{Name="bass-effective-range",Passed=true};
 }
 static object Measure(string name,Action action){
  action();GC.Collect();GC.WaitForPendingFinalizers();GC.Collect();
  long start=AppDomain.CurrentDomain.MonitoringTotalAllocatedMemorySize;int a=GC.CollectionCount(0),b=GC.CollectionCount(1),c=GC.CollectionCount(2);var watch=Stopwatch.StartNew();action();watch.Stop();
  return new{Name=name,ElapsedMs=watch.Elapsed.TotalMilliseconds,AllocatedBytes=AppDomain.CurrentDomain.MonitoringTotalAllocatedMemorySize-start,Gen0=GC.CollectionCount(0)-a,Gen1=GC.CollectionCount(1)-b,Gen2=GC.CollectionCount(2)-c};
 }
 static object ComponentAllocation(Assembly baseline){
  const int blocks=6000;float[] input=new float[480*6];for(int i=0;i<input.Length;i++)input[i]=(float)(.1*Math.Sin(i*.013));
  object reference=baseline==null?(object)new PcmClockSynchronizer():Activator.CreateInstance(baseline.GetType("Hd820GameBridge.PcmClockSynchronizer"),true);
  reference.GetType().GetMethod("SetCorrectionPpm").Invoke(reference,new object[]{120.0});
  var original=(Func<float[],float[]>)Delegate.CreateDelegate(typeof(Func<float[],float[]>),reference,reference.GetType().GetMethod("Process",new[]{typeof(float[])}));
  var current=new PcmClockSynchronizer();current.SetCorrectionPpm(120);var output=new float[PcmClockSynchronizer.MaximumOutputSamples(input.Length)];var raw=new byte[output.Length*4];
  object before=Measure("original-allocating-resample-and-float-to-byte",delegate{for(int i=0;i<blocks;i++){float[] value=original(input);var bytes=new byte[value.Length*4];Buffer.BlockCopy(value,0,bytes,0,bytes.Length);}});
  object after=Measure("reused-resample-and-float-to-byte-workspaces",delegate{for(int i=0;i<blocks;i++){int count=current.Process(input,input.Length,output);Buffer.BlockCopy(output,0,raw,0,count*4);}});
  return new{Name="component-allocation",Blocks=blocks,FramesPerBlock=480,Before=before,After=after,Scope="Resampling and float-to-byte conversion only; not hardware latency or whole application CPU."};
 }
 static object PoolOverflow(string self){
  using(var encoder=new RealtimeAc3Encoder(self,640,32)){
   encoder.DropOldInputOnOverflow=true;Active(encoder);float[] source=new float[480*6+120];long accepted=0;
   for(int block=0;block<6000;block++){
    int count=(479+block%3)*6;for(int i=0;i<source.Length;i++)source[i]=i<count?block*.0001f:1234;
    Check(encoder.TryWritePooledSamples(source,0,count),"pool drop enqueue");accepted+=count/6;
    Array.Clear(source,0,source.Length);
   }
   byte[][] pending=encoder.SnapshotQueuedInput();Check(pending.Length>0,"pool retained queue");byte[] last=pending[pending.Length-1];
   Check(last.Length==(479+5999%3)*24,"pool uses effective byte count");Check(BitConverter.ToSingle(last,0)==5999*.0001f,"producer workspace copied before reuse");
   Check(encoder.DroppedInputFrames+encoder.QueuedInputBytes/24==accepted,"pool overflow frame accounting");
   Check(encoder.PooledBuffersAllocated<=6&&encoder.PooledBufferReuses>5900,"pool steady reuse");
   long allocated=encoder.PooledBuffersAllocated,reused=encoder.PooledBufferReuses;encoder.Stop();
   Check(encoder.PooledBuffersInUse==0&&encoder.PooledBuffersRetained<=16,"stop returns all queued pooled buffers");
   Check(!encoder.TryWritePooledSamples(source,0,480*6)&&encoder.PooledBuffersAllocated==allocated,"stopped enqueue does not allocate");
   return new{Name="bounded-pool-overflow-and-producer-reuse",Passed=true,AllocatedBuffers=allocated,BufferReuses=reused,AcceptedFrames=accepted};
  }
 }
 static object PoolPipe(string self,string output,bool fail){
  Console.InputEncoding=new UTF8Encoding(false);string path=Path.Combine(output,fail?"fault.pcm":"delayed.pcm");Environment.SetEnvironmentVariable(Mode,fail?"fail":"capture");Environment.SetEnvironmentVariable(Capture,path);
  using(var encoder=new RealtimeAc3Encoder(self,640,32)){
   encoder.DropOldInputOnOverflow=true;encoder.Start();var source=new float[480*6+60];for(int i=0;i<480*6;i++)source[i]=.125f;
   Check(encoder.TryWritePooledSamples(source,0,480*6),"first pooled packet");
   for(int wait=0;wait<200&&encoder.InFlightInputFrames==0;wait++)Thread.Sleep(1);
   Check(encoder.InFlightInputFrames>0,"controlled peer blocks an in-flight write");
   for(int block=0;block<60;block++){for(int i=0;i<source.Length;i++)source[i]=.2f+block*.001f;Check(encoder.TryWritePooledSamples(source,0,480*6),"pooled pressure write");}
   encoder.CompleteInput();Check(encoder.WaitForCompletion(5000),"pool pipe workers finish");
   if(fail)Check(encoder.LastError!=null,"failed pipe is surfaced");
   else{
    Check(encoder.LastError==null,"delayed pipe completed without error");byte[] raw=File.ReadAllBytes(path);
    Check(raw.Length==encoder.InputFramesWritten*24,"only valid bytes reached pipe");
    for(int i=0;i<480*6;i++)Check(BitConverter.ToSingle(raw,i*4)==.125f,"in-flight packet was not recycled early");
    Check(encoder.InputFramesWritten+encoder.DroppedInputFrames==61*480,"pipe pressure frame accounting");
   }
   encoder.Stop();Check(encoder.PooledBuffersInUse==0&&encoder.PooledBuffersRetained<=16,"pipe success or fault returns every buffer");
   return new{Name=fail?"pooled-pipe-failure-cleanup":"in-flight-buffer-protection",Passed=true,AllocatedBuffers=encoder.PooledBuffersAllocated,Reuses=encoder.PooledBufferReuses};
  }
 }
 static object Real(string ffmpeg,string output){
  Console.InputEncoding=new UTF8Encoding(true);string path=Path.Combine(output,"pooled.spdif");
  using(var file=File.Create(path))using(var encoder=new RealtimeAc3Encoder(ffmpeg)){
   encoder.BurstReady+=delegate(byte[] bytes){file.Write(bytes,0,bytes.Length);};encoder.Start();var pace=Stopwatch.StartNew();var data=new float[480*6+120];double[] hz={401,503,607,61,701,809};
   for(int block=0;block<600;block++){
    Array.Clear(data,0,data.Length);for(int i=480*6;i<data.Length;i++)data[i]=1234;
    for(int frame=0;frame<480;frame++){int position=block*480+frame,ch=position/48000;data[frame*6+ch]=(float)(.15*Math.Sin(2*Math.PI*hz[ch]*position/48000));}
    Check(encoder.TryWritePooledSamples(data,0,480*6),"real pooled PCM accepted");Array.Clear(data,0,data.Length);
    long wait=(block+1)*10-pace.ElapsedMilliseconds;if(wait>0)Thread.Sleep((int)wait);
   }
   encoder.CompleteInput();Check(encoder.WaitForCompletion(5000),"real pooled encoder drains");Check(encoder.LastError==null&&encoder.StderrText.Length==0,"real pooled FFmpeg error");
   Check(encoder.InputFramesWritten==288000&&encoder.PooledBuffersInUse==0,"real pooled frame and ownership counts");Check(encoder.PooledBufferReuses>550,"real pooled reuse");
   return new{Name="real-pooled-ffmpeg-with-utf8-bom",Passed=true,InputFrames=encoder.InputFramesWritten,Bursts=encoder.BurstsProduced,AllocatedBuffers=encoder.PooledBuffersAllocated,BufferReuses=encoder.PooledBufferReuses,Stderr=encoder.StderrText,Path=path};
  }
 }
 static object DiagnosticBound(string self,string output){
  Environment.SetEnvironmentVariable(Mode,"flood");Environment.SetEnvironmentVariable(Capture,Path.Combine(output,"diagnostic.pcm"));
  using(var encoder=new RealtimeAc3Encoder(self)){
   encoder.Start();encoder.CompleteInput();Check(encoder.WaitForCompletion(5000),"diagnostic flood peer completed");
   int retained=encoder.StderrText.Length;Check(encoder.LastError==null&&retained>0&&retained<=65536,"newline-free diagnostic is bounded");
   return new{Name="newline-free-ffmpeg-diagnostic-bound",Passed=true,InputCharacters=262144,RetainedCharacters=retained};
  }
 }
 static int Main(string[] args){
  if(args.Length>0&&args[0]=="-hide_banner")return Fake();
  if(args.Length<3){Console.Error.WriteLine("AudioBufferHarness <valid-carrier> <output-dir> <ffmpeg> [baseline-assembly]");return 2;}
  string oldMode=Environment.GetEnvironmentVariable(Mode),oldCapture=Environment.GetEnvironmentVariable(Capture),oldCarrier=Environment.GetEnvironmentVariable(Carrier);Encoding oldEncoding=Console.InputEncoding;
  try{
   AppDomain.MonitoringIsEnabled=true;string output=Path.GetFullPath(args[1]);Directory.CreateDirectory(output);string self=Assembly.GetExecutingAssembly().Location;
   Environment.SetEnvironmentVariable(Carrier,Path.GetFullPath(args[0]));Assembly baseline=args.Length>3?Assembly.LoadFile(Path.GetFullPath(args[3])):null;
   var cases=new List<object>();cases.Add(ResampleExact(baseline));cases.Add(BassRange(baseline));cases.Add(ComponentAllocation(baseline));cases.Add(PoolOverflow(self));cases.Add(PoolPipe(self,output,false));cases.Add(PoolPipe(self,output,true));cases.Add(DiagnosticBound(self,output));cases.Add(Real(Path.GetFullPath(args[2]),output));
   string json=new JavaScriptSerializer().Serialize(new{Passed=true,Checks=checks,Cases=cases,Scope="Offline only; no audio device, production process, settings or routing."});
   File.WriteAllText(Path.Combine(output,"audio-buffer-results.json"),json,new UTF8Encoding(false));Console.WriteLine(json);return 0;
  }catch(Exception e){Console.Error.WriteLine(e);return 1;}
  finally{Console.InputEncoding=oldEncoding;Environment.SetEnvironmentVariable(Mode,oldMode);Environment.SetEnvironmentVariable(Capture,oldCapture);Environment.SetEnvironmentVariable(Carrier,oldCarrier);}
 }
}
