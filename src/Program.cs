using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using System.Xml.Serialization;
using System.Xml;
using Hd820GameBridge.Reviewer;

namespace Hd820GameBridge {
 public class RoutingSnapshot {
  static readonly object gate=new object();
  public string SourceId,ConsoleId,MultimediaId,CreatedAt;
  public static string PathName {get{return Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"routing-before.xml");}}
  public static void Save(string source){lock(gate){
   if(String.IsNullOrWhiteSpace(source))throw new ArgumentException("入口设备ID不能为空。");
   if(File.Exists(PathName))throw new InvalidOperationException("仍有上次的路由记录，请先恢复。");
   var value=new RoutingSnapshot{SourceId=source,ConsoleId=AudioApi.DefaultId(0),MultimediaId=AudioApi.DefaultId(1),CreatedAt=DateTime.UtcNow.ToString("o")};
   string temporary=PathName+"."+Guid.NewGuid().ToString("N")+".tmp";
   try{using(var file=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None))new XmlSerializer(typeof(RoutingSnapshot)).Serialize(file,value);File.Move(temporary,PathName);}
   finally{if(File.Exists(temporary))try{File.Delete(temporary);}catch{}}
  }}
  public static string Restore(){
   lock(gate){
   if(!File.Exists(PathName))return "当前输出路由未改动。";
   RoutingSnapshot saved;
   using(var reader=XmlReader.Create(PathName,new XmlReaderSettings{DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null}))saved=(RoutingSnapshot)new XmlSerializer(typeof(RoutingSnapshot)).Deserialize(reader);
   if(saved==null || String.IsNullOrWhiteSpace(saved.SourceId) || String.IsNullOrWhiteSpace(saved.ConsoleId) || String.IsNullOrWhiteSpace(saved.MultimediaId))throw new InvalidOperationException("路由记录不完整，已保留原文件；请手动检查默认输出。");
   var messages=new List<string>();
   bool restoreConsole=AudioApi.DefaultId(0)==saved.SourceId,restoreMultimedia=AudioApi.DefaultId(1)==saved.SourceId;
   if(restoreConsole){AudioPolicy.SetDefaultEndpoint(saved.ConsoleId,AudioPolicy.Role.Console);messages.Add("已恢复游戏默认输出");}
   if(restoreMultimedia){AudioPolicy.SetDefaultEndpoint(saved.MultimediaId,AudioPolicy.Role.Multimedia);messages.Add("已恢复媒体默认输出");}
   // Only revert roles we still own; preserve a device the user selected during the run.
   Directory.CreateDirectory(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"logs"));File.Move(PathName,Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"logs","routing-restored-"+DateTime.Now.ToString("yyyyMMdd-HHmmss-fff")+"-"+Guid.NewGuid().ToString("N")+".xml"));
   return messages.Count==0?"当前默认设备已经由你另行选择，保持现状。":String.Join("；",messages.ToArray());
   }
  }
 }
 internal static class Program {
  internal static string SourceId {get{return RuntimeConfig.ResolveEndpoint(true).Id;}}
  internal static string OutputId {get{return RuntimeConfig.ResolveEndpoint(false).Id;}}
  internal static string Root {get{return AppDomain.CurrentDomain.BaseDirectory;}}
  internal static string Ffmpeg {get{return RuntimeConfig.FindFfmpeg(Root,Environment.GetEnvironmentVariable("PATH"));}}
  internal static byte[] Silence {get{return File.ReadAllBytes(Path.Combine(Root,"silence-ac3-640k.spdif"));}}
  [STAThread] static int Main(string[] args){
   bool gui=args.All(a=>a=="--background" || a=="--autostart" || a=="--show");
   bool exit=args.Length==1 && args[0]=="--exit";
   // Startup settings have their own lock and must remain usable while the GUI owns the audio mutex.
   if(args.Length==1 && (args[0]=="--enable-startup" || args[0]=="--disable-startup")){
    try{Directory.CreateDirectory(Path.Combine(Root,"logs"));return Command(args);}catch(Exception error){WriteLastError(error);return 1;}
   }
   bool owner;using(var mutex=new Mutex(true,AppLifetime.MutexName,out owner)){
    if(!owner){
     if(exit)return AppLifetime.Signal(AppLifetime.ExitEventName)?0:3;
     if(gui){return args.Contains("--background") || AppLifetime.Signal(AppLifetime.ShowEventName)?0:3;}
     return 3;
    }
    if(exit)return 0;
    try{
     Directory.CreateDirectory(Path.Combine(Root,"logs"));
     if(!gui)return Command(args);
     Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
     using(var showRequest=new EventWaitHandle(false,EventResetMode.AutoReset,AppLifetime.ShowEventName))
     using(var exitRequest=new EventWaitHandle(false,EventResetMode.AutoReset,AppLifetime.ExitEventName))
      Application.Run(new MainForm(args.Contains("--background"),args.Contains("--autostart"),showRequest,exitRequest));
     return 0;
    }catch(Exception e){WriteLastError(e);try{RoutingSnapshot.Restore();}catch{}if(gui && !args.Contains("--background"))MessageBox.Show(e.Message,"HD820游戏5.1助手");return 1;}
   }
  }
  static void WriteLastError(Exception error){try{Directory.CreateDirectory(Path.Combine(Root,"logs"));RuntimeConfig.AtomicWrite(Path.Combine(Root,"logs","last-error.txt"),error.ToString());}catch{}}
  static int Command(string[] args){
   if(args[0]=="--enable-startup" || args[0]=="--disable-startup"){
    bool enabled=args[0]=="--enable-startup";AppLifetime.SetStartup(enabled);
    if(AppLifetime.StartupEnabled!=enabled)throw new InvalidOperationException("自启动任务回读不一致。");
    File.WriteAllText(Path.Combine(Root,"logs","startup-result.txt"),"Enabled="+enabled+Environment.NewLine+"Task="+AppLifetime.StartupTaskName,Encoding.UTF8);return 0;
   }
   if(args[0]=="--restore"){File.WriteAllText(Path.Combine(Root,"logs","restore-result.txt"),RoutingSnapshot.Restore(),Encoding.UTF8);return 0;}
   if(args[0]=="--list"){File.WriteAllLines(Path.Combine(Root,"logs","endpoints.txt"),AudioApi.Endpoints().Select(e=>e.Id+" | "+e+" | "+(e.Mix==null?"":e.Mix.ToString())).ToArray(),Encoding.UTF8);return 0;}
   if(args[0]=="--probe-output"){File.WriteAllText(Path.Combine(Root,"logs","output-initialize.txt"),BridgeEngine.DiagnoseOutput(OutputId),Encoding.UTF8);return 0;}
   if(args[0]=="--verify-chain"){
    string sourceId=SourceId,outputId=OutputId;var lines=new ConcurrentQueue<string>();using(var engine=new BridgeEngine(sourceId,outputId,Ffmpeg,Silence,false)){
     engine.AuditBurstPath=Path.Combine(Root,"logs","chain-proof.spdif");engine.Log+=delegate(string s){lines.Enqueue(s);};engine.Start();
     Thread.Sleep(500);VirtualStimulus.Play(sourceId,6);Thread.Sleep(600);
     lines.Enqueue("CapturedFrames="+engine.CapturedFrames);lines.Enqueue("EncodedBursts="+engine.EncodedBursts);lines.Enqueue("RenderedBursts="+engine.RenderedBursts);lines.Enqueue("Underruns="+engine.Underruns);lines.Enqueue("DroppedBursts="+engine.DroppedBursts);lines.Enqueue("Discontinuities="+engine.Discontinuities);lines.Enqueue("Error="+engine.Error);
     engine.Stop();if(engine.Error!=null)throw new InvalidOperationException(engine.Error);
    }File.WriteAllLines(Path.Combine(Root,"logs","chain-proof.txt"),lines.ToArray(),Encoding.UTF8);return 0;
   }
   if(args[0]=="--test-speakers"){
    int seconds=args.Length>1?Int32.Parse(args[1]):18;seconds=Math.Max(1,Math.Min(36,seconds));var lines=new ConcurrentQueue<string>();using(var engine=new BridgeEngine(SourceId,OutputId,Ffmpeg,Silence,true)){
     engine.Log+=delegate(string s){lines.Enqueue(s);};engine.Start();Thread.Sleep(seconds*1000);lines.Enqueue("EncodedBursts="+engine.EncodedBursts);lines.Enqueue("RenderedBursts="+engine.RenderedBursts);lines.Enqueue("Underruns="+engine.Underruns);lines.Enqueue("DroppedBursts="+engine.DroppedBursts);lines.Enqueue("Error="+engine.Error);engine.Stop();if(engine.Error!=null)throw new InvalidOperationException(engine.Error);
    }File.WriteAllLines(Path.Combine(Root,"logs","speaker-test.txt"),lines.ToArray(),Encoding.UTF8);return 0;
   }
   throw new ArgumentException("未知命令。");
  }
 }
 internal sealed class MainForm : Form {
  readonly ModernCombo source=new ModernCombo(),output=new ModernCombo();
  readonly ModernButton start=new ModernButton(ModernButton.Kind.Primary),stop=new ModernButton(ModernButton.Kind.Default),test=new ModernButton(ModernButton.Kind.Ghost),refresh=new ModernButton(ModernButton.Kind.Ghost),restore=new ModernButton(ModernButton.Kind.Ghost);
  readonly Label status=new Label(),details=new Label(),gainLabel=new Label(),bassLabel=new Label();
  readonly ModernSwitch bassToggle=new ModernSwitch(),latencyToggle=new ModernSwitch(),startupToggle=new ModernSwitch();
  readonly ModernSlider gain=new ModernSlider(),bassLevel=new ModernSlider();
  readonly TextBox log=new TextBox();
  readonly StatusPill pill=new StatusPill();
  readonly Panel content=new Panel();
  readonly ChannelMeterControl channelMeter=new ChannelMeterControl();
  readonly System.Windows.Forms.Timer meterTimer=new System.Windows.Forms.Timer();
  readonly System.Windows.Forms.Timer timer=new System.Windows.Forms.Timer();
  readonly System.Windows.Forms.Timer lifecycleTimer=new System.Windows.Forms.Timer();
  readonly NotifyIcon tray=new NotifyIcon();readonly ContextMenuStrip trayMenu=new ContextMenuStrip();
  Icon ownedTrayIcon;
  readonly ToolStripMenuItem trayStart=new ToolStripMenuItem("启动游戏5.1"),trayStop=new ToolStripMenuItem("停止并恢复输出");
  readonly EventWaitHandle showRequest,exitRequest;
  readonly object lifecycleGate=new object();
  readonly ManualResetEvent startupCancellation=new ManualResetEvent(false);
  BridgeEngine pendingEngine;Thread startupWorker;
  BridgeEngine engine;bool busy,testMode,startHidden,loadingPreferences,autoStartPending,errorState;volatile bool exitRequested;
  bool restoringWindow,hidingToTray;
  DateTime testStarted,nextAutoStart,autoStartDeadline;
  DateTime nextTelemetry;
  DateTime nextExitAttempt;
  string pendingTelemetryJson;
  int telemetryWriterRunning;
  string preferredSource,preferredOutput;
  string logPath;
  readonly int uiThreadId=Thread.CurrentThread.ManagedThreadId;
  readonly ConcurrentQueue<string> queuedLogs=new ConcurrentQueue<string>();
  int queuedLogCount,skippedLogCount;
  const int MaximumUiLogChars=65536,MaximumQueuedLogs=256,MaximumLogLineChars=2048;
  const long MaximumSessionLogBytes=2*1024*1024;
  public MainForm():this(false,false,null,null){}
  internal MainForm(bool background,bool autoStart,EventWaitHandle showSignal,EventWaitHandle exitSignal){
   startHidden=background;autoStartPending=autoStart;showRequest=showSignal;exitRequest=exitSignal;ShowInTaskbar=!background;
   nextAutoStart=DateTime.UtcNow.AddSeconds(2);autoStartDeadline=DateTime.UtcNow.AddMinutes(2);
   Text="HD820 游戏 5.1 助手 · v3.3.1 · 六声道电平";ClientSize=new Size(860,Math.Min(980,Math.Max(640,Screen.PrimaryScreen.WorkingArea.Height-90)));MinimumSize=new Size(730,680);StartPosition=FormStartPosition.CenterScreen;Font=new Font("Microsoft YaHei UI",9.75f);BackColor=UiTheme.Window;AutoScaleMode=AutoScaleMode.Dpi;AutoScroll=true;AutoScrollMinSize=new Size(0,980);
   content.Location=Point.Empty;content.Size=new Size(ClientSize.Width,980);content.Anchor=AnchorStyles.Top|AnchorStyles.Left;content.BackColor=UiTheme.Window;base.Controls.Add(content);
   Layout+=delegate{if(content.Width!=ClientSize.Width)content.Width=ClientSize.Width;};
   try{using(var appIcon=Icon.ExtractAssociatedIcon(Application.ExecutablePath)){if(appIcon!=null)Icon=new Icon(appIcon,appIcon.Width,appIcon.Height);}}catch{}
   // 顶部：标题 + 副标题 + 状态胶囊
   content.Controls.Add(new Label{Text="HD820 游戏 5.1 助手 · v3.3.1",Font=new Font(Font.FontFamily,15.5f,FontStyle.Bold),ForeColor=UiTheme.PrimaryText,AutoSize=true,Location=new Point(28,16)});
   content.Controls.Add(new Label{Text="游戏 5.1 → 实时 AC3 → HD820 → 六路音箱",Font=new Font(Font.FontFamily,9f),ForeColor=UiTheme.SecondaryText,AutoSize=true,Location=new Point(31,56)});
   pill.Location=new Point(ClientSize.Width-24-92,20);pill.Anchor=AnchorStyles.Top|AnchorStyles.Right;pill.SetState("就绪",UiTheme.Gray,UiTheme.GrayBack,UiTheme.Gray);content.Controls.Add(pill);
   // 卡片1：音频链路
   var linkCard=new CardPanel{Location=new Point(20,88),Size=new Size(ClientSize.Width-40,134),Anchor=AnchorStyles.Top|AnchorStyles.Left|AnchorStyles.Right,TitleText="音频链路"};
   int innerW=linkCard.ClientSize.Width-28;
   startupToggle.Location=new Point(innerW-46,7);startupToggle.Anchor=AnchorStyles.Top|AnchorStyles.Right;linkCard.Controls.Add(startupToggle);
   var startupCaption=new Label{Text="开机自启动",Font=new Font(Font.FontFamily,8.5f),ForeColor=UiTheme.SecondaryText,AutoSize=true,Location=new Point(innerW-112,12),Anchor=AnchorStyles.Top|AnchorStyles.Right};linkCard.Controls.Add(startupCaption);
   linkCard.Controls.Add(new Label{Text="游戏入口",AutoSize=true,Location=new Point(14,50)});
   source.Location=new Point(132,42);source.Size=new Size(innerW-132,32);source.Anchor=AnchorStyles.Top|AnchorStyles.Left|AnchorStyles.Right;source.PlaceholderText="未检测到 Voicemeeter 5.1 入口";linkCard.Controls.Add(source);
   linkCard.Controls.Add(new Label{Text="HD820 输出",AutoSize=true,Location=new Point(14,92)});
   output.Location=new Point(132,84);output.Size=new Size(innerW-132,32);output.Anchor=AnchorStyles.Top|AnchorStyles.Left|AnchorStyles.Right;output.PlaceholderText="未检测到 HD820 数字输出";linkCard.Controls.Add(output);
   content.Controls.Add(linkCard);
   // 卡片2：声音调节
   var soundCard=new CardPanel{Location=new Point(20,232),Size=new Size(ClientSize.Width-40,168),Anchor=AnchorStyles.Top|AnchorStyles.Left|AnchorStyles.Right,TitleText="声音调节"};
   gainLabel.Text="游戏音量：70%";gainLabel.AutoSize=true;gainLabel.Location=new Point(14,50);soundCard.Controls.Add(gainLabel);
   gain.Location=new Point(132,42);gain.Size=new Size(innerW-146,36);gain.Anchor=AnchorStyles.Top|AnchorStyles.Left|AnchorStyles.Right;gain.Minimum=0;gain.Maximum=100;gain.Value=70;gain.Scroll+=delegate{gainLabel.Text="游戏音量："+gain.Value+"%";if(engine!=null)engine.Gain=gain.Value/100f;SavePreferences();};soundCard.Controls.Add(gain);
   bassLabel.Text="补充低音：50%";bassLabel.AutoSize=true;bassLabel.Location=new Point(14,92);soundCard.Controls.Add(bassLabel);
   bassLevel.Location=new Point(132,84);bassLevel.Size=new Size(innerW-146,36);bassLevel.Anchor=AnchorStyles.Top|AnchorStyles.Left|AnchorStyles.Right;bassLevel.Minimum=0;bassLevel.Maximum=100;bassLevel.Value=50;bassLevel.Scroll+=delegate{bassLabel.Text="补充低音："+bassLevel.Value+"%";if(engine!=null)engine.BassLevel=bassLevel.Value/100f;SavePreferences();};soundCard.Controls.Add(bassLevel);
   bassToggle.Location=new Point(14,126);soundCard.Controls.Add(bassToggle);
   soundCard.Controls.Add(new Label{Text="给低音炮补充主声道低频（80Hz）",Font=new Font(Font.FontFamily,9f),ForeColor=UiTheme.SecondaryText,AutoSize=true,Location=new Point(68,130)});
   latencyToggle.Location=new Point(410,126);soundCard.Controls.Add(latencyToggle);
   soundCard.Controls.Add(new Label{Text="FPS 低延迟 · 降低排队延迟",Font=new Font(Font.FontFamily,9f),ForeColor=UiTheme.SecondaryText,AutoSize=true,Location=new Point(464,130)});
   content.Controls.Add(soundCard);
   // 六路表取自增益和低音管理之后、AC3编码之前，不是音箱声压测量。
   channelMeter.Location=new Point(20,410);channelMeter.Size=new Size(ClientSize.Width-40,220);channelMeter.Anchor=AnchorStyles.Top|AnchorStyles.Left|AnchorStyles.Right;content.Controls.Add(channelMeter);
   // 卡片3：控制
   var ctrlCard=new CardPanel{Location=new Point(20,640),Size=new Size(ClientSize.Width-40,140),Anchor=AnchorStyles.Top|AnchorStyles.Left|AnchorStyles.Right,TitleText="控制"};
   start.Text="启动游戏 5.1";start.Location=new Point(14,40);start.Size=new Size(190,40);ctrlCard.Controls.Add(start);
   stop.Text="停止并恢复输出";stop.Location=new Point(216,40);stop.Size=new Size(190,40);ctrlCard.Controls.Add(stop);
   test.Text="逐声道测试";test.Location=new Point(418,40);test.Size=new Size(150,40);ctrlCard.Controls.Add(test);
   refresh.Text="刷新设备";refresh.Location=new Point(14,92);refresh.Size=new Size(120,34);ctrlCard.Controls.Add(refresh);
   restore.Text="恢复原输出";restore.Location=new Point(142,92);restore.Size=new Size(120,34);ctrlCard.Controls.Add(restore);
   ctrlCard.Controls.Add(new Label{Text="关闭窗口继续在托盘运行；退出助手才恢复原输出。",Font=new Font(Font.FontFamily,8.5f),ForeColor=UiTheme.MutedText,AutoSize=true,Location=new Point(278,99)});
   content.Controls.Add(ctrlCard);
   // 卡片4：状态与日志
   var logCard=new CardPanel{Location=new Point(20,790),Size=new Size(ClientSize.Width-40,176),Anchor=AnchorStyles.Top|AnchorStyles.Left|AnchorStyles.Right,TitleText="状态与日志"};
   var trayHint=new Label{Text="双击托盘可打开 · 右键托盘退出并恢复输出",Font=new Font(Font.FontFamily,8.5f),ForeColor=UiTheme.MutedText,AutoSize=true,Location=new Point(logCard.ClientSize.Width-14-268,13),Anchor=AnchorStyles.Top|AnchorStyles.Right};logCard.Controls.Add(trayHint);
   status.Location=new Point(14,38);status.Size=new Size(logCard.ClientSize.Width-28,24);status.Anchor=AnchorStyles.Top|AnchorStyles.Left|AnchorStyles.Right;status.Font=new Font(Font.FontFamily,11.5f,FontStyle.Bold);status.ForeColor=UiTheme.PrimaryText;status.Text="准备就绪";logCard.Controls.Add(status);
   details.Location=new Point(14,64);details.Size=new Size(logCard.ClientSize.Width-28,50);details.Anchor=AnchorStyles.Top|AnchorStyles.Left|AnchorStyles.Right;details.Font=new Font(Font.FontFamily,9f);details.ForeColor=UiTheme.SecondaryText;details.Text="补充低音把主声道中的低频送给低音炮，中置和环绕保持原声道。\r\n关闭窗口继续在托盘运行；退出助手才恢复原输出。";logCard.Controls.Add(details);
   log.Location=new Point(14,118);log.Size=new Size(logCard.ClientSize.Width-28,Math.Max(40,logCard.ClientSize.Height-118-14));log.Anchor=AnchorStyles.Top|AnchorStyles.Bottom|AnchorStyles.Left|AnchorStyles.Right;log.Multiline=true;log.ReadOnly=true;log.ScrollBars=ScrollBars.Vertical;log.BorderStyle=BorderStyle.None;log.BackColor=Color.FromArgb(248,250,253);log.ForeColor=Color.FromArgb(51,65,85);log.Font=new Font(Font.FontFamily,9f);log.TabStop=false;logCard.Controls.Add(log);
   content.Controls.Add(logCard);
   logPath=Path.Combine(Program.Root,"logs","session-"+DateTime.Now.ToString("yyyyMMdd-HHmmss")+".txt");
   start.Click+=delegate{autoStartPending=false;StartBridge(false);};test.Click+=delegate{autoStartPending=false;StartBridge(true);};stop.Click+=delegate{autoStartPending=false;StopBridge();};refresh.Click+=delegate{RefreshDevices();};restore.Click+=delegate{try{AddLog(RoutingSnapshot.Restore());RefreshDevices();}catch(Exception e){AddLog(e.Message);}};
   bassToggle.Checked=true;
   bassToggle.CheckedChanged+=delegate{bassLevel.Enabled=bassToggle.Checked;if(engine!=null)engine.BassEnabled=bassToggle.Checked && !testMode;SavePreferences();};
   latencyToggle.Checked=true;
   latencyToggle.CheckedChanged+=delegate{if(engine!=null)engine.LowLatencyEnabled=latencyToggle.Checked;SavePreferences();};
   try{startupToggle.Checked=AppLifetime.StartupEnabled;}catch{}
   startupToggle.CheckedChanged+=HandleStartupChanged;
   ConfigureTray();LoadPreferences();
   source.SelectedIndexChanged+=delegate{if(!loadingPreferences){SavePreferences();SetButtons();}};
   output.SelectedIndexChanged+=delegate{if(!loadingPreferences){SavePreferences();SetButtons();}};
   try{if(File.Exists(RoutingSnapshot.PathName))AddLog("上次路由恢复："+RoutingSnapshot.Restore());RefreshDevices();}catch(Exception e){AddLog(e.Message);}
   FormClosing+=HandleClosing;FormClosed+=delegate{meterTimer.Stop();timer.Stop();lifecycleTimer.Stop();tray.Visible=false;tray.Dispose();trayMenu.Dispose();meterTimer.Dispose();timer.Dispose();lifecycleTimer.Dispose();if(startupWorker==null || !startupWorker.IsAlive)startupCancellation.Dispose();};
   meterTimer.Interval=50;meterTimer.Tick+=delegate{if(Visible && WindowState!=FormWindowState.Minimized)channelMeter.SetSnapshot(engine==null?null:engine.MeterSnapshot);};
   VisibleChanged+=delegate{meterTimer.Enabled=Visible && WindowState!=FormWindowState.Minimized;if(meterTimer.Enabled)channelMeter.SetSnapshot(engine==null?null:engine.MeterSnapshot);};
   // Keep native minimize semantics. Only an explicit close-to-tray action hides
   // the form; a resize/native placement notification must never close it again.
   Resize+=delegate{meterTimer.Enabled=Visible && WindowState!=FormWindowState.Minimized;};
   lifecycleTimer.Interval=400;lifecycleTimer.Tick+=delegate{
    DrainLogs();
    if(exitRequested){if(DateTime.UtcNow>=nextExitAttempt){nextExitAttempt=DateTime.UtcNow.AddSeconds(2);Close();}return;}
    if(exitRequest!=null && exitRequest.WaitOne(0)){RequestExit();return;}
    if(showRequest!=null && showRequest.WaitOne(0))ShowControlPanel();
    TryAutoStart();UpdateTray();
   };lifecycleTimer.Start();
   timer.Interval=400;timer.Tick+=delegate{
    if(engine==null || busy)return;if(DateTime.UtcNow>=nextTelemetry){nextTelemetry=DateTime.UtcNow.AddSeconds(2);SaveTelemetry();}
    if(engine.Error!=null){AddLog(engine.Error);tray.ShowBalloonTip(4000,"HD820 5.1 已停止",engine.Error,ToolTipIcon.Warning);StopBridge();return;}
    status.Text=testMode?"正在测试："+engine.CurrentTestChannel:"游戏 5.1 正在运行";details.Text=engine.SourceFormat+"\r\n"+engine.BassInfo+"\r\n"+(engine.LowLatencyEnabled?"FPS模式":"标准模式")+"；播放排队均值 "+engine.MeanPaddingMs.ToString("F1")+"ms；欠载 "+engine.Underruns+"；丢包 "+engine.DroppedBursts+"；跳过PCM "+engine.DroppedInputFrames+"帧";if(testMode && (DateTime.Now-testStarted).TotalSeconds>=18)StopBridge();
   };timer.Start();SetButtons();
  }
  protected override void SetVisibleCore(bool value){if(value && startHidden && !restoringWindow){if(!IsHandleCreated)CreateHandle();value=false;}base.SetVisibleCore(value);}
  protected override void Dispose(bool disposing){
   if(disposing){meterTimer.Dispose();timer.Dispose();lifecycleTimer.Dispose();tray.Visible=false;tray.Dispose();trayMenu.Dispose();if(ownedTrayIcon!=null){ownedTrayIcon.Dispose();ownedTrayIcon=null;}if(startupWorker==null || !startupWorker.IsAlive)startupCancellation.Dispose();}
   base.Dispose(disposing);
  }
  [System.Runtime.InteropServices.DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd,int attribute,ref int value,int size);
  protected override void OnHandleCreated(EventArgs e){
   base.OnHandleCreated(e);
   try{int light=0;DwmSetWindowAttribute(Handle,20,ref light,4);DwmSetWindowAttribute(Handle,19,ref light,4);}catch{}
  }
  void HandleStartupChanged(object sender,EventArgs e){
   try{AppLifetime.SetStartup(startupToggle.Checked);AddLog(startupToggle.Checked?"已启用登录后后台自动启动5.1。":"已关闭开机自启动。");}
   catch(Exception error){AddLog("自启动设置失败："+error.Message);startupToggle.CheckedChanged-=HandleStartupChanged;try{startupToggle.Checked=AppLifetime.StartupEnabled;}catch{}finally{startupToggle.CheckedChanged+=HandleStartupChanged;}}
  }
  void ConfigureTray(){
   trayMenu.Items.Add("打开控制面板",null,delegate{ShowControlPanel();});trayMenu.Items.Add(new ToolStripSeparator());
   trayStart.Click+=delegate{autoStartPending=false;StartBridge(false);};trayStop.Click+=delegate{autoStartPending=false;StopBridge();};
   trayMenu.Items.Add(trayStart);trayMenu.Items.Add(trayStop);trayMenu.Items.Add(new ToolStripSeparator());
   trayMenu.Items.Add("退出并恢复输出",null,delegate{RequestExit();});
   try{using(var appIcon=Icon.ExtractAssociatedIcon(Application.ExecutablePath)){ownedTrayIcon=appIcon!=null?new Icon(appIcon,appIcon.Width,appIcon.Height):null;tray.Icon=ownedTrayIcon??SystemIcons.Application;}}catch{tray.Icon=SystemIcons.Application;}
   tray.Text="HD820 游戏5.1助手";tray.ContextMenuStrip=trayMenu;tray.DoubleClick+=delegate{ShowControlPanel();};tray.Visible=true;
  }
  void HideToTray(){
   if(restoringWindow || hidingToTray || exitRequested || IsDisposed)return;
   hidingToTray=true;
   try{
    // A hidden window has no taskbar button. Keep ShowInTaskbar once enabled:
    // toggling it on every tray round trip recreates the native handle and can
    // replay a delayed minimized placement after the restoration has returned.
    startHidden=true;Hide();WindowState=FormWindowState.Normal;
    AddLog("窗口已收起到托盘，5.1运行状态保持。");
   }
   finally{hidingToTray=false;}
  }
  [System.Runtime.InteropServices.DllImport("user32.dll")]
  static extern bool ShowWindow(IntPtr window,int command);
  void ShowControlPanel(){
   if(restoringWindow || exitRequested || IsDisposed || Disposing)return;
   restoringWindow=true;
   try{
    startHidden=false;
    // Only the first explicit display needs to enable taskbar participation.
    // Later tray round trips keep the same handle and only change visibility.
    WindowState=FormWindowState.Normal;if(!ShowInTaskbar)ShowInTaskbar=true;startHidden=false;
    Show();ShowWindow(Handle,9);WindowState=FormWindowState.Normal;
    meterTimer.Enabled=Visible;if(Visible)channelMeter.SetSnapshot(engine==null?null:engine.MeterSnapshot);
    Activate();BringToFront();
   }finally{restoringWindow=false;}
  }
  void UpdateTray(){tray.Text=engine!=null?(engine.Running?"HD820 游戏5.1 · 正在运行":"HD820 游戏5.1 · 输出待释放"):busy?"HD820 游戏5.1 · 正在连接":autoStartPending?"HD820 游戏5.1 · 等待设备":"HD820 游戏5.1 · 已停止";}
  void UpdatePill(){
   string text;Color dot,bg,fg;
   if(engine!=null){
    if(testMode){text="正在测试";dot=UiTheme.Accent;bg=UiTheme.BlueBack;fg=UiTheme.Accent;}
    else{text="正在运行";dot=UiTheme.Green;bg=UiTheme.GreenBack;fg=UiTheme.Green;}
   }else if(busy){text="正在连接";dot=UiTheme.Amber;bg=UiTheme.AmberBack;fg=UiTheme.Amber;}
   else if(autoStartPending){text="等待设备";dot=UiTheme.Amber;bg=UiTheme.AmberBack;fg=UiTheme.Amber;}
   else if(errorState){text="异常";dot=UiTheme.Red;bg=UiTheme.RedBack;fg=UiTheme.Red;}
   else{text="就绪";dot=UiTheme.Gray;bg=UiTheme.GrayBack;fg=UiTheme.Gray;}
   pill.SetState(text,dot,bg,fg);
  }
  void RequestExit(){if(IsDisposed || Disposing)return;lock(lifecycleGate){exitRequested=true;}startupCancellation.Set();autoStartPending=false;nextExitAttempt=DateTime.UtcNow.AddSeconds(2);Close();}
  void HandleClosing(object sender,FormClosingEventArgs e){
   if(e.CloseReason==CloseReason.UserClosing && !exitRequested){e.Cancel=true;HideToTray();return;}
   lock(lifecycleGate){exitRequested=true;}startupCancellation.Set();autoStartPending=false;
   // Do not cancel Windows logoff/shutdown; the startup worker is barred from changing routing.
   bool systemClosing=e.CloseReason==CloseReason.WindowsShutDown || e.CloseReason==CloseReason.TaskManagerClosing;
   if(busy && !systemClosing){e.Cancel=true;nextExitAttempt=DateTime.UtcNow.AddSeconds(2);return;}
   if(startupWorker!=null && startupWorker.IsAlive)startupWorker.Join(2000);
   if(startupWorker==null || !startupWorker.IsAlive)CleanupPendingEngine();
   StopBridge();lock(lifecycleGate){try{AddLog(RoutingSnapshot.Restore());}catch(Exception error){AddLog(error.Message);}}
   if(!systemClosing && (pendingEngine!=null || engine!=null || (startupWorker!=null && startupWorker.IsAlive))){
    e.Cancel=true;nextExitAttempt=DateTime.UtcNow.AddSeconds(2);meterTimer.Stop();timer.Stop();lifecycleTimer.Start();status.Text="正在等待音频资源释放后退出…";return;
   }
   DrainLogs();WaitForTelemetry(1500);meterTimer.Stop();timer.Stop();lifecycleTimer.Stop();tray.Visible=false;
  }
  void CleanupPendingEngine(){
   BridgeEngine pending;lock(lifecycleGate){pending=pendingEngine;}
   if(pending==null)return;
   try{pending.Dispose();lock(lifecycleGate){if(Object.ReferenceEquals(pendingEngine,pending))pendingEngine=null;}}
   catch(Exception error){AddLog("退出连接清理未完成，已保留资源供重试："+error.Message);}
  }
  void FinishStarted(BridgeEngine created,bool testing){
   bool cancelled;
   lock(lifecycleGate){cancelled=exitRequested || !Object.ReferenceEquals(pendingEngine,created);if(!cancelled){pendingEngine=null;engine=created;}}
   if(cancelled){CleanupPendingEngine();busy=false;if(!IsDisposed)Close();return;}
   autoStartPending=false;errorState=false;engine.Gain=gain.Value/100f;engine.BassEnabled=bassToggle.Checked && !testing;engine.BassLevel=bassLevel.Value/100f;engine.LowLatencyEnabled=latencyToggle.Checked;testStarted=DateTime.Now;busy=false;timer.Start();SetButtons();SavePreferences();AddLog(testing?"测试顺序：左前 → 右前 → 中置 → 低音 → 左环绕 → 右环绕，每路3秒。":"已启动。关闭窗口仍继续5.1；右键托盘可退出并恢复输出。");
  }
  void TryAutoStart(){
   if(!autoStartPending || exitRequested || busy || engine!=null || DateTime.UtcNow<nextAutoStart)return;
   if(DateTime.UtcNow>=autoStartDeadline){autoStartPending=false;errorState=true;status.Text="自动启动未完成，请检查HD820连接";AddLog(status.Text);SetButtons();tray.ShowBalloonTip(5000,"HD820 5.1 未启动","请接好HD820，双击托盘后手动启动。",ToolTipIcon.Warning);return;}
   nextAutoStart=DateTime.UtcNow.AddSeconds(5);
   try{RefreshDevices();if(source.SelectedItem==null || output.SelectedItem==null){status.Text="等待HD820和5.1入口就绪…";SetButtons();return;}StartBridge(false);}catch(Exception error){AddLog("自动启动等待："+error.Message);}
  }
  void SavePreferences(){if(loadingPreferences)return;try{var input=source.SelectedItem as AudioEndpoint;var destination=output.SelectedItem as AudioEndpoint;if(input!=null)preferredSource=input.Id;if(destination!=null)preferredOutput=destination.Id;RuntimeConfig.AtomicWrite(Path.Combine(Program.Root,"bass-settings.txt"),String.Join(Environment.NewLine,new[]{bassToggle.Checked?"1":"0",bassLevel.Value.ToString(),latencyToggle.Checked?"1":"0",gain.Value.ToString(),preferredSource??"",preferredOutput??""})+Environment.NewLine);}catch{}}
  void LoadPreferences(){loadingPreferences=true;try{var p=Path.Combine(Program.Root,"bass-settings.txt");if(!File.Exists(p))return;var lines=File.ReadAllLines(p);int value;if(lines.Length>=2 && Int32.TryParse(lines[1],out value)){bassLevel.Value=Math.Max(0,Math.Min(100,value));bassLabel.Text="补充低音："+bassLevel.Value+"%";}if(lines.Length>0)bassToggle.Checked=lines[0]=="1";if(lines.Length>=3)latencyToggle.Checked=lines[2]=="1";if(lines.Length>=4 && Int32.TryParse(lines[3],out value)){gain.Value=Math.Max(0,Math.Min(100,value));gainLabel.Text="游戏音量："+gain.Value+"%";}if(lines.Length>=6){preferredSource=lines[4];preferredOutput=lines[5];}}catch{}finally{loadingPreferences=false;}}
  void SaveTelemetry(){
   var current=engine;if(current==null)return;
   try{Interlocked.Exchange(ref pendingTelemetryJson,current.MetricsJson());StartTelemetryWriter();}catch{}
  }
  void StartTelemetryWriter(){
   if(Interlocked.CompareExchange(ref telemetryWriterRunning,1,0)!=0)return;
   ThreadPool.QueueUserWorkItem(delegate{
    try{
     string json;while((json=Interlocked.Exchange(ref pendingTelemetryJson,null))!=null){
      var p=Path.Combine(Program.Root,"logs","live-state.json");Directory.CreateDirectory(Path.GetDirectoryName(p));RuntimeConfig.AtomicWrite(p,json);
     }
    }catch{}finally{
     Interlocked.Exchange(ref telemetryWriterRunning,0);
     if(Interlocked.CompareExchange(ref pendingTelemetryJson,null,null)!=null)StartTelemetryWriter();
    }
   });
  }
  void WaitForTelemetry(int timeoutMs){var watch=Stopwatch.StartNew();while(watch.ElapsedMilliseconds<timeoutMs && (Interlocked.CompareExchange(ref telemetryWriterRunning,0,0)!=0 || Interlocked.CompareExchange(ref pendingTelemetryJson,null,null)!=null))Thread.Sleep(10);}
  void AddLog(string s){
   if(IsDisposed || Disposing)return;
   s=s??"";if(s.Length>MaximumLogLineChars)s=s.Substring(0,MaximumLogLineChars)+"…";
   if(Interlocked.Increment(ref queuedLogCount)>MaximumQueuedLogs){Interlocked.Decrement(ref queuedLogCount);Interlocked.Increment(ref skippedLogCount);return;}
   queuedLogs.Enqueue(DateTime.Now.ToString("HH:mm:ss")+" "+s);
   // Audio workers only enqueue. A bounded UI timer drain avoids an unbounded BeginInvoke backlog.
   if(Thread.CurrentThread.ManagedThreadId==uiThreadId)DrainLogs();
  }
  void DrainLogs(){
   if(IsDisposed || Disposing || Thread.CurrentThread.ManagedThreadId!=uiThreadId)return;
   var text=new StringBuilder();int skipped=Interlocked.Exchange(ref skippedLogCount,0);if(skipped>0)text.AppendLine("日志过密，已合并省略 "+skipped+" 条。");
   string line;while(queuedLogs.TryDequeue(out line)){Interlocked.Decrement(ref queuedLogCount);text.AppendLine(line);}
   if(text.Length==0)return;
   string addition=text.ToString();if(addition.Length>MaximumUiLogChars)addition=addition.Substring(addition.Length-MaximumUiLogChars);
   if(log.TextLength+addition.Length>MaximumUiLogChars){int remove=Math.Min(log.TextLength,log.TextLength+addition.Length-MaximumUiLogChars);log.Select(0,remove);log.SelectedText="";}
   log.AppendText(addition);
   try{
    Directory.CreateDirectory(Path.GetDirectoryName(logPath));
    if(File.Exists(logPath) && new FileInfo(logPath).Length+Encoding.UTF8.GetByteCount(text.ToString())>MaximumSessionLogBytes){
     for(int i=3;i>=1;i--){string next=logPath+"."+i;if(File.Exists(next))File.Delete(next);string previous=i==1?logPath:logPath+"."+(i-1);if(File.Exists(previous))File.Move(previous,next);}
    }
    File.AppendAllText(logPath,text.ToString(),Encoding.UTF8);
   }catch{}
  }
  void RefreshDevices(){
   if(engine!=null || busy)return;var endpoints=AudioApi.Endpoints();
   var selectedSource=RuntimeConfig.Select(endpoints,true,preferredSource);var selectedOutput=RuntimeConfig.Select(endpoints,false,preferredOutput);
   bool wasLoading=loadingPreferences;loadingPreferences=true;
   try{source.SelectedIndex=output.SelectedIndex=-1;source.Items.Clear();output.Items.Clear();foreach(var endpoint in endpoints.Where(RuntimeConfig.IsSource))source.Items.Add(endpoint);foreach(var endpoint in endpoints.Where(RuntimeConfig.IsOutput))output.Items.Add(endpoint);source.SelectedIndex=selectedSource==null?-1:source.Items.IndexOf(selectedSource);output.SelectedIndex=selectedOutput==null?-1:output.Items.IndexOf(selectedOutput);}
   finally{loadingPreferences=wasLoading;}
   SetButtons();
  }
  void SetButtons(){bool running=engine!=null;source.Enabled=output.Enabled=!busy && !running;start.Enabled=test.Enabled=!busy && !running && source.SelectedItem!=null && output.SelectedItem!=null;stop.Enabled=!busy && running;refresh.Enabled=restore.Enabled=!busy && !running;trayStart.Enabled=start.Enabled;trayStop.Enabled=stop.Enabled;UpdateTray();UpdatePill();}
  void StartBridge(bool testing){
   if(busy || engine!=null || exitRequested)return;
   var input=(AudioEndpoint)source.SelectedItem;var destination=(AudioEndpoint)output.SelectedItem;if(input==null || destination==null)return;
   startupCancellation.Reset();busy=true;testMode=testing;errorState=false;SetButtons();status.Text="正在连接…";float level=gain.Value/100f;bool enableBass=bassToggle.Checked && !testing;float subLevel=bassLevel.Value/100f;bool lowLatency=latencyToggle.Checked;
   var worker=new Thread(delegate(){BridgeEngine created=null;try{
    created=new BridgeEngine(input.Id,destination.Id,Program.Ffmpeg,Program.Silence,testing);lock(lifecycleGate){if(exitRequested)throw new OperationCanceledException("助手正在退出，已取消启动。");pendingEngine=created;}created.Gain=level;created.BassEnabled=enableBass;created.BassLevel=subLevel;created.LowLatencyEnabled=lowLatency;created.Log+=AddLog;created.Start(startupCancellation);AddLog(testing?"逐声道测试保留独立输出，不混入补充低音。":created.BassInfo);
    lock(lifecycleGate){if(exitRequested)throw new OperationCanceledException("助手正在退出，已取消启动。");if(!testing){RoutingSnapshot.Save(input.Id);AudioPolicy.SetDefaultEndpoint(input.Id,AudioPolicy.Role.Console);AudioPolicy.SetDefaultEndpoint(input.Id,AudioPolicy.Role.Multimedia);AddLog("已将游戏和媒体默认入口切换到5.1虚拟设备；停止时恢复。");}}
    BeginInvoke((Action)delegate{FinishStarted(created,testing);});
   }catch(Exception e){BridgeEngine retained=null;if(created!=null)try{created.Dispose();}catch(Exception stopError){retained=created;AddLog(stopError.Message);}lock(lifecycleGate){if(Object.ReferenceEquals(pendingEngine,created))pendingEngine=retained;try{AddLog(RoutingSnapshot.Restore());}catch(Exception restoreError){AddLog("恢复失败："+restoreError.Message);}}AddLog(e.Message);try{BeginInvoke((Action)delegate{if(exitRequested){CleanupPendingEngine();busy=false;Close();return;}lock(lifecycleGate){engine=retained;pendingEngine=null;}if(retained!=null)autoStartPending=false;busy=false;errorState=true;status.Text=retained==null?"未启动："+e.Message:"驱动未释放，请退出助手后重开";SetButtons();});}catch(InvalidOperationException){}}
   });worker.IsBackground=true;worker.SetApartmentState(ApartmentState.STA);startupWorker=worker;worker.Start();
  }
  void StopBridge(){if(engine==null){try{AddLog(RoutingSnapshot.Restore());}catch(Exception e){AddLog(e.Message);}return;}try{engine.Dispose();SaveTelemetry();AddLog("结束统计：采集 "+engine.CapturedFrames+" PCM帧；编码 "+engine.EncodedBursts+" AC3包；输出 "+engine.RenderedBursts+"包；欠载 "+engine.Underruns+"；丢包 "+engine.DroppedBursts+"；采集间断 "+engine.Discontinuities+"；低音限幅 "+(engine.BassInfo));engine=null;errorState=false;status.Text="已停止，输出已释放";details.Text="补充低音可随时调整或关闭。\r\n逐声道测试会在18秒后自动停止。";}catch(Exception e){AddLog(e.Message);errorState=true;status.Text="驱动未释放，请关闭助手后重开";timer.Stop();}finally{try{AddLog(RoutingSnapshot.Restore());}catch(Exception e){AddLog("恢复失败："+e.Message);}SetButtons();}}
 }
}
