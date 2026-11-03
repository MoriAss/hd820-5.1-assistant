using System;

namespace Hd820GameBridge {
 // Capture and USB playback use independent oscillators. Correct their small
 // speed difference before AC-3 encoding instead of dropping whole 32 ms bursts.
 // One capture thread owns this object. No UI, files, waits, or audio APIs here.
 internal sealed class PcmClockSynchronizer {
  public const int Channels=6;
  public const int SampleRate=48000;
  public const double MaximumCorrectionPpm=5000;
  public const double SlewPpmPerSecond=500;
  const double ProportionalPpmPerFrame=4;
  const double IntegralPpmPerFrameSecond=0.25;
  const double BufferFilterSeconds=0.75;
  readonly float[] previous=new float[Channels];
  bool havePrevious,controllerReady;
  double nextPosition,step=1,targetPpm,integralPpm,filteredBuffer;
  long inputFrames,outputFrames;
  public long InputFrames {get{return inputFrames;}}
  public long OutputFrames {get{return outputFrames;}}
  public long TotalInputFrames {get{return inputFrames;}}
  public long TotalOutputFrames {get{return outputFrames;}}
  public double CorrectionPpm {get{return CurrentCorrectionPpm;}}
  public double CurrentCorrectionPpm {get{return (step-1)*1000000;}}
  public double TargetCorrectionPpm {get{return targetPpm;}}
  public double FilteredBufferFrames {get{return filteredBuffer;}}
  public double IntegralCorrectionPpm {get{return integralPpm;}}

  public void Reset(){
   havePrevious=controllerReady=false;nextPosition=0;step=1;
   targetPpm=integralPpm=filteredBuffer=0;inputFrames=outputFrames=0;
   Array.Clear(previous,0,previous.Length);
  }
  public void ResetController(){controllerReady=false;integralPpm=0;targetPpm=0;}
  static bool Finite(double value){return !Double.IsNaN(value) && !Double.IsInfinity(value);}
  static double Clamp(double value,double minimum,double maximum){return Math.Max(minimum,Math.Min(maximum,value));}
  // Positive correction consumes input slightly faster: a queue that is too
  // deep gets shorter. Negative correction produces slightly more output.
  public void SetCorrectionPpm(double value){
   if(!Finite(value))throw new ArgumentOutOfRangeException("value");
   targetPpm=Clamp(value,-MaximumCorrectionPpm,MaximumCorrectionPpm);
  }
  // bufferedFrames should include accepted encoder PCM, encoded queue, and
  // hardware padding, less audio already rendered. Ignore the startup interval.
  // durationSeconds is capture-clock elapsed time since the preceding update.
  public void UpdateBuffer(double bufferedFrames,double targetFrames,double durationSeconds){
   if(!Finite(bufferedFrames)||!Finite(targetFrames)||!Finite(durationSeconds) || targetFrames<0 || durationSeconds<=0)return;
   double dt=Math.Min(durationSeconds,1.0);
   if(!controllerReady){filteredBuffer=bufferedFrames;controllerReady=true;}
   double alpha=1-Math.Exp(-dt/BufferFilterSeconds);
   filteredBuffer+=alpha*(bufferedFrames-filteredBuffer);
   double error=filteredBuffer-targetFrames;
   double proposedIntegral=Clamp(integralPpm+error*IntegralPpmPerFrameSecond*dt,-MaximumCorrectionPpm,MaximumCorrectionPpm);
   double proposed=ProportionalPpmPerFrame*error+proposedIntegral;
   // Conditional integration prevents recovery being held back by windup after
   // a process stall, while still allowing an opposite error to unwind it.
   if(Math.Abs(proposed)<=MaximumCorrectionPpm || (proposed>MaximumCorrectionPpm && error<0) || (proposed< -MaximumCorrectionPpm && error>0))integralPpm=proposedIntegral;
   SetCorrectionPpm(ProportionalPpmPerFrame*error+integralPpm);
  }
  static float Clean(float sample){return Single.IsNaN(sample)||Single.IsInfinity(sample)?0:sample;}
  public float[] Process(float[] samples){
   if(samples==null)throw new ArgumentNullException("samples");
   if(samples.Length%Channels!=0)throw new ArgumentException("PCM requires complete six-channel frames.","samples");
   int frames=samples.Length/Channels;if(frames==0)return samples;
   // This also preserves bit-identical PCM when no correction is needed.
   if(nextPosition==0 && step==1 && targetPpm==0){
    float[] clean=samples;
    for(int i=0;i<samples.Length;i++)if(Single.IsNaN(samples[i]) || Single.IsInfinity(samples[i])){
     if(Object.ReferenceEquals(clean,samples))clean=(float[])samples.Clone();clean[i]=0;
    }
    for(int ch=0;ch<Channels;ch++)previous[ch]=clean[(frames-1)*Channels+ch];
    havePrevious=true;inputFrames+=frames;outputFrames+=frames;return clean;
   }
   int maximum=checked((int)Math.Ceiling((frames+2)/(1-MaximumCorrectionPpm/1000000.0))+2);
   var result=new float[checked(maximum*Channels)];int written=0;
   double desiredStep=1+targetPpm/1000000.0;
   double maximumStepChange=SlewPpmPerSecond/(1000000.0*SampleRate);
   while(nextPosition<=frames-1){
    int left=(int)Math.Floor(nextPosition);double fraction=nextPosition-left;
    if(left< -1 || (left<0 && !havePrevious))throw new InvalidOperationException("Resampler phase is outside its retained boundary frame.");
    if(fraction>0 && left+1>=frames)break;
    if(written>=maximum)throw new InvalidOperationException("Resampler output bound exceeded.");
    int destination=written*Channels;
    for(int ch=0;ch<Channels;ch++){
     float a=left<0?previous[ch]:Clean(samples[left*Channels+ch]);
     float b=fraction==0?a:Clean(samples[(left+1)*Channels+ch]);
     result[destination+ch]=(float)(a+((double)b-a)*fraction);
    }
    written++;
    step+=Clamp(desiredStep-step,-maximumStepChange,maximumStepChange);
    nextPosition+=step;
   }
   nextPosition-=frames;
   // A frame at the boundary can remain pending until the next packet arrives.
   // Retaining it and the fractional phase avoids duplicate/missing block edges.
   for(int ch=0;ch<Channels;ch++)previous[ch]=Clean(samples[(frames-1)*Channels+ch]);
   havePrevious=true;inputFrames+=frames;outputFrames+=written;
   if(result.Length!=written*Channels)Array.Resize(ref result,written*Channels);
   return result;
  }
 }
}
