using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace Hd820GameBridge {
 internal static class VirtualStimulus {
  // Synthetic proof of the virtual six-channel input. This never sends raw PCM to the HD820 device.
  public static void Play(string id,int seconds){
   IMMDevice device=null;IAudioClient client=null;IAudioRenderClient render=null;
   try{
    device=AudioApi.Device(id);client=AudioApi.Activate<IAudioClient>(device);IntPtr p;AudioApi.Check(client.GetMixFormat(out p),"读取测试入口格式");AudioFormat format;
    try{format=AudioFormat.Read(p);if(format.Rate!=48000 || format.Channels!=6 || !format.Floating || format.Bits!=32)throw new InvalidOperationException("虚拟测试需要48kHz六声道Float32。");AudioApi.Check(client.Initialize(0,0,1000000,0,p,IntPtr.Zero),"初始化虚拟六声道测试");}finally{Marshal.FreeCoTaskMem(p);}
    render=AudioApi.Service<IAudioRenderClient>(client);uint capacity;client.GetBufferSize(out capacity);long position=0;
    Feed(render,format,capacity,ref position);AudioApi.Check(client.Start(),"启动虚拟测试");var watch=Stopwatch.StartNew();
    while(watch.Elapsed.TotalSeconds<seconds){uint padding;AudioApi.Check(client.GetCurrentPadding(out padding),"虚拟测试时钟");if(padding<capacity)Feed(render,format,capacity-padding,ref position);Thread.Sleep(2);}
   }finally{if(client!=null)client.Stop();AudioApi.Release(render);AudioApi.Release(client);AudioApi.Release(device);}
  }
  static void Feed(IAudioRenderClient render,AudioFormat format,uint frames,ref long position){
   float[] samples=new float[frames*6];double[] frequencies={401,503,607,61,809,907};
   for(int i=0;i<frames;i++){long frame=position+i;int channel=(int)((frame/48000)%6);double local=(frame%48000)/48000.0;double envelope=Math.Min(1,local/0.03)*Math.Min(1,(1-local)/0.03);samples[i*6+channel]=(float)(0.0158489*Math.Max(0,envelope)*Math.Sin(2*Math.PI*frequencies[channel]*frame/48000.0));}
   IntPtr p;AudioApi.Check(render.GetBuffer(frames,out p),"取得虚拟六声道测试缓冲");try{Marshal.Copy(samples,0,p,samples.Length);}finally{AudioApi.Check(render.ReleaseBuffer(frames,0),"提交虚拟六声道测试");}position+=frames;
  }
 }
}
