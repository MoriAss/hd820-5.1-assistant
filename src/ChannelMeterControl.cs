using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Hd820GameBridge {
 internal sealed class ChannelMeterControl : Control {
  static readonly string[] Names={"左前 FL","右前 FR","中置 C","低音 LFE","左环绕 SL","右环绕 SR"};
  readonly double[] rms=new double[6],peak=new double[6],hold=new double[6];
  readonly long[] holdUntil=new long[6];
  long lastUpdate;
  bool fresh,hasSignal;
  public ChannelMeterControl(){
   SetStyle(ControlStyles.AllPaintingInWmPaint|ControlStyles.UserPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.ResizeRedraw,true);
   BackColor=Color.White;ForeColor=Color.FromArgb(30,41,59);Font=new Font("Microsoft YaHei UI",9f);
   AccessibleName="六声道实时电平";AccessibleDescription="六列编码前PCM电平。实色为RMS，蓝线为峰值，橙线为峰值保持。单位dBFS，不表示音箱实际声压。";
   ResetLevels();
  }
  void ResetLevels(){for(int i=0;i<6;i++){rms[i]=peak[i]=hold[i]=ChannelMeterSnapshot.FloorDbFs;holdUntil[i]=0;}hasSignal=false;}
  public void SetSnapshot(ChannelMeterSnapshot value){SetSnapshot(value,DateTime.UtcNow.Ticks);}
  internal void SetSnapshot(ChannelMeterSnapshot value,long now){
   double seconds=lastUpdate==0?0.05:Math.Max(0,Math.Min(0.5,(now-lastUpdate)/(double)TimeSpan.TicksPerSecond));lastUpdate=now;
   fresh=value!=null && value.IsFresh(now);hasSignal=false;
   if(!fresh)ResetLevels();
   else for(int i=0;i<6;i++){
    double p=value.PeakDbFs[i],r=value.RmsDbFs[i];hasSignal|=p>ChannelMeterSnapshot.FloorDbFs+0.1;
    if(p<=ChannelMeterSnapshot.FloorDbFs+0.1){rms[i]=peak[i]=hold[i]=ChannelMeterSnapshot.FloorDbFs;holdUntil[i]=0;continue;}
    rms[i]=Math.Max(r,rms[i]-30*seconds);peak[i]=Math.Max(p,peak[i]-48*seconds);
    if(p>=hold[i]){hold[i]=p;holdUntil[i]=now+TimeSpan.TicksPerMillisecond*1200;}
    else if(now>holdUntil[i])hold[i]=Math.Max(p,hold[i]-18*seconds);
   }
   Invalidate();
  }
  internal bool HasFreshSignal {get{return fresh && hasSignal;}}
  internal double DisplayedRms(int channel){return rms[channel];}
  protected override void OnPaint(PaintEventArgs e){
   base.OnPaint(e);var g=e.Graphics;g.SmoothingMode=SmoothingMode.AntiAlias;
   var bounds=new Rectangle(0,0,Math.Max(1,Width-1),Math.Max(1,Height-1));
   using(var border=new Pen(Color.FromArgb(226,232,240)))g.DrawRectangle(border,bounds);
   float scale=DeviceDpi/96f;
   using(var title=new Font(Font.FontFamily,10f,FontStyle.Bold))
   using(var small=new Font(Font.FontFamily,8f))
   using(var ink=new SolidBrush(ForeColor))
   using(var muted=new SolidBrush(Color.FromArgb(100,116,139)))
   using(var track=new SolidBrush(Color.FromArgb(241,245,249)))
   using(var green=new SolidBrush(Color.FromArgb(34,160,107)))
   using(var amber=new SolidBrush(Color.FromArgb(226,163,48)))
   using(var red=new SolidBrush(Color.FromArgb(224,78,83)))
   using(var grid=new Pen(Color.FromArgb(230,235,241)))
   using(var peakPen=new Pen(Color.FromArgb(58,120,219),2*scale))
   using(var holdPen=new Pen(Color.FromArgb(218,142,37),2*scale)){
    g.DrawString("六声道实时电平",title,ink,12*scale,8*scale);
    string state=!fresh?"等待音频":hasSignal?"正在接收":"静音";
    SizeF stateSize=g.MeasureString(state,small);g.DrawString(state,small,muted,Width-12*scale-stateSize.Width,11*scale);
    g.DrawString("RMS 实色  ·  峰值蓝线  ·  峰值保持橙线  ·  编码前 dBFS",small,muted,12*scale,32*scale);
    float top=58*scale,bottom=Height-43*scale,left=42*scale,right=Width-14*scale;
    if(bottom<=top || right<=left)return;
    float plotH=bottom-top,colW=(right-left)/6f,barW=Math.Min(34*scale,colW*0.42f);
    int[] ticks={0,-12,-24,-36,-48,-60,-72};
    foreach(int db in ticks){float y=Y(db,top,bottom);g.DrawLine(grid,left,y,right,y);g.DrawString(db.ToString(),small,muted,7*scale,y-7*scale);}
    for(int i=0;i<6;i++){
     float center=left+colW*(i+0.5f),x=center-barW/2;
     g.FillRectangle(track,x,top,barW,plotH);
     float yRms=Y(rms[i],top,bottom);FillRange(g,green,x,barW,yRms,bottom,Y(-12,top,bottom),bottom);
     FillRange(g,amber,x,barW,yRms,bottom,Y(-6,top,bottom),Y(-12,top,bottom));
     FillRange(g,red,x,barW,yRms,bottom,top,Y(-6,top,bottom));
     if(peak[i]>ChannelMeterSnapshot.FloorDbFs+0.1){float y=Y(peak[i],top,bottom);g.DrawLine(peakPen,x-2*scale,y,x+barW+2*scale,y);}
     if(hold[i]>ChannelMeterSnapshot.FloorDbFs+0.1){float y=Y(hold[i],top,bottom);g.DrawLine(holdPen,x-4*scale,y,x+barW+4*scale,y);}
     string reading=rms[i]<=ChannelMeterSnapshot.FloorDbFs+0.1?"−∞":rms[i].ToString("F1");
     DrawCentered(g,reading,small,ink,center,bottom+4*scale);
     DrawCentered(g,Names[i],small,muted,center,bottom+22*scale);
    }
   }
  }
  static void FillRange(Graphics g,Brush b,float x,float width,float start,float end,float rangeStart,float rangeEnd){float y=Math.Max(start,rangeStart),bottom=Math.Min(end,rangeEnd);if(bottom>y)g.FillRectangle(b,x,y,width,bottom-y);}
  static float Y(double db,float top,float bottom){return bottom-(float)((Math.Max(ChannelMeterSnapshot.FloorDbFs,Math.Min(0,db))-ChannelMeterSnapshot.FloorDbFs)/-ChannelMeterSnapshot.FloorDbFs)*(bottom-top);}
  static void DrawCentered(Graphics g,string text,Font font,Brush brush,float center,float top){var size=g.MeasureString(text,font);g.DrawString(text,font,brush,center-size.Width/2,top);}
 }
}
