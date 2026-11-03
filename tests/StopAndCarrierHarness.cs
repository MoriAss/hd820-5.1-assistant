using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using System.Web.Script.Serialization;

internal static class StopAndCarrierHarness {
 const BindingFlags Fields=BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public;
 static Type engineType;
 static ConstructorInfo constructor;
 static byte[] silence;
 static readonly List<string> assertions=new List<string>();
 static void Check(bool value,string label){if(!value)throw new InvalidOperationException(label);assertions.Add(label);}
 static object Create(){return constructor.Invoke(new object[]{"fixture-no-source","fixture-no-output","fixture-no-ffmpeg",silence,false});}
 static FieldInfo Field(string name){return engineType.GetField(name,Fields);}
 static void Dispose(object engine){try{((IDisposable)engine).Dispose();}catch(TargetInvocationException e){throw e.InnerException;}}
 static string Hash(string path){using(var file=File.OpenRead(path))using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(file)).Replace("-","");}
 static void Carrier(byte[] bytes,bool valid,string label){
  bool success=true;try{engineType.GetMethod("ValidateBurst",BindingFlags.Public|BindingFlags.Static).Invoke(null,new object[]{bytes});}
  catch(TargetInvocationException error){if(!(error.InnerException is InvalidDataException))throw;success=false;}
  Check(success==valid,label);
 }
 static int Main(string[] args){
  string status="passed",error=null;int exit=0;string hashBefore=null,hashAfter=null;
  try{
   if(args.Length!=2)throw new ArgumentException("Expected production EXE and report JSON paths.");
   string target=Path.GetFullPath(args[0]);hashBefore=Hash(target);
   engineType=Assembly.LoadFrom(target).GetType("Hd820GameBridge.BridgeEngine",true);
   constructor=engineType.GetConstructor(Fields,null,new[]{typeof(string),typeof(string),typeof(string),typeof(byte[]),typeof(bool)},null);
   silence=File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(target),"silence-ac3-640k.spdif"));
   Carrier(silence,true,"valid silent IEC61937 AC3 carrier accepted");
   Carrier(null,false,"null rejected");Carrier(new byte[0],false,"empty rejected");
   Carrier(new byte[6144],false,"raw zero PCM rejected");
   var badSync=(byte[])silence.Clone();badSync[8]=0;Carrier(badSync,false,"corrupt AC3 sync rejected");
   var badPreamble=(byte[])silence.Clone();badPreamble[0]=0;Carrier(badPreamble,false,"corrupt IEC preamble rejected");
   var badType=(byte[])silence.Clone();badType[4]=0;Carrier(badType,false,"non AC3 carrier type rejected");
   var badLength=(byte[])silence.Clone();badLength[6]=0;badLength[7]=0;Carrier(badLength,false,"zero payload length rejected");
   var hugeLength=(byte[])silence.Clone();hugeLength[6]=255;hugeLength[7]=255;Carrier(hugeLength,false,"oversized payload length rejected");
   object empty=Create();Dispose(empty);Dispose(empty);Check((bool)Field("disposed").GetValue(empty),"empty and repeated Dispose succeed");
   object unstarted=Create();Field("captureThread").SetValue(unstarted,new Thread(delegate(){}));Dispose(unstarted);Check((bool)Field("disposed").GetValue(unstarted),"unstarted worker Dispose succeeds");
   object delayed=Create();var worker=new Thread(delegate(){Thread.Sleep(4500);});worker.IsBackground=true;Field("captureThread").SetValue(delayed,worker);worker.Start();
   bool timeout=false;try{Dispose(delayed);}catch(TimeoutException){timeout=true;}
   Check(timeout,"live synthetic worker timeout reported");Check(!(bool)Field("disposed").GetValue(delayed),"timeout keeps engine undisposed");
   ((ManualResetEvent)Field("captureReady").GetValue(delayed)).WaitOne(0);Check(true,"timeout keeps wait handle usable");
   Check(worker.Join(2000),"synthetic worker eventually stops");Dispose(delayed);Dispose(delayed);Check((bool)Field("disposed").GetValue(delayed),"retry Dispose after timeout succeeds");
   object self=Create();Field("captureThread").SetValue(self,Thread.CurrentThread);bool selfRejected=false;
   try{engineType.GetMethod("Stop").Invoke(self,null);}catch(TargetInvocationException e){if(!(e.InnerException is TimeoutException))throw;selfRejected=true;}
   Check(selfRejected,"current thread not treated as already stopped");Field("captureThread").SetValue(self,null);Dispose(self);
   hashAfter=Hash(target);Check(hashBefore==hashAfter,"production binary unchanged");
  }catch(Exception e){status="failed";error=e.ToString();exit=1;}
  var report=new{status=status,checkedAtUtc=DateTime.UtcNow.ToString("o"),productionSha256=hashBefore,afterSha256=hashAfter,assertions=assertions,error=error,noAudioDeviceOpened=true,noProductionMainInvoked=true,noGlobalIpcInvoked=true};
  string json=new JavaScriptSerializer().Serialize(report);if(args.Length>1)File.WriteAllText(args[1],json);Console.WriteLine(json);return exit;
 }
}
