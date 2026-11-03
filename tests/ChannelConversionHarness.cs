using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using Hd820GameBridge;

internal static class ChannelConversionHarness {
 static int assertions;
 static void Check(bool condition,string label){if(!condition)throw new Exception(label);assertions++;}
 static float Read(byte[] b,int i,int bits,bool floating){
  if(floating)return BitConverter.ToSingle(b,i);
  if(bits==16)return BitConverter.ToInt16(b,i)/32768f;
  if(bits==24){int v=b[i]|(b[i+1]<<8)|(b[i+2]<<16);if(v>=0x800000)v-=0x1000000;return v/8388608f;}
  return BitConverter.ToInt32(b,i)/2147483648f;
 }
 static double Finite(double v){return Double.IsNaN(v)||Double.IsInfinity(v)?0:v;}
 static int Main(){
  var rng=new Random(338204);var all=new MemoryStream();
  foreach(uint mask in new uint[]{0x3F,0x60F,0x63F})foreach(int kind in new[]{0,1,2,3,4,5,6,7}){
   bool floating=kind>=6;int bits=floating?32:new[]{16,24,32}[kind%3];int channels=mask==0x63F?8:6;
   var f=AudioFormat.Float(channels,mask);f.Bits=bits;f.ValidBits=bits;f.BlockAlign=channels*(bits/8);f.Tag=kind<3?1:kind==7?3:65534;
   if(!floating)f.Subformat=new Guid("00000001-0000-0010-8000-00aa00389b71");
   var input=new byte[131*f.BlockAlign];rng.NextBytes(input);
   if(floating){var values=new float[131*channels];for(int i=0;i<values.Length;i++)values[i]=(float)(rng.NextDouble()*4-2);values[0]=float.NaN;values[1]=float.PositiveInfinity;values[2]=float.NegativeInfinity;values[3]=float.MaxValue;values[4]=-float.MaxValue;Buffer.BlockCopy(values,0,input,0,input.Length);}
   foreach(float gain in new[]{-0.5f,0f,0.7f,1f,2f,float.NaN,float.PositiveInfinity}){
    var actual=ChannelMapper.ConvertPacket(input,127,f,gain);Check(actual.Length==127*6,"packet frame count");
    for(int frame=0;frame<127;frame++)for(int channel=0;channel<6;channel++){
     double value=Finite(Read(input,frame*f.BlockAlign+channel*(bits/8),bits,floating));
     if(channels==8&&channel>=4)value=(value+Finite(Read(input,frame*f.BlockAlign+(channel+2)*(bits/8),bits,floating)))*0.5;
     float expected=(float)Math.Max(-1,Math.Min(1,Finite(value*gain)));
     Check(actual[frame*6+channel]==expected,"reference mismatch: "+kind+"/"+mask+"/"+channel);
    }
    var raw=new byte[actual.Length*4];Buffer.BlockCopy(actual,0,raw,0,raw.Length);all.Write(raw,0,raw.Length);
   }
  }
  foreach(uint mask in new uint[]{0,0x13F,0x63F,0xFF}){bool rejected=false;try{ChannelMapper.ConvertPacket(new byte[24],1,AudioFormat.Float(6,mask),1);}catch(InvalidOperationException){rejected=true;}Check(rejected,"invalid layout accepted");}
  bool shortRejected=false;try{ChannelMapper.ConvertPacket(new byte[23],1,AudioFormat.Float(6,0x60F),1);}catch(ArgumentException){shortRejected=true;}Check(shortRejected,"short packet accepted");
  var format=AudioFormat.Float(6,0x60F);var data=new byte[480*24];
  for(int i=0;i<100;i++)ChannelMapper.ConvertPacket(data,480,format,0.7f);
  var timer=Stopwatch.StartNew();for(int i=0;i<2000;i++)ChannelMapper.ConvertPacket(data,480,format,0.7f);timer.Stop();
  string hash;using(var sha=SHA256.Create())hash=BitConverter.ToString(sha.ComputeHash(all.ToArray())).Replace("-","");
  Console.WriteLine("{\"assertions\":"+assertions+",\"samplesSha256\":\""+hash+"\",\"benchmarkPackets\":2000,\"benchmarkMs\":"+timer.ElapsedMilliseconds+"}");return 0;
 }
}
