using System;
using System.Collections.ObjectModel;

namespace Hd820GameBridge {
 internal sealed class ChannelMeterSnapshot {
  public const double FloorDbFs=-72.0;
  public readonly ReadOnlyCollection<double> PeakDbFs,RmsDbFs;
  public readonly long UpdatedAtUtcTicks;
  public readonly int Frames;
  public ChannelMeterSnapshot(double[] peaks,double[] rms,long updatedAtUtcTicks,int frames){
   if(peaks==null || rms==null || peaks.Length!=6 || rms.Length!=6)throw new ArgumentException("电平快照必须包含六个声道。");
   var p=new double[6];var r=new double[6];for(int i=0;i<6;i++){p[i]=Clamp(peaks[i]);r[i]=Clamp(rms[i]);}
   PeakDbFs=Array.AsReadOnly(p);RmsDbFs=Array.AsReadOnly(r);UpdatedAtUtcTicks=updatedAtUtcTicks;Frames=frames;
  }
  static double Clamp(double db){return Double.IsNaN(db)||Double.IsInfinity(db)?FloorDbFs:Math.Max(FloorDbFs,Math.Min(0,db));}
  public bool IsFresh(long utcTicks){return UpdatedAtUtcTicks>0 && utcTicks>=UpdatedAtUtcTicks && utcTicks-UpdatedAtUtcTicks<=TimeSpan.TicksPerMillisecond*500;}
 }
 // One audio producer accumulates exact 50 ms windows. Published snapshots are never mutated.
 internal sealed class ChannelMeter {
  const int Channels=6,WindowFrames=2400;
  readonly double[] sums=new double[Channels],peaks=new double[Channels];
  int frames;
  volatile ChannelMeterSnapshot snapshot;
  public ChannelMeterSnapshot Snapshot {get{return snapshot;}}
  public void Process(float[] samples){Process(samples,0);}
  internal void Process(float[] samples,long fixedUtcTicks){
   if(samples==null || samples.Length%Channels!=0)throw new ArgumentException("电平数据必须为六声道交错PCM。");
   for(int at=0;at<samples.Length;at+=Channels){
    for(int ch=0;ch<Channels;ch++){
     double value=samples[at+ch];if(Double.IsNaN(value)||Double.IsInfinity(value))value=0;
     sums[ch]+=value*value;peaks[ch]=Math.Max(peaks[ch],Math.Abs(value));
    }
    if(++frames==WindowFrames){
     var p=new double[Channels];var r=new double[Channels];
     for(int ch=0;ch<Channels;ch++){p[ch]=Db(peaks[ch]);r[ch]=Db(Math.Sqrt(sums[ch]/frames));sums[ch]=peaks[ch]=0;}
     snapshot=new ChannelMeterSnapshot(p,r,fixedUtcTicks==0?DateTime.UtcNow.Ticks:fixedUtcTicks,frames);frames=0;
    }
   }
  }
  static double Db(double value){return value<=0?ChannelMeterSnapshot.FloorDbFs:Math.Max(ChannelMeterSnapshot.FloorDbFs,Math.Min(0,20*Math.Log10(value)));}
 }
}
