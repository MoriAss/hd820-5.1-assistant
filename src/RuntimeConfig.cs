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
   return saved ?? (source && String.IsNullOrWhiteSpace(preferred) && candidates.Length==1?candidates[0]:null);
  }
  internal static string PreferencesBackupPath(string root){return Path.Combine(root,"logs","preferences-last-good.txt");}
  internal static void ValidatePreferences(string[] lines){
   if(lines==null || lines.Length==0 || lines.Length==5 || lines.Length>64)throw new InvalidDataException("设置文件不完整。");
   int value;
   if(lines[0]!="0" && lines[0]!="1")throw new InvalidDataException("补充低音开关无效。");
   if(lines.Length>=2 && (!Int32.TryParse(lines[1],out value) || value<0 || value>100))throw new InvalidDataException("低音比例应为0到100。");
   if(lines.Length>=3 && lines[2]!="0" && lines[2]!="1")throw new InvalidDataException("FPS开关无效。");
   if(lines.Length>=4 && (!Int32.TryParse(lines[3],out value) || value<0 || value>100))throw new InvalidDataException("音量应为0到100。");
   if(lines.Length>=6 && (lines[4].Length>4096 || lines[5].Length>4096))throw new InvalidDataException("保存的设备标识过长。");
  }
  static string[] ReadPreferencesFile(string path){
   if(new FileInfo(path).Length>65536)throw new InvalidDataException("设置文件超过64KiB，已保留原文件。");
   var lines=File.ReadAllLines(path);ValidatePreferences(lines);return lines;
  }
  internal static string[] Preferences(string root){string warning;return Preferences(root,out warning);}
  internal static string[] Preferences(string root,out string warning){
   warning=null;string path=Path.Combine(root,"bass-settings.txt"),backup=PreferencesBackupPath(root);
   if(!File.Exists(path) && !File.Exists(backup))return new string[0];
   try{if(File.Exists(path))return ReadPreferencesFile(path);throw new FileNotFoundException("设置文件缺失。");}
   catch(Exception mainError){
    if(!(mainError is IOException) && !(mainError is InvalidDataException) && !(mainError is UnauthorizedAccessException))throw;
    try{var lines=ReadPreferencesFile(backup);warning="当前设置不可读，已载入最后有效备份；原文件未覆盖。";return lines;}
    catch(Exception backupError){if(!(backupError is IOException) && !(backupError is InvalidDataException) && !(backupError is UnauthorizedAccessException))throw;throw new IOException("无法读取已保存设置，请重新选择设备；原文件已保留。"+mainError.Message,mainError);}
   }
  }
  internal static void SavePreferences(string root,string[] lines){
   ValidatePreferences(lines);string path=Path.Combine(root,"bass-settings.txt");
   string text=String.Join(Environment.NewLine,lines)+Environment.NewLine;
   if(File.Exists(path)){
    bool valid=true;try{ReadPreferencesFile(path);}catch(InvalidDataException){valid=false;}
    Directory.CreateDirectory(Path.Combine(root,"logs"));
    if(valid){string old=File.ReadAllText(path);if(old==text)return;AtomicWrite(PreferencesBackupPath(root),old);}
    else File.Copy(path,Path.Combine(root,"logs","badprefs-"+Guid.NewGuid().ToString("N")+".txt"));
   }
   AtomicWrite(path,text);
  }
  internal static int TelemetryIntervalMilliseconds(bool foreground){
   string value=Environment.GetEnvironmentVariable(foreground?"HD820_TELEMETRY_FOREGROUND_MS":"HD820_TELEMETRY_BACKGROUND_MS");int milliseconds;
   return Int32.TryParse(value,out milliseconds)?Math.Max(1000,Math.Min(60000,milliseconds)):(foreground?2000:5000);
  }
  internal static AudioEndpoint ResolveEndpoint(bool source) {
   string[] preferences=Preferences(Program.Root);string preferred=preferences.Length>=6?preferences[source?4:5]:null;
   var selected=Select(AudioApi.Endpoints(),source,preferred);
   if(selected==null)throw new InvalidOperationException(source?"未找到唯一48kHz六声道Voicemeeter入口，请先在控制面板选择并保存入口。":"请先在控制面板明确选择并保存已确认的HD820数字输出；不会按通用USB设备名称自动发送AC3。");
   return selected;
  }
  internal static void AtomicWrite(string path,string text) {
   string directory=Path.GetDirectoryName(Path.GetFullPath(path));
   // Do not append a GUID to an already long destination name. Keep the
   // temporary sibling inside the legacy MAX_PATH budget as well.
   int randomCharacters=Math.Min(32,259-directory.Length-1-6);
   if(randomCharacters<4)throw new PathTooLongException("目录过深，无法安全创建原子写入临时文件；请把助手移至较短路径。");
   string temporary=null;FileStream stream=null;
   for(int attempt=0;attempt<16;attempt++){
    string candidate=Path.Combine(directory,".t"+Guid.NewGuid().ToString("N").Substring(0,randomCharacters)+".tmp");
    try{stream=new FileStream(candidate,FileMode.CreateNew,FileAccess.Write,FileShare.None);temporary=candidate;break;}
    catch(IOException error){if(error.HResult!=unchecked((int)0x80070050) && error.HResult!=unchecked((int)0x800700B7))throw;}
   }
   if(stream==null)throw new IOException("无法分配不重名的临时文件，原文件未改动。");
   try{using(stream)using(var writer=new StreamWriter(stream,new UTF8Encoding(false)))writer.Write(text);if(File.Exists(path))File.Replace(temporary,path,null);else File.Move(temporary,path);}
   finally{if(File.Exists(temporary))try{File.Delete(temporary);}catch{}}
  }
 }
}
