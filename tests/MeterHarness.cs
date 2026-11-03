using System;
using System.Drawing;
using System.IO;
using System.Collections.Generic;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Hd820GameBridge;
internal static class MeterHarness {
 static readonly List<object> results=new List<object>();
 static void Check(string name,bool ok,object detail){results.Add(new{name=name,passed=ok,detail=detail});if(!ok)throw new Exception(name);}
 [STAThread] static int Main(){
  string root=AppDomain.CurrentDomain.BaseDirectory;long now=DateTime.UtcNow.Ticks;
  try{
   var meter=new ChannelMeter();var samples=new float[2400*6];double[] amplitudes={.5,.25,.125,.0625,.03125,.015625};
   for(int f=0;f<2400;f++)for(int ch=0;ch<6;ch++)samples[f*6+ch]=(float)(amplitudes[ch]*Math.Sin(2*Math.PI*1000*f/48000.0));
   meter.Process(samples,now);var snapshot=meter.Snapshot;
   bool correct=true;for(int ch=0;ch<6;ch++)correct &= Math.Abs(snapshot.RmsDbFs[ch]-20*Math.Log10(amplitudes[ch]/Math.Sqrt(2)))<.002 && Math.Abs(snapshot.PeakDbFs[ch]-20*Math.Log10(amplitudes[ch]))<.002;
   Check("six_sines_rms_and_peak",correct,new{rms=snapshot.RmsDbFs,peaks=snapshot.PeakDbFs});
   meter.Process(new float[2400*6],now+1);Check("silence_floor_and_old_snapshot_immutable",meter.Snapshot.RmsDbFs[0]==-72 && snapshot.RmsDbFs[0]>-10,new{silence=meter.Snapshot.RmsDbFs,previous=snapshot.RmsDbFs});
   var partial=new ChannelMeter();partial.Process(new float[1200*6],now);Check("partial_window_unpublished",partial.Snapshot==null,null);partial.Process(new float[1200*6],now);Check("partial_window_accumulates_2400_frames",partial.Snapshot.Frames==2400,null);
   for(int active=0;active<6;active++){var isolated=new ChannelMeter();var data=new float[2400*6];for(int f=0;f<2400;f++)data[f*6+active]=.25f;isolated.Process(data,now);bool independent=true;for(int ch=0;ch<6;ch++)independent &= ch==active?isolated.Snapshot.RmsDbFs[ch]>-13:isolated.Snapshot.RmsDbFs[ch]==-72;Check("isolated_channel_"+active,independent,isolated.Snapshot.RmsDbFs);}
   var nonFinite=new ChannelMeter();var bad=new float[2400*6];bad[0]=float.NaN;bad[1]=float.PositiveInfinity;nonFinite.Process(bad,now);Check("non_finite_samples_sanitized",nonFinite.Snapshot.RmsDbFs[0]==-72 && nonFinite.Snapshot.RmsDbFs[1]==-72,null);
   Check("fresh_500ms_boundary",snapshot.IsFresh(now+TimeSpan.TicksPerMillisecond*500) && !snapshot.IsFresh(now+TimeSpan.TicksPerMillisecond*501) && !snapshot.IsFresh(now-1),null);
   Application.EnableVisualStyles();using(var control=new ChannelMeterControl()){
    control.Size=new Size(820,220);control.CreateControl();control.SetSnapshot(snapshot,now);Check("ui_uses_independent_rms",control.HasFreshSignal && Math.Abs(control.DisplayedRms(0)-snapshot.RmsDbFs[0])<.002,null);
    using(var bitmap=new Bitmap(control.Width,control.Height)){control.DrawToBitmap(bitmap,control.ClientRectangle);bitmap.Save(Path.Combine(root,"meter-820.png"));}
    control.Size=new Size(670,220);using(var bitmap=new Bitmap(control.Width,control.Height)){control.DrawToBitmap(bitmap,control.ClientRectangle);bitmap.Save(Path.Combine(root,"meter-670.png"));}
    control.SetSnapshot(snapshot,now+TimeSpan.TicksPerMillisecond*501);bool expired=!control.HasFreshSignal;for(int ch=0;ch<6;ch++)expired &= control.DisplayedRms(ch)==-72;Check("stale_reading_clears_all_six_channels",expired,null);
    control.SetSnapshot(meter.Snapshot,now+2);Check("fresh_silence_clears_without_release_tail",!control.HasFreshSignal && control.DisplayedRms(0)==-72,null);
   }
   File.WriteAllText(Path.Combine(root,"meter-evidence.json"),new JavaScriptSerializer().Serialize(new{passed=true,cases=results}));return 0;
  }catch(Exception ex){File.WriteAllText(Path.Combine(root,"meter-evidence.json"),new JavaScriptSerializer().Serialize(new{passed=false,error=ex.ToString(),cases=results}));return 1;}
 }
}