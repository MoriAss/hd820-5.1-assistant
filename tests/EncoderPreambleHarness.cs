using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using Hd820GameBridge;

// Offline only. The same EXE is also a byte-recording fake FFmpeg peer.
// Explicit Console.InputEncoding makes the regression independent of the host code page.
internal static class EncoderPreambleHarness {
 const string Capture="HD820_PREAMBLE_TEST_CAPTURE",Carrier="HD820_PREAMBLE_TEST_CARRIER";
 sealed class EncodingChangesDuringStart : UTF8Encoding {
  bool changed;
  internal EncodingChangesDuringStart():base(true){}
  public override byte[] GetPreamble(){if(!changed){changed=true;Console.InputEncoding=new UTF8Encoding(false);}return base.GetPreamble();}
 }
 static int Fake(string[] args){
  int skip=0;for(int i=0;i<args.Length-1;i++)if(args[i]=="-skip_initial_bytes")skip=Int32.Parse(args[i+1]);
  using(var input=Console.OpenStandardInput())using(var output=File.Create(Environment.GetEnvironmentVariable(Capture))){input.CopyTo(output);}
  File.WriteAllText(Environment.GetEnvironmentVariable(Capture)+".skip",skip.ToString(System.Globalization.CultureInfo.InvariantCulture));
  byte[] carrier=File.ReadAllBytes(Environment.GetEnvironmentVariable(Carrier));
  using(var output=Console.OpenStandardOutput()){output.Write(carrier,0,carrier.Length);output.Flush();}return 0;
 }
 static float[] Pattern(){var value=new float[480*6];for(int i=0;i<value.Length;i++)value[i]=.125f+(i%41)*.001f;return value;}
 static bool Equal(byte[] a,int offset,byte[] b){if(a.Length-offset!=b.Length)return false;for(int i=0;i<b.Length;i++)if(a[offset+i]!=b[i])return false;return true;}
 static object ByteCase(string output,bool bom){
  Console.InputEncoding=new UTF8Encoding(bom);string name=bom?"utf8-bom":"utf8-no-bom",capture=Path.Combine(output,name+".pcm");
  Environment.SetEnvironmentVariable(Capture,capture);
  var samples=Pattern();var expected=new byte[samples.Length*4];Buffer.BlockCopy(samples,0,expected,0,expected.Length);
  string error=null,stderr;bool completed;long inputFrames;
  using(var encoder=new RealtimeAc3Encoder(Assembly.GetExecutingAssembly().Location)){
   encoder.Start();encoder.WriteSamples(samples,0,samples.Length);encoder.CompleteInput();completed=encoder.WaitForCompletion(5000);
   error=encoder.LastError;stderr=encoder.StderrText;inputFrames=encoder.InputFramesWritten;
  }
  byte[] actual=File.ReadAllBytes(capture);int skipped=Int32.Parse(File.ReadAllText(capture+".skip"));
  bool passed=completed&&error==null&&stderr.Length==0&&inputFrames==480&&Equal(actual,skipped,expected);
  return new {Name=name,Passed=passed,ExpectedBytes=expected.Length,RawBytes=actual.Length,SkippedBytes=skipped,
   Prefix=BitConverter.ToString(actual,0,Math.Min(actual.Length,12)),Complete=completed,Error=error,Stderr=stderr};
 }
 static object ChangedEncodingCase(){
  Console.InputEncoding=new EncodingChangesDuringStart();bool rejected=false,stopped=false;
  using(var encoder=new RealtimeAc3Encoder(Assembly.GetExecutingAssembly().Location)){
   try{encoder.Start();}catch(InvalidOperationException e){rejected=e.Message.IndexOf("Console input encoding changed",StringComparison.Ordinal)>=0;}
   encoder.Stop();stopped=!encoder.IsRunning;
  }
  return new{Name="encoding-changed-before-Start-writer-created",Passed=rejected&&stopped,Rejected=rejected,Stopped=stopped};
 }
 static object RealCase(string ffmpeg,string output,bool bom){
  Console.InputEncoding=new UTF8Encoding(bom);string name=bom?"utf8-bom":"utf8-no-bom",path=Path.Combine(output,name+".spdif");
  bool completed;string error,stderr;long frames,bursts;
  using(var stream=File.Create(path))using(var encoder=new RealtimeAc3Encoder(ffmpeg)){
   encoder.BurstReady+=delegate(byte[] b){stream.Write(b,0,b.Length);};encoder.Start();
   var pace=Stopwatch.StartNew();double[] frequencies={401,503,607,61,701,809};
   for(int block=0;block<600;block++){
    float[] samples=new float[480*6];for(int frame=0;frame<480;frame++){int pos=block*480+frame,ch=pos/48000;samples[frame*6+ch]=(float)(.15*Math.Sin(2*Math.PI*frequencies[ch]*pos/48000));}
    encoder.WriteSamples(samples,0,samples.Length);long wait=(block+1)*10-pace.ElapsedMilliseconds;if(wait>0)Thread.Sleep((int)wait);
   }
   encoder.CompleteInput();completed=encoder.WaitForCompletion(5000);error=encoder.LastError;stderr=encoder.StderrText;frames=encoder.InputFramesWritten;bursts=encoder.BurstsProduced;
  }
  return new{Name=name,Passed=completed&&error==null&&stderr.Length==0&&frames==288000&&bursts>0,Complete=completed,Error=error,Stderr=stderr,Frames=frames,Bursts=bursts,Path=path};
 }
 static int Main(string[] args){
  if(args.Length>0&&args[0]=="-hide_banner")return Fake(args);
  if(args.Length<2){Console.Error.WriteLine("EncoderPreambleHarness <valid-carrier> <output-dir> [ffmpeg]");return 2;}
  string oldCapture=Environment.GetEnvironmentVariable(Capture),oldCarrier=Environment.GetEnvironmentVariable(Carrier);Encoding oldEncoding=Console.InputEncoding;
  var serializer=new JavaScriptSerializer();var cases=new List<object>();bool passed=true;
  try{
   string output=Path.GetFullPath(args[1]);Directory.CreateDirectory(output);Environment.SetEnvironmentVariable(Carrier,Path.GetFullPath(args[0]));
   foreach(bool bom in new[]{false,true}){object item=ByteCase(output,bom);cases.Add(item);passed&=(bool)item.GetType().GetProperty("Passed").GetValue(item,null);}
   object changed=ChangedEncodingCase();cases.Add(changed);passed&=(bool)changed.GetType().GetProperty("Passed").GetValue(changed,null);
   if(args.Length>2)foreach(bool bom in new[]{false,true}){object item=RealCase(Path.GetFullPath(args[2]),output,bom);cases.Add(item);passed&=(bool)item.GetType().GetProperty("Passed").GetValue(item,null);}
   string json=serializer.Serialize(new{Passed=passed,Cases=cases,Scope="Offline byte pipes and FFmpeg only; no audio hardware or production process."});
   File.WriteAllText(Path.Combine(output,"preamble-results.json"),json,new UTF8Encoding(false));Console.WriteLine(json);return passed?0:1;
  }catch(Exception e){Console.Error.WriteLine(e);return 1;}
  finally{Console.InputEncoding=oldEncoding;Environment.SetEnvironmentVariable(Capture,oldCapture);Environment.SetEnvironmentVariable(Carrier,oldCarrier);}
 }
}
