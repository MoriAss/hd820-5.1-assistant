using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Hd820GameBridge;

internal static class BufferAndPreflightHarness {
 static int cases;
 static void Check(bool ok,string label){if(!ok)throw new Exception(label);cases++;}
 static int Main(string[] args){
  if(args.Length>0){if(args[0]=="hang"){Thread.Sleep(10000);return 0;}if(args[0]=="flood"){for(int i=0;i<2000;i++)Console.Write(new string('x',100));return 0;}Console.WriteLine("ffmpeg version fixture");Console.Error.WriteLine(" A....D ac3 AC-3 encoder");return 0;}
  var bytes=new byte[24];Buffer.BlockCopy(new float[]{.1f,.2f,.3f,.4f,.5f,.6f},0,bytes,0,24);
  var destination=new float[24];for(int i=0;i<destination.Length;i++)destination[i]=.99f;
  int valid=ChannelMapper.ConvertPacket(bytes,1,AudioFormat.Float(6,0x60F),1,destination);
  Check(valid==6 && destination[5]==.6f && destination[6]==.99f,"destination tail changed");
  bool rejected=false;try{ChannelMapper.ConvertPacket(bytes,1,AudioFormat.Float(6,0x60F),1,new float[5]);}catch(ArgumentException){rejected=true;}Check(rejected,"undersized output accepted");
  var meter=new ChannelMeter();var padded=new float[2400*6];for(int i=0;i<padded.Length;i++)padded[i]=1;
  for(int i=0;i<1200*6;i++)padded[i]=0;
  meter.Process(padded,1200*6);Check(meter.Snapshot==null,"meter consumed stale tail");meter.Process(padded,1200*6);
  Check(meter.Snapshot.RmsDbFs[0]==-72 && meter.Snapshot.PeakDbFs[5]==-72,"stale tail leaked into meter");
  rejected=false;try{meter.Process(padded,7);}catch(ArgumentException){rejected=true;}Check(rejected,"partial frame accepted by meter");
  Check(Preflight.HasCapability(" A....D ac3 AC-3 (ATSC A/52)\n", "ac3"),"encoder missing");
  Check(Preflight.HasCapability("  E spdif IEC 61937\r\n", "spdif"),"muxer missing");
  Check(!Preflight.HasCapability(" A..... ac3_fixed fixed point\n", "ac3"),"wrong encoder accepted");
  Check(!Preflight.HasCapability("mention ac3 in some other description", "ac3"),"description mistaken for capability");
  string exe=Process.GetCurrentProcess().MainModule.FileName;
  var process=Preflight.Capture(exe,"probe",2000);Check(!process.TimedOut && process.ExitCode==0 && process.Output.Contains("ffmpeg version fixture") && process.Output.Contains("ac3"),"stdout/stderr not drained");
  var watch=Stopwatch.StartNew();process=Preflight.Capture(exe,"hang",250);watch.Stop();Check(process.TimedOut && watch.ElapsedMilliseconds<4000,"process timeout not bounded");
  process=Preflight.Capture(exe,"flood",3000);Check(process.Truncated && process.Output.Length<=65538,"diagnostic output unbounded");
  Console.WriteLine("{\"passed\":true,\"cases\":"+cases+"}");return 0;
 }
}
