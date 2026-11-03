using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Web.Script.Serialization;
using Hd820GameBridge;

internal static class ClockSynchronizerHarness {
 static readonly List<object> results=new List<object>();
 static int checks;
 static void Check(bool condition,string message){checks++;if(!condition)throw new Exception(message);}
 static bool Finite(float x){return !Single.IsNaN(x)&&!Single.IsInfinity(x);}
 static double Phase(PcmClockSynchronizer c){return (double)typeof(PcmClockSynchronizer).GetField("nextPosition",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic).GetValue(c);}
 static void Unity(){
  var c=new PcmClockSynchronizer();int[] sizes={1,2,7,48,127,480,960,1536,2048};long total=0;
  for(int k=0;k<200;k++){
   int n=sizes[k%sizes.Length];var source=new float[n*6];for(int f=0;f<n;f++)for(int ch=0;ch<6;ch++)source[f*6+ch]=(float)(ch*.1+Math.Sin((f+total)*.02)*.05);
   var output=c.Process(source);Check(output.Length==source.Length,"unity count");
   for(int i=0;i<source.Length;i++)Check(source[i]==output[i],"unity samples");total+=n;
  }
  Check(c.TotalInputFrames==total && c.TotalOutputFrames==total,"unity cumulative counts");results.Add(new{Test="unity_bit_identical",Frames=total,Passed=true});
 }
 static void Boundaries(double ppm){
  var c=new PcmClockSynchronizer();c.SetCorrectionPpm(ppm);int[] sizes={1,127,480,960,1536,37,2048};long frames=0;double previous=-1,maximumGap=0,minimumGap=Double.MaxValue;long outputCount=0;
  for(int k=0;k<1200;k++){
   int n=sizes[k%sizes.Length];var source=new float[n*6];for(int f=0;f<n;f++)for(int ch=0;ch<6;ch++)source[f*6+ch]=(float)((frames+f)*1e-6+ch*.1);
   var output=c.Process(source);Check(output.Length%6==0,"interleave length");
   for(int f=0;f<output.Length/6;f++){
    double value=output[f*6];if(previous>=0){double gap=(value-previous)*1e6;maximumGap=Math.Max(maximumGap,gap);minimumGap=Math.Min(minimumGap,gap);Check(gap>.88&&gap<1.13,"boundary duplicate/gap "+gap);}
    previous=value;for(int ch=0;ch<6;ch++){float v=output[f*6+ch];Check(Finite(v),"finite output");Check(Math.Abs((v-value)-ch*.1)<3e-7,"channel isolation");}outputCount++;
   }
   frames+=n;Check(Phase(c)>-1.0000001&&Phase(c)<1.01,"phase outside boundary retention");
  }
  Check(ppm>0?outputCount<frames:outputCount>frames,"correction direction");
  results.Add(new{Test="fragment_boundaries",Ppm=ppm,InputFrames=frames,OutputFrames=outputCount,MinimumInputStep=minimumGap,MaximumInputStep=maximumGap,Passed=true});
 }
 static void Simulate(double driftPpm,int seconds,int seed){
  var c=new PcmClockSynchronizer();const double target=4608;double buffered=target,carry=0,minimum=target,maximum=target,lastMin=Double.MaxValue,lastMax=0,averageSum=0;int averageCount=0;
  int[] sizes={127,240,480,960,1536};double currentTime=0,controlTime=0;long generated=0;
  while(currentTime<seconds){
   int request=sizes[(seed++)%sizes.Length];double dt=request/48000.0;
   carry+=request*(1+driftPpm/1000000.0);int n=(int)carry;carry-=n;var block=new float[n*6];
   var output=c.Process(block);buffered+=output.Length/6.0-request;currentTime+=dt;generated+=n;
   minimum=Math.Min(minimum,buffered);maximum=Math.Max(maximum,buffered);
   averageSum+=buffered;averageCount++;controlTime+=dt;
   if(controlTime>=0.5){c.UpdateBuffer(averageSum/averageCount,target,controlTime);controlTime=0;averageSum=0;averageCount=0;}
   if(currentTime>seconds-60){lastMin=Math.Min(lastMin,buffered);lastMax=Math.Max(lastMax,buffered);}
   Check(Math.Abs(c.CorrectionPpm)<=PcmClockSynchronizer.MaximumCorrectionPpm+0.01,"unbounded correction");Check(buffered>1536 && buffered<7680,"buffer approached underrun/drop threshold");
  }
  Check(Math.Abs(c.CorrectionPpm-driftPpm)<8,"steady correction mismatch "+driftPpm+" vs "+c.CorrectionPpm);
  Check(lastMin>target-12 && lastMax<target+12,"steady queue error");
  results.Add(new{Test="clock_drift_closed_loop",DriftPpm=driftPpm,SimulatedSeconds=currentTime,InputFrames=generated,OutputFrames=c.TotalOutputFrames,MinimumBufferedMs=minimum/48,MaximumBufferedMs=maximum/48,LastMinuteMinimumMs=lastMin/48,LastMinuteMaximumMs=lastMax/48,FinalCorrectionPpm=c.CorrectionPpm,Passed=true});
 }
 static void ResetAndLimits(){
  var c=new PcmClockSynchronizer();c.SetCorrectionPpm(999999);Check(c.TargetCorrectionPpm==5000,"positive clamp");c.SetCorrectionPpm(-999999);Check(c.TargetCorrectionPpm==-5000,"negative clamp");
  c.Process(new float[480*6]);long input=c.TotalInputFrames,output=c.TotalOutputFrames;c.ResetController();Check(c.TotalInputFrames==input&&c.TotalOutputFrames==output,"controller reset phase/counters");
  c.Reset();Check(c.TotalInputFrames==0&&c.TotalOutputFrames==0&&c.CorrectionPpm==0,"full reset");
  bool rejected=false;try{c.Process(new float[7]);}catch(ArgumentException){rejected=true;}Check(rejected,"partial frame rejected");
  rejected=false;try{c.SetCorrectionPpm(Double.NaN);}catch(ArgumentOutOfRangeException){rejected=true;}Check(rejected,"NaN correction rejected");
  c.UpdateBuffer(Double.NaN,4608,.5);Check(c.TargetCorrectionPpm==0,"invalid telemetry ignored");
  results.Add(new{Test="limits_reset_invalid",Passed=true});
 }
 static void JitterAndDriftReversal(){
  var c=new PcmClockSynchronizer();const double target=4608;double buffered=target,carry=0,minimum=target,maximum=target,sum=0;int count=0;
  double finalMinimum=Double.MaxValue,finalMaximum=0;
  for(int k=0;k<120000;k++){
   double time=k*.01;double drift=time<600?1000:-1000;carry+=480*(1+drift/1000000.0);int n=(int)carry;carry-=n;
   buffered+=c.Process(new float[n*6]).Length/6.0-480;
   // Deliberately noisy telemetry: half an AC-3 frame of 32 ms periodic
   // sampling phase plus a smaller scheduling ripple. The actual PCM queue
   // remains separate from the measurement supplied to the controller.
   double measurement=buffered+768*Math.Sin(2*Math.PI*time/.032)+96*Math.Sin(2*Math.PI*time/.17);
   sum+=measurement;count++;if(count==50){c.UpdateBuffer(sum/count,target,.5);sum=0;count=0;}
   minimum=Math.Min(minimum,buffered);maximum=Math.Max(maximum,buffered);
   if(time>1140){finalMinimum=Math.Min(finalMinimum,buffered);finalMaximum=Math.Max(finalMaximum,buffered);}
   Check(buffered>1536&&buffered<7680,"jitter/reversal buffer escaped bounds");
  }
  Check(finalMinimum>target-16&&finalMaximum<target+16,"jitter steady queue error");
  Check(Math.Abs(c.CorrectionPpm+1000)<100,"jitter final correction mismatch");
  results.Add(new{Test="noisy_telemetry_and_drift_reversal",SimulatedSeconds=1200,NoisePeakFrames=864,MinimumBufferedMs=minimum/48,MaximumBufferedMs=maximum/48,LastMinuteMinimumMs=finalMinimum/48,LastMinuteMaximumMs=finalMaximum/48,FinalCorrectionPpm=c.CorrectionPpm,Passed=true});
 }
 static int Main(string[] args){
  var watch=Stopwatch.StartNew();string path=args.Length>0?args[0]:"clock-synchronizer-evidence.json";
  try{Unity();Boundaries(1000);Boundaries(-1000);ResetAndLimits();double[] ppm={-1000,-200,-50,50,200,1000};for(int i=0;i<ppm.Length;i++)Simulate(ppm[i],600,i);JitterAndDriftReversal();
   File.WriteAllText(path,new JavaScriptSerializer().Serialize(new{Passed=true,Checks=checks,ElapsedSeconds=watch.Elapsed.TotalSeconds,Results=results}));Console.WriteLine("PASS "+checks+" checks; "+watch.Elapsed.TotalSeconds.ToString("F2")+" s");return 0;
  }catch(Exception e){File.WriteAllText(path,new JavaScriptSerializer().Serialize(new{Passed=false,Checks=checks,ElapsedSeconds=watch.Elapsed.TotalSeconds,Results=results,Error=e.ToString()}));Console.Error.WriteLine(e);return 1;}
 }
}
