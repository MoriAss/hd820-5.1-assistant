using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Hd820GameBridge;

internal static class UiLifecycleHarness {
 public sealed class ThrowingTaskFolder {
  public Exception Failure;public int Calls;public string RequestedName;
  public object GetTask(string name){Calls++;RequestedName=name;throw Failure;}
 }
 const BindingFlags Flags=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
 static readonly List<object> cases=new List<object>();static int failed;
 static void Require(bool value,string message){if(!value)throw new InvalidOperationException(message);}
 static object Get(object value,string name){return value.GetType().GetField(name,Flags).GetValue(value);}
 static object Call(object value,string name,params object[] args){return value.GetType().GetMethod(name,Flags).Invoke(value,args);}
 static void Run(string name,Action test){try{test();cases.Add(new{Name=name,Passed=true});}catch(Exception error){while(error is TargetInvocationException && error.InnerException!=null)error=error.InnerException;failed++;cases.Add(new{Name=name,Passed=false,Error=error.ToString()});}}
 static AudioEndpoint Endpoint(string id,bool source){return new AudioEndpoint{Id=id,State=1,Name=source?"Voicemeeter Input":"数字输出",Interface=source?"VB-Audio Voicemeeter VAIO":"USB PnP Sound Device",Mix=source?new AudioFormat{Rate=48000,Channels=6,Mask=0x3F}:null};}
 [STAThread]static int Main(string[] args){
  string root=AppDomain.CurrentDomain.BaseDirectory;
  if(File.Exists(Path.Combine(root,"routing-before.xml")) || File.Exists(Path.Combine(root,"bass-settings.txt")))throw new InvalidOperationException("Fixture must not contain routing or preferences.");
  Directory.CreateDirectory(Path.Combine(root,"logs"));Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
  Run("missing_task_com_hresult_is_absent",delegate{
   var error=new COMException("synthetic missing task",unchecked((int)0x80070002));var folder=new ThrowingTaskFolder{Failure=error};string name="HD820_Fixture_"+Guid.NewGuid().ToString("N");
   Require(AppLifetime.IsMissingTask(error) && AppLifetime.FindTask(folder,name)==null && folder.Calls==1 && folder.RequestedName==name,"A missing-task COM HRESULT must be treated as absent.");
  });
  Run("missing_task_mapped_file_exception_is_absent",delegate{
   var error=Marshal.GetExceptionForHR(unchecked((int)0x80070002),new IntPtr(-1));Require(error is FileNotFoundException,"The missing-file HRESULT must reproduce the CLR-mapped exception type.");
   var folder=new ThrowingTaskFolder{Failure=error};Require(AppLifetime.IsMissingTask(error) && AppLifetime.FindTask(folder,"HD820_Fixture_Mapped")==null,"Mapped FileNotFoundException must have the same absent-task result.");
  });
  Run("task_access_denied_is_not_missing",delegate{
   foreach(Exception error in new Exception[]{new COMException("synthetic denied",unchecked((int)0x80070005)),new UnauthorizedAccessException("synthetic denied")}){
    Require(!AppLifetime.IsMissingTask(error),"Access denied must not mean an absent task.");var folder=new ThrowingTaskFolder{Failure=error};bool preserved=false;try{AppLifetime.FindTask(folder,"HD820_Fixture_Denied");}catch(Exception observed){preserved=Object.ReferenceEquals(observed,error);}Require(preserved,"The original access error must propagate unchanged.");
   }
  });
  Run("task_account_configuration_error_is_not_missing",delegate{
   // Microsoft WinError.h: 0x8004130F is SCHED_E_ACCOUNT_INFORMATION_NOT_SET, not task-not-found.
   var error=new COMException("synthetic task account configuration error",unchecked((int)0x8004130F));Require(!AppLifetime.IsMissingTask(error),"An existing task with missing account information is not an absent task.");bool preserved=false;try{AppLifetime.FindTask(new ThrowingTaskFolder{Failure=error},"HD820_Fixture_Account");}catch(Exception observed){preserved=Object.ReferenceEquals(observed,error);}Require(preserved,"Task account errors must propagate.");Require(!AppLifetime.IsMissingTask(new IOException("synthetic IO error")),"Unrelated IO errors must not be swallowed.");
  });
  Run("random_missing_scheduled_task_readonly_getter",delegate{
   object service=null,folder=null;try{service=Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service",true));((dynamic)service).Connect();folder=((dynamic)service).GetFolder(@"\");string randomName="HD820_ReadOnly_Missing_"+Guid.NewGuid().ToString("N");Require(AppLifetime.FindTask(folder,randomName)==null,"A real random non-existent task must return absent without exception.");}
   finally{if(folder!=null && Marshal.IsComObject(folder))Marshal.ReleaseComObject(folder);if(service!=null && Marshal.IsComObject(service))Marshal.ReleaseComObject(service);}
  });
  Run("combo_populates_and_preserves_selection",delegate{
   using(var owner=new ModernCombo()){owner.Items.AddRange(new object[]{"one","two","three"});owner.SelectedIndex=1;using(var popup=new ModernComboPopup(owner,220,3)){var list=(ListBox)Get(popup,"list");Require(list.Items.Count==3 && list.SelectedIndex==1,"Popup must contain all devices and preserve selection.");popup.Commit(2);Require(owner.SelectedIndex==2,"Popup commit must update owner.");}}
  });
  Run("combo_empty_selection_does_not_throw",delegate{using(var owner=new ModernCombo()){owner.Items.Add("one");using(var popup=new ModernComboPopup(owner,220,1)){Require(((ListBox)Get(popup,"list")).SelectedIndex==-1,"No selection must remain explicit.");}}});
  Run("one_click_for_framework_mouse_click_path",delegate{
   using(var button=new ModernButton(ModernButton.Kind.Default)){button.Size=new Size(100,35);int clicks=0;button.Click+=delegate{clicks++;};Call(button,"OnMouseDown",new MouseEventArgs(MouseButtons.Left,1,10,10,0));
    // WinForms StandardClick delivers OnClick before OnMouseUp. Replay that dispatch sequence without moving the mouse or injecting input.
    typeof(Control).GetMethod("OnClick",Flags).Invoke(button,new object[]{EventArgs.Empty});Call(button,"OnMouseUp",new MouseEventArgs(MouseButtons.Left,1,10,10,0));Require(clicks==1,"A WinForms mouse click must not invoke the action twice.");}
  });
  Run("slider_capture_loss_clears_drag",delegate{using(var slider=new ModernSlider()){slider.Maximum=100;typeof(ModernSlider).GetField("drag",Flags).SetValue(slider,true);Call(slider,"OnMouseCaptureChanged",EventArgs.Empty);Require(!(bool)Get(slider,"drag"),"Lost capture must end drag.");}});
  Run("tiny_rounded_path_is_valid",delegate{using(var path=UiTheme.Rounded(new Rectangle(0,0,1,1),12)){Require(path.PointCount>0,"Tiny controls must have a valid path.");}});
  Run("snapshot_owns_and_sanitizes_data",delegate{double[] p={-3,-6,-9,-12,-15,-18},r={Double.NaN,Double.PositiveInfinity,-9,-12,-15,-18};var snapshot=new ChannelMeterSnapshot(p,r,DateTime.UtcNow.Ticks,2400);p[0]=0;Require(snapshot.PeakDbFs[0]==-3 && snapshot.RmsDbFs[0]==-72 && snapshot.RmsDbFs[1]==-72,"Snapshot must own immutable finite values.");bool blocked=false;try{((IList<double>)snapshot.PeakDbFs)[0]=0;}catch(NotSupportedException){blocked=true;}Require(blocked,"Published snapshot must reject mutation.");});
  Run("ambiguous_endpoints_need_explicit_selection",delegate{var output1=Endpoint("fixture-output-a",false);var output2=Endpoint("fixture-output-b",false);Require(RuntimeConfig.Select(new[]{output1,output2},false,null)==null,"Ambiguous output must not be guessed.");Require(Object.ReferenceEquals(RuntimeConfig.Select(new[]{output1,output2},false,output2.Id),output2),"Explicit stored output must win.");Require(RuntimeConfig.Select(new[]{output1},false,null)==output1,"A unique USB digital candidate may be selected.");output1.Name="扬声器";Require(RuntimeConfig.Select(new[]{output1},false,null)==null,"An analog speaker endpoint must never be selected for AC3.");});
  Run("disconnected_saved_output_does_not_fall_back",delegate{Require(RuntimeConfig.Select(new[]{Endpoint("fixture-other-output",false)},false,"fixture-disconnected-output")==null,"A disconnected saved device must never redirect AC3 to another interface.");});
  Run("source_requires_48k_multichannel_voicemeeter",delegate{var endpoint=Endpoint("fixture-source",true);Require(RuntimeConfig.IsSource(endpoint),"48k six-channel source accepted.");endpoint.Mix.Channels=2;Require(!RuntimeConfig.IsSource(endpoint),"Stereo source rejected.");endpoint.Mix.Channels=6;endpoint.Mix.Rate=44100;Require(!RuntimeConfig.IsSource(endpoint),"44.1k source rejected.");});
  Run("ffmpeg_relative_config_and_path_resolution",delegate{
   string fixture=Path.Combine(root,"runtime-config-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(Path.Combine(fixture,"tools"));string executable=Path.Combine(fixture,"tools","ffmpeg.exe");File.WriteAllBytes(executable,new byte[]{0});
   File.WriteAllText(Path.Combine(fixture,"ffmpeg-path.txt"),"tools\\ffmpeg.exe");Require(RuntimeConfig.FindFfmpeg(fixture,"")==executable,"Relative config resolves against app root.");File.Delete(Path.Combine(fixture,"ffmpeg-path.txt"));Require(RuntimeConfig.FindFfmpeg(fixture,Path.GetDirectoryName(executable))==executable,"PATH lookup must work.");File.Delete(executable);bool missing=false;try{RuntimeConfig.FindFfmpeg(fixture,"");}catch(FileNotFoundException){missing=true;}Require(missing,"Missing FFmpeg must fail clearly.");
  });
  Run("atomic_writer_has_no_shared_temporary_name",delegate{string file=Path.Combine(root,"logs","atomic-fixture.json");RuntimeConfig.AtomicWrite(file,"first");RuntimeConfig.AtomicWrite(file,"second");Require(File.ReadAllText(file)=="second" && Directory.GetFiles(Path.GetDirectoryName(file),"atomic-fixture.json.*.tmp").Length==0,"Atomic writes replace the result and clean temporary files.");});
  Run("logs_and_pending_dispatch_are_bounded",delegate{
   using(var form=new MainForm(true,false,null,null)){
    foreach(string timer in new[]{"timer","meterTimer","lifecycleTimer"})((System.Windows.Forms.Timer)Get(form,timer)).Stop();((NotifyIcon)Get(form,"tray")).Visible=false;
    form.StartPosition=FormStartPosition.Manual;form.Location=new Point(-10000,-10000);form.Opacity=0;form.Enabled=false;
    string payload=new string('x',2048);var worker=new Thread(delegate(){for(int i=0;i<10000;i++)Call(form,"AddLog",payload);});worker.Start();Require(worker.Join(10000),"Logging producer must complete without UI pumping.");
    Require((int)Get(form,"queuedLogCount")<=256,"Queued logs must have a hard limit.");Call(form,"DrainLogs");Require(((TextBox)Get(form,"log")).TextLength<=65536,"UI text must have a hard limit.");
    for(int batch=0;batch<6;batch++){worker=new Thread(delegate(){for(int i=0;i<256;i++)Call(form,"AddLog",payload);});worker.Start();worker.Join();Call(form,"DrainLogs");}
    string file=(string)Get(form,"logPath");Require(Directory.GetFiles(Path.GetDirectoryName(file),Path.GetFileName(file)+"*").Length<=4,"One session retains at most current file and three rotations.");Require(Directory.GetFiles(Path.GetDirectoryName(file),Path.GetFileName(file)+"*").All(p=>new FileInfo(p).Length<=2*1024*1024),"Each session log file must stay below 2MiB.");
    Call(form,"RequestExit");
   }
  });
  Run("invalid_route_record_has_no_setter_path",delegate{string record=RoutingSnapshot.PathName;File.WriteAllText(record,"<RoutingSnapshot><SourceId>fixture-source</SourceId></RoutingSnapshot>");bool rejected=false;try{RoutingSnapshot.Restore();}catch(InvalidOperationException){rejected=true;}Require(rejected && File.Exists(record),"An incomplete record must be rejected before reading or setting any defaults.");File.Delete(record);});
  Run("pending_timeout_retains_owner_then_exit_retries",delegate{
   string carrier=args.Length>1?args[1]:Path.Combine(root,"silence-ac3-640k.spdif");
   var pending=new BridgeEngine("fixture-no-source","fixture-no-output","fixture-no-encoder",File.ReadAllBytes(carrier),false);
   using(var form=new MainForm(true,false,null,null)){
    ((NotifyIcon)Get(form,"tray")).Visible=false;form.StartPosition=FormStartPosition.Manual;form.Location=new Point(-10000,-10000);form.Opacity=0;form.Enabled=false;form.Show();
    typeof(MainForm).GetField("pendingEngine",Flags).SetValue(form,pending);
    typeof(BridgeEngine).GetField("starting",Flags).SetValue(pending,true);typeof(BridgeEngine).GetField("startupThread",Flags).SetValue(pending,Thread.CurrentThread);
    Call(form,"RequestExit");Require(!form.IsDisposed && Object.ReferenceEquals(Get(form,"pendingEngine"),pending),"Timed-out disposal must retain both form and pending ownership.");Require(((System.Windows.Forms.Timer)Get(form,"lifecycleTimer")).Enabled,"Exit retry timer must remain active.");
    var notBefore=(DateTime)Get(form,"nextExitAttempt");Require(notBefore>DateTime.UtcNow.AddSeconds(1),"Cleanup retries must be rate limited.");
    typeof(BridgeEngine).GetField("starting",Flags).SetValue(pending,false);typeof(BridgeEngine).GetField("startupThread",Flags).SetValue(pending,null);
    var watch=Stopwatch.StartNew();while(!form.IsDisposed && watch.ElapsedMilliseconds<4500){Application.DoEvents();Thread.Sleep(10);}
    Require(form.IsDisposed && (bool)typeof(BridgeEngine).GetField("disposed",Flags).GetValue(pending),"The lifecycle timer must eventually dispose resources and finish exit.");
   }
  });
  var result=new{Schema="hd820.ui.lifecycle.deep-debug.20261103",Passed=failed==0,CaseCount=cases.Count,FailureCount=failed,Cases=cases,Boundaries=new[]{"No production Program.Main, production named IPC, startup setter, default route setter, audio start, playback, capture, or input injection was called.","MainForm constructor only reads endpoint/startup state; all fixture state resolves to this directory.","The button test replays documented StandardClick dispatch, rather than moving the system pointer."}};
  File.WriteAllText(args.Length>0?args[0]:Path.Combine(root,"ui-lifecycle-evidence.json"),new JavaScriptSerializer{MaxJsonLength=1000000}.Serialize(result));Console.WriteLine("Cases="+cases.Count+" Failures="+failed);return failed==0?0:1;
 }
}
