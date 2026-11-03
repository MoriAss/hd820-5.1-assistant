using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Hd820GameBridge {
 internal static class RuntimeConfig {
  internal static string FindFfmpeg(string root,string searchPath) {
   string configured=Path.Combine(root,"ffmpeg-path.txt");
   if(File.Exists(configured)) {
    string value=File.ReadAllText(configured).Trim().Trim('"');
    if(value.Length>0) {
     string resolved=Path.GetFullPath(Path.IsPathRooted(value)?value:Path.Combine(root,value));
     if(File.Exists(resolved))return resolved;
     throw new FileNotFoundException("ffmpeg-path.txt 指定的FFmpeg不存在，请更新该文件或放入应用bin目录。",resolved);
    }
   }
   foreach(string candidate in new[]{Path.Combine(root,"ffmpeg.exe"),Path.Combine(root,"bin","ffmpeg.exe")})if(File.Exists(candidate))return Path.GetFullPath(candidate);
   foreach(string part in (searchPath??"").Split(Path.PathSeparator)) {
    if(String.IsNullOrWhiteSpace(part))continue;
    try{string folder=part.Trim().Trim('"');if(!Path.IsPathRooted(folder))continue;string candidate=Path.Combine(folder,"ffmpeg.exe");if(File.Exists(candidate))return Path.GetFullPath(candidate);}catch(ArgumentException){}catch(NotSupportedException){}
   }
   throw new FileNotFoundException("未找到FFmpeg。请将ffmpeg.exe放在应用目录/bin目录，或在ffmpeg-path.txt中填写路径，或加入PATH。");
  }
  internal static bool IsSource(AudioEndpoint endpoint){return endpoint!=null && endpoint.State==1 && endpoint.Mix!=null && endpoint.Mix.Rate==48000 && ChannelMapper.IsSupportedLayout(endpoint.Mix) && (endpoint.Interface??"").IndexOf("Voicemeeter",StringComparison.OrdinalIgnoreCase)>=0;}
  internal static bool IsOutput(AudioEndpoint endpoint){
   if(endpoint==null || endpoint.State!=1)return false;
   string name=endpoint.Name??"",hardware=endpoint.Interface??"";
   bool digital=name.IndexOf("数字",StringComparison.OrdinalIgnoreCase)>=0 || name.IndexOf("Digital",StringComparison.OrdinalIgnoreCase)>=0 || name.IndexOf("S/PDIF",StringComparison.OrdinalIgnoreCase)>=0 || name.IndexOf("SPDIF",StringComparison.OrdinalIgnoreCase)>=0;
   return digital && (hardware.IndexOf("USB",StringComparison.OrdinalIgnoreCase)>=0 || hardware.IndexOf("HD820",StringComparison.OrdinalIgnoreCase)>=0);
  }
  internal static AudioEndpoint Select(IEnumerable<AudioEndpoint> endpoints,bool source,string preferred) {
   var candidates=endpoints.Where(source?(Func<AudioEndpoint,bool>)IsSource:IsOutput).ToArray();
   var saved=candidates.FirstOrDefault(e=>String.Equals(e.Id,preferred,StringComparison.OrdinalIgnoreCase));
   // A disconnected saved device must never silently fall back to another USB interface.
   return saved ?? (String.IsNullOrWhiteSpace(preferred) && candidates.Length==1?candidates[0]:null);
  }
  internal static string[] Preferences(string root){string p=Path.Combine(root,"bass-settings.txt");return File.Exists(p)?File.ReadAllLines(p):new string[0];}
  internal static AudioEndpoint ResolveEndpoint(bool source) {
   string[] preferences=Preferences(Program.Root);string preferred=preferences.Length>=6?preferences[source?4:5]:null;
   var selected=Select(AudioApi.Endpoints(),source,preferred);
   if(selected==null)throw new InvalidOperationException(source?"未找到唯一48kHz六声道Voicemeeter入口，请先在控制面板选择并保存入口。":"未找到唯一USB数字输出，请先在控制面板明确选择HD820数字输出并保存；不会向未选定设备发送AC3。");
   return selected;
  }
  internal static void AtomicWrite(string path,string text) {
   string temporary=path+"."+Guid.NewGuid().ToString("N")+".tmp";
   try{File.WriteAllText(temporary,text,new UTF8Encoding(false));if(File.Exists(path))File.Replace(temporary,path,null);else File.Move(temporary,path);}
   finally{if(File.Exists(temporary))try{File.Delete(temporary);}catch{}}
  }
 }
}
