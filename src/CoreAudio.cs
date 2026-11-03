using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Hd820GameBridge {
 [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] internal class MMEnumerator {}
 [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] internal interface IMMEnumerator {
  [PreserveSig] int EnumAudioEndpoints(int flow,uint mask,out IMMCollection collection);
  [PreserveSig] int GetDefaultAudioEndpoint(int flow,int role,out IMMDevice device);
  [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id,out IMMDevice device);
 }
 [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] internal interface IMMCollection {
  [PreserveSig] int GetCount(out uint count);
  [PreserveSig] int Item(uint index,out IMMDevice device);
 }
 [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] internal interface IMMDevice {
  [PreserveSig] int Activate(ref Guid iid,uint context,IntPtr parameters,[MarshalAs(UnmanagedType.IUnknown)] out object instance);
  [PreserveSig] int OpenPropertyStore(uint access,out IntPtr store);
  [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
  [PreserveSig] int GetState(out uint state);
 }
 [ComImport, Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] internal interface IAudioClient {
  [PreserveSig] int Initialize(int mode,uint flags,long duration,long periodicity,IntPtr format,IntPtr sessionGuid);
  [PreserveSig] int GetBufferSize(out uint frames);
  [PreserveSig] int GetStreamLatency(out long latency);
  [PreserveSig] int GetCurrentPadding(out uint frames);
  [PreserveSig] int IsFormatSupported(int mode,IntPtr format,out IntPtr closest);
  [PreserveSig] int GetMixFormat(out IntPtr format);
  [PreserveSig] int GetDevicePeriod(out long normal,out long minimum);
  [PreserveSig] int Start();
  [PreserveSig] int Stop();
  [PreserveSig] int Reset();
  [PreserveSig] int SetEventHandle(IntPtr handle);
  [PreserveSig] int GetService(ref Guid iid,[MarshalAs(UnmanagedType.IUnknown)] out object instance);
 }
 [ComImport, Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] internal interface IAudioCaptureClient {
  [PreserveSig] int GetBuffer(out IntPtr data,out uint frames,out uint flags,out ulong devicePosition,out ulong qpcPosition);
  [PreserveSig] int ReleaseBuffer(uint frames);
  [PreserveSig] int GetNextPacketSize(out uint frames);
 }
 [ComImport, Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] internal interface IAudioRenderClient {
  [PreserveSig] int GetBuffer(uint frames,out IntPtr data);
  [PreserveSig] int ReleaseBuffer(uint frames,uint flags);
 }
 internal sealed class AudioFormat {
  internal static readonly Guid FloatSubformat=new Guid("00000003-0000-0010-8000-00aa00389b71");
  internal static readonly Guid PcmSubformat=new Guid("00000001-0000-0010-8000-00aa00389b71");
  public int Tag,Channels,Rate,Bits,BlockAlign,ValidBits;
  public uint Mask;
  public Guid Subformat;
  public byte[] Bytes;
  public bool Floating { get { return Tag==3 || (Tag==65534 && Subformat==FloatSubformat); } }
  public static AudioFormat Read(IntPtr p) {
   if(p==IntPtr.Zero)throw new InvalidOperationException("音频驱动未返回有效格式。");
   int size=18+(ushort)Marshal.ReadInt16(p,16);
   if(size<18 || size>256)throw new InvalidOperationException("无效WAVEFORMAT长度");
   var f=new AudioFormat{Tag=(ushort)Marshal.ReadInt16(p,0),Channels=(ushort)Marshal.ReadInt16(p,2),Rate=Marshal.ReadInt32(p,4),BlockAlign=(ushort)Marshal.ReadInt16(p,12),Bits=(ushort)Marshal.ReadInt16(p,14),Bytes=new byte[size]};
   Marshal.Copy(p,f.Bytes,0,size);
   if(f.Tag==65534 && size>=40){f.ValidBits=(ushort)Marshal.ReadInt16(p,18);f.Mask=unchecked((uint)Marshal.ReadInt32(p,20));f.Subformat=(Guid)Marshal.PtrToStructure(IntPtr.Add(p,24),typeof(Guid));}
   return f;
  }
  public static AudioFormat Float(int channels,uint mask) {
   var f=new AudioFormat{Tag=65534,Channels=channels,Rate=48000,BlockAlign=channels*4,Bits=32,ValidBits=32,Mask=mask,Subformat=new Guid("00000003-0000-0010-8000-00aa00389b71"),Bytes=new byte[40]};
   f.Put16(0,65534);f.Put16(2,channels);f.Put32(4,48000);f.Put32(8,48000*f.BlockAlign);f.Put16(12,f.BlockAlign);f.Put16(14,32);f.Put16(16,22);f.Put16(18,32);f.Put32(20,unchecked((int)mask));Array.Copy(f.Subformat.ToByteArray(),0,f.Bytes,24,16);return f;
  }
  public static AudioFormat AC3() {
   var f=new AudioFormat{Tag=0x92,Channels=2,Rate=48000,BlockAlign=4,Bits=16,Bytes=new byte[18]};
   f.Put16(0,0x92);f.Put16(2,2);f.Put32(4,48000);f.Put32(8,192000);f.Put16(12,4);f.Put16(14,16);return f;
  }
  void Put16(int at,int value){Array.Copy(BitConverter.GetBytes((ushort)value),0,Bytes,at,2);}
  void Put32(int at,int value){Array.Copy(BitConverter.GetBytes(value),0,Bytes,at,4);}
  public IntPtr Allocate(){IntPtr p=Marshal.AllocCoTaskMem(Bytes.Length);Marshal.Copy(Bytes,0,p,Bytes.Length);return p;}
  public override string ToString(){return Channels+"声道 / "+Rate+"Hz / "+Bits+"bit / "+(Floating?"Float":"PCM")+" / mask 0x"+Mask.ToString("X");}
 }
 internal sealed class AudioEndpoint {
  public string Id,Name,Interface;
  public uint State;
  public bool DefaultConsole,DefaultMultimedia;
  public AudioFormat Mix;
  public override string ToString(){return Name+"（"+Interface+"）"+(State==1?"":" [已停用/未连接]");}
 }
 internal static class AudioApi {
  public static void Check(int hr,string action){if(hr<0)throw new COMException(action+"失败（0x"+unchecked((uint)hr).ToString("X8")+"）",hr);}
  public static void Release(object obj){if(obj!=null && Marshal.IsComObject(obj))Marshal.ReleaseComObject(obj);}
  public static T Activate<T>(IMMDevice d){Guid g=typeof(T).GUID;object o;Check(d.Activate(ref g,1,IntPtr.Zero,out o),"打开音频接口");return (T)o;}
  public static T Service<T>(IAudioClient a){Guid g=typeof(T).GUID;object o;Check(a.GetService(ref g,out o),"打开音频缓冲接口");return (T)o;}
  public static string DefaultId(int role){var e=(IMMEnumerator)new MMEnumerator();IMMDevice d=null;try{Check(e.GetDefaultAudioEndpoint(0,role,out d),"读取默认音频设备");string id;d.GetId(out id);return id;}finally{Release(d);Release(e);}}
  public static IMMDevice Device(string id){var e=(IMMEnumerator)new MMEnumerator();try{IMMDevice d;Check(e.GetDevice(id,out d),"找到音频设备");return d;}finally{Release(e);}}
  public static List<AudioEndpoint> Endpoints(){
   var list=new List<AudioEndpoint>();var e=(IMMEnumerator)new MMEnumerator();IMMCollection c=null;var defaults=new string[2];
   for(int role=0;role<2;role++){IMMDevice d=null;try{if(e.GetDefaultAudioEndpoint(0,role,out d)==0)d.GetId(out defaults[role]);}finally{Release(d);}}
   try{Check(e.EnumAudioEndpoints(0,15,out c),"枚举音频设备");uint n;c.GetCount(out n);for(uint i=0;i<n;i++){
    IMMDevice d=null;IAudioClient a=null;
    try{c.Item(i,out d);var ep=new AudioEndpoint();d.GetId(out ep.Id);d.GetState(out ep.State);ep.DefaultConsole=ep.Id==defaults[0];ep.DefaultMultimedia=ep.Id==defaults[1];
     string guid=ep.Id.Substring(ep.Id.LastIndexOf('.')+1);
     using(var key=Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render\"+guid+@"\Properties")){
      ep.Name=key==null?ep.Id:Convert.ToString(key.GetValue("{a45c254e-df1c-4efd-8020-67d146a850e0},2",ep.Id));ep.Interface=key==null?"":Convert.ToString(key.GetValue("{b3f8fa53-0004-438e-9003-51a46e139bfc},6",""));
     }
     if(ep.State==1){try{a=Activate<IAudioClient>(d);IntPtr p;Check(a.GetMixFormat(out p),"读取音频格式");try{ep.Mix=AudioFormat.Read(p);}finally{Marshal.FreeCoTaskMem(p);}}catch(COMException){}}
     list.Add(ep);
    }finally{Release(a);Release(d);}
   }}finally{Release(c);Release(e);}return list;
  }
 }
 internal static class ChannelMapper {
  // Windows channel order follows ascending speaker mask bits. AC3 input order here is FL FR FC LFE BL BR.
  public static bool IsSupportedLayout(AudioFormat format){return format!=null && ((format.Channels==6 && (format.Mask==0x3F || format.Mask==0x60F)) || (format.Channels==8 && format.Mask==0x63F));}
  public static float[] ConvertPacket(byte[] bytes,int frames,AudioFormat format,float gain){
   if(frames<0)throw new ArgumentOutOfRangeException("frames");
   var output=new float[checked(frames*6)];ConvertPacket(bytes,frames,format,gain,output);return output;
  }
  internal static int ConvertPacket(byte[] bytes,int frames,AudioFormat format,float gain,float[] output){
   if(bytes==null)throw new ArgumentNullException("bytes");
   if(format==null)throw new ArgumentNullException("format");
   if(frames<0)throw new ArgumentOutOfRangeException("frames");
   int sampleCount=checked(frames*6);
   if(output==null || output.Length<sampleCount)throw new ArgumentException("输出缓冲不足以包含完整六声道帧。","output");
   if(format.Channels<6 || format.Channels>8 || format.Rate!=48000)throw new InvalidOperationException("游戏入口需要48kHz的5.1或7.1格式。");
   if(!IsSupportedLayout(format))throw new InvalidOperationException("仅支持标准5.1（0x3F/0x60F）或7.1（0x63F）声道布局；不对6.1或宽声场布局静默丢弃声道。");
   // The three accepted masks have the same first six positions. In 7.1 the
   // side pair is at indices 6/7; combine it with the back pair at 4/5.
   // Validate once per packet instead of rebuilding four channel maps and
   // reparsing the floating-point GUID for every sample on the audio thread.
   int size=format.Bits/8;bool floating=format.Floating;
   if(format.Bits!=16 && format.Bits!=24 && format.Bits!=32)throw new InvalidOperationException("不支持此PCM位深。");
   if(format.BlockAlign!=format.Channels*size)throw new InvalidOperationException("不支持此PCM对齐方式。");
   if(!floating && format.Tag!=1 && (format.Tag!=65534 || format.Subformat!=AudioFormat.PcmSubformat))throw new InvalidOperationException("不支持此游戏入口编码。");
   if(floating && format.Bits!=32)throw new InvalidOperationException("仅支持32bit浮点。");
   if(bytes.Length<checked(frames*format.BlockAlign))throw new ArgumentException("PCM数据不足以包含声明的完整帧。","bytes");
   for(int f=0;f<frames;f++)for(int t=0;t<6;t++){
    int at=f*format.BlockAlign;double value=ReadSample(bytes,at+t*size,format.Bits,floating);
    if(Double.IsNaN(value)||Double.IsInfinity(value))value=0;
    if(format.Channels==8 && t>=4){double sideValue=ReadSample(bytes,at+(t+2)*size,format.Bits,floating);if(Double.IsNaN(sideValue)||Double.IsInfinity(sideValue))sideValue=0;value=(value+sideValue)*0.5;}
    value*=gain;if(Double.IsNaN(value)||Double.IsInfinity(value))value=0;output[f*6+t]=(float)Math.Max(-1.0,Math.Min(1.0,value));
   }return sampleCount;
  }
  static float ReadSample(byte[] bytes,int at,int bits,bool floating){
   if(floating)return BitConverter.ToSingle(bytes,at);
   if(bits==16)return BitConverter.ToInt16(bytes,at)/32768f;
   if(bits==24){int value=bytes[at]|(bytes[at+1]<<8)|(bytes[at+2]<<16);if((value&0x800000)!=0)value|=unchecked((int)0xFF000000);return value/8388608f;}
   if(bits==32)return BitConverter.ToInt32(bytes,at)/2147483648f;
   throw new InvalidOperationException("不支持此PCM位深。");
  }
 }
}
