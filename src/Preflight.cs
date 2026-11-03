using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace Hd820GameBridge {
 // Reads endpoint metadata and launches FFmpeg only for capability listings.
 // Never opens an audio stream, changes routing, or sends a report anywhere.
 internal static class Preflight {
  internal sealed class ProcessResult {internal string Output;internal int ExitCode;internal bool TimedOut,Truncated;}
  internal static ProcessResult Capture(string executable,string arguments,int timeoutMs){
   var text=new StringBuilder();var gate=new object();var result=new ProcessResult{ExitCode=-1};
   using(var process=new Process()){
    process.StartInfo=new ProcessStartInfo(executable,arguments){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
    Action<StreamReader> drain=delegate(StreamReader reader){
     var block=new char[4096];
     try{int count;while((count=reader.Read(block,0,block.Length))>0){lock(gate){int take=Math.Min(count,Math.Max(0,65536-text.Length));if(take>0)text.Append(block,0,take);if(take<count)result.Truncated=true;}}}
     catch(IOException){}catch(ObjectDisposedException){}
    };
    process.Start();
    var outputReader=process.StandardOutput;var errorReader=process.StandardError;
    var stdout=new Thread(delegate(){drain(outputReader);}){IsBackground=true,Name="HD820 diagnostic stdout"};
    var stderr=new Thread(delegate(){drain(errorReader);}){IsBackground=true,Name="HD820 diagnostic stderr"};
    stdout.Start();stderr.Start();
    if(!process.WaitForExit(timeoutMs)){result.TimedOut=true;try{process.Kill();}catch(InvalidOperationException){}process.WaitForExit(500);}
    if(process.HasExited)result.ExitCode=process.ExitCode;
    bool outputEnded=stdout.Join(500),errorEnded=stderr.Join(500);
    lock(gate){if(!outputEnded||!errorEnded)result.Truncated=true;result.Output=text.ToString();}return result;
   }
  }
  internal static bool HasCapability(string listing,string name){return Regex.IsMatch(listing??"",@"(?m)^\s*[A-Z\.]{1,8}\s+"+Regex.Escape(name)+@"\s+");}
  static string CleanVersion(string value){
   var match=Regex.Match(value??"",@"(?m)^ffmpeg version ([^\r\n ]+)");
   return match.Success?match.Value:null;
  }
  internal static string Run(string root){
   var report=new StringBuilder();Action<string,string> line=(state,message)=>report.AppendLine("["+state+"] "+message);
   report.AppendLine("HD820 5.1 助手 · 无声检查");
   report.AppendLine("版本："+typeof(Preflight).Assembly.GetName().Version+"；64位进程："+Environment.Is64BitProcess);
   report.AppendLine("本检查不会播放测试音或更改默认设备；未包含设备ID、个人文件路径和原始音频。");report.AppendLine();
   try{
    string path=RuntimeConfig.FindFfmpeg(root,Environment.GetEnvironmentVariable("PATH"));
    var version=Capture(path,"-hide_banner -version",4000);string label=CleanVersion(version.Output);
    if(version.TimedOut || version.ExitCode!=0 || label==null)line("需处理","FFmpeg无法正常响应。请使用官方推荐的Windows构建，并检查ffmpeg-path.txt是否指向了正确程序。");
    else{
     line("通过",label);
     var encoders=Capture(path,"-hide_banner -encoders",4000);var muxers=Capture(path,"-hide_banner -muxers",4000);
     bool ac3=!encoders.TimedOut && encoders.ExitCode==0 && HasCapability(encoders.Output,"ac3");
     bool spdif=!muxers.TimedOut && muxers.ExitCode==0 && HasCapability(muxers.Output,"spdif");
     line(ac3&&spdif?"通过":"需处理","AC3编码器："+(ac3?"可用":"未确认")+"；IEC 61937/SPDIF封装："+(spdif?"可用":"未确认")+"。");
    }
   }catch(FileNotFoundException){line("需处理","找不到FFmpeg。把ffmpeg.exe放到助手旁边；若已有ffmpeg-path.txt，请先修正或移除其中的旧路径。");}
   catch(Exception e){line("需处理","FFmpeg检查失败（"+e.GetType().Name+"）。检查文件权限、解压是否完整及安全软件拦截记录。");}
   try{RealtimeAc3Encoder.ValidateBurst(File.ReadAllBytes(Path.Combine(root,"silence-ac3-640k.spdif")));line("通过","程序自带静音载体完整。");}
   catch(Exception){line("需处理","静音载体缺失或损坏，请从Release重新完整解压程序包。");}
   try{
    var endpoints=AudioApi.Endpoints();string[] settings=RuntimeConfig.Preferences(root);
    var virtuals=endpoints.Where(e=>(e.Interface??"").IndexOf("Voicemeeter",StringComparison.OrdinalIgnoreCase)>=0).ToArray();
    if(virtuals.Length==0)line("需处理","未检测到Voicemeeter播放端。安装Basic后重启电脑，确认播放设备Input存在。");
    bool haveValidSource=virtuals.Any(RuntimeConfig.IsSource);int inactive=virtuals.Count(e=>e.State!=1);
    if(inactive>0)line("提示",inactive+"个未启用或未连接的虚拟播放端已忽略；只需配置准备使用的入口。");
    for(int i=0;i<virtuals.Length;i++){
     var e=virtuals[i];bool ok=RuntimeConfig.IsSource(e);
     if(e.State!=1)continue;
     line(ok?"通过":haveValidSource?"提示":"需处理","虚拟播放端"+(i+1)+"："+(e.Mix==null?"格式不可读":e.Mix.ToString())+(ok?"":"。用作入口时需要5.1/48kHz（或支持的常规7.1），空间音效关闭。"));
    }
    string inputId=settings.Length>=6?settings[4]:null,outputId=settings.Length>=6?settings[5]:null;
    var source=RuntimeConfig.Select(endpoints,true,inputId);var output=RuntimeConfig.Select(endpoints,false,outputId);
    line(source==null?"需处理":"通过",source==null?"尚未选定可用入口，请在助手中选择Voicemeeter Input。":"已选入口格式可用。");
    line(output==null?"需处理":"待实测",output==null?"尚未明确选定可用HD820数字输出。请手动核实USB设备，并在助手下拉框选择。":"已保存的USB数字输出在线；名称不能证明AC3兼容，仍需低音量逐声道验收。");
    if(source!=null){bool active=AudioApi.DefaultId(1)==source.Id;line("提示",active?"当前系统媒体默认输出为已选虚拟入口。":"当前系统媒体默认输出不是已选虚拟入口；助手启动后才切换，应用也需使用默认设备或该入口。");}
   }catch(Exception e){line("需处理","设备或偏好读取失败（"+e.GetType().Name+"）。重新插拔后刷新；若配置损坏，请查看界面的恢复提示。");}
   try{line("提示",AppLifetime.StartupEnabled?"此程序位置的当前用户登录自启已开启。":"此程序位置的登录自启未开启（任务也可能指向另一个版本）。需要时在本版助手中启用。");}
   catch(Exception){line("需处理","无法读取当前用户登录任务；这不代表自启已关闭。请检查任务计划程序权限。");}
   report.AppendLine();report.AppendLine("下载与操作说明：https://github.com/MoriAss/hd820-5.1-assistant#readme");
   report.AppendLine("Voicemeeter：https://vb-audio.com/Voicemeeter/");
   report.AppendLine("FFmpeg Windows ZIP：https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip");
   report.AppendLine("本检查不测USB/音箱的实际连续性与端到端延迟，不以通过代替逐声道实听。");return report.ToString();
  }
 }
}
