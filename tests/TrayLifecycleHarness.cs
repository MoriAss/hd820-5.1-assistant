using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

internal static class TrayLifecycleHarness {
 const BindingFlags InstanceFlags=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
 const BindingFlags StaticFlags=BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
 static Type formType,lifetimeType,bridgeType;
 static string targetPath,fixtureRoot;
 static readonly List<object> cases=new List<object>();
 static int failures,maximumVisibleWindows;
 static readonly List<Form> forms=new List<Form>();
 static readonly List<IDisposable> unstartedEngines=new List<IDisposable>();

 [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowProc callback,IntPtr data);
 delegate bool EnumWindowProc(IntPtr hwnd,IntPtr data);
 [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
 [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd,out uint pid);
 [DllImport("shell32.dll",SetLastError=true)] static extern IntPtr CommandLineToArgvW([MarshalAs(UnmanagedType.LPWStr)] string command,out int count);
 [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr value);

 [STAThread] static int Main(string[] args) {
  fixtureRoot=AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
  string output=Path.Combine(fixtureRoot,"tray-lifecycle-evidence.json");
  string hashBefore=null,hashAfter=null;
  try {
   if(args.Length!=1)throw new ArgumentException("Expected the production EXE path only.");
   targetPath=Path.GetFullPath(args[0]);
   // Isolation is enforced by a known fixture leaf and absence of routing/preferences.
   if(Path.GetFileName(fixtureRoot)!="tray_restore_fixture_20261028" && Path.GetFileName(fixtureRoot)!="tray_lifecycle_fixture_20261004")throw new InvalidOperationException("Unexpected AppDomain base directory.");
   if(File.Exists(Path.Combine(fixtureRoot,"routing-before.xml")) || File.Exists(Path.Combine(fixtureRoot,"bass-settings.txt")))throw new InvalidOperationException("Fixture contains routing/preferences; refusing constructor execution.");
   Directory.CreateDirectory(Path.Combine(fixtureRoot,"logs"));
   hashBefore=Hash(targetPath);
   var assembly=Assembly.LoadFrom(targetPath);
   formType=assembly.GetType("Hd820GameBridge.MainForm",true);
   lifetimeType=assembly.GetType("Hd820GameBridge.AppLifetime",true);
   bridgeType=assembly.GetType("Hd820GameBridge.BridgeEngine",true);
   Application.EnableVisualStyles();
   Application.SetCompatibleTextRenderingDefault(false);

   Run("background_handle_without_visible_window",delegate {
    var form=NewForm();
    form.Show();
    int visible=VisibleWindows();
    var detail=State(form);
    detail["NativeIsWindowVisible"]=IsWindowVisible(form.Handle);
    detail["VisibleProcessWindows"]=visible;
    Require(form.IsHandleCreated && !form.Visible && !form.ShowInTaskbar && !IsWindowVisible(form.Handle) && visible==0,"Background form must create its handle while remaining hidden.");
    Require(Get(form,"engine")==null,"Audio engine must remain null.");
    return detail;
   });

   UserClosing(false);UserClosing(true);
   SystemClosing(CloseReason.WindowsShutDown,false);
   SystemClosing(CloseReason.WindowsShutDown,true);
   SystemClosing(CloseReason.TaskManagerClosing,false);
   SystemClosing(CloseReason.TaskManagerClosing,true);

   Run("real_exit_disposes_tray",delegate {
    var form=NewForm();form.Show();
    var tray=(NotifyIcon)Get(form,"tray");
    bool trayDisposed=false;tray.Disposed+=delegate{trayDisposed=true;};
    bool before=tray.Visible;
    Invoke(form,"RequestExit");
    var detail=State(form);detail["TrayVisibleBefore"]=before;detail["TrayDisposedEvent"]=trayDisposed;
    Require(before && form.IsDisposed && trayDisposed && !tray.Visible && !TimerEnabled(form,"timer") && !TimerEnabled(form,"lifecycleTimer"),"Actual RequestExit must close the form, stop both timers, and dispose its tray icon.");
    return detail;
   });

   Run("busy_exit_defers_then_completed_state_can_close",delegate {
    var form=NewForm();form.Show();
    var tray=(NotifyIcon)Get(form,"tray");
    bool trayDisposed=false;tray.Disposed+=delegate{trayDisposed=true;};
    Set(form,"busy",true);Set(form,"autoStartPending",true);
    Invoke(form,"RequestExit");
    var deferred=State(form);
    Require(!form.IsDisposed && (bool)Get(form,"exitRequested") && CancellationSet(form) && !(bool)Get(form,"autoStartPending") && tray.Visible,"Busy exit must defer closure, signal startup cancellation, and record exit intent.");
    // Simulate only the UI completion state used by both worker completion callbacks.
    // Do not construct BridgeEngine or invoke StartBridge/worker/audio/routing operations.
    Set(form,"busy",false);form.Close();
    var completed=State(form);
    Require(form.IsDisposed && trayDisposed && !tray.Visible,"After busy clears, the completion Close must dispose the form and tray.");
    return new Dictionary<string,object>{{"Deferred",deferred},{"Completed",completed},{"TrayDisposedEvent",trayDisposed},{"CompletionMode","Reflection sets busy=false, then calls the same Form.Close operation as production completion callbacks; no worker/audio started."}};
   });

   Run("FinishStarted_after_exit_does_not_publish_or_restart_timers",delegate {
    var form=NewForm();form.Show();
    byte[] silence=File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(targetPath),"silence-ac3-640k.spdif"));
    var ctor=bridgeType.GetConstructor(InstanceFlags,null,new Type[]{typeof(string),typeof(string),typeof(string),typeof(byte[]),typeof(bool)},null);
    object created=ctor.Invoke(new object[]{"fixture-no-source","fixture-no-output",Path.Combine(fixtureRoot,"unused-ffmpeg.exe"),silence,false});
    unstartedEngines.Add((IDisposable)created);
    Require(bridgeType.GetField("captureThread",InstanceFlags).GetValue(created)==null && bridgeType.GetField("outputThread",InstanceFlags).GetValue(created)==null && bridgeType.GetField("encoder",InstanceFlags).GetValue(created)==null,"Only an unstarted engine may enter this fixture.");
    Set(form,"pendingEngine",created);Set(form,"busy",true);Set(form,"exitRequested",true);
    ((System.Windows.Forms.Timer)Get(form,"timer")).Stop();((System.Windows.Forms.Timer)Get(form,"lifecycleTimer")).Stop();
    Invoke(form,"FinishStarted",created,false);
    bool createdDisposed=(bool)bridgeType.GetField("disposed",InstanceFlags).GetValue(created);
    var detail=State(form);detail["UnstartedEngineDisposed"]=createdDisposed;detail["StartNeverInvoked"]=true;detail["SilenceByteLength"]=silence.Length;
    Require(Get(form,"engine")==null && Get(form,"pendingEngine")==null && !(bool)Get(form,"busy") && form.IsDisposed && createdDisposed && !TimerEnabled(form,"timer") && !TimerEnabled(form,"lifecycleTimer"),"Late FinishStarted after exit must dispose pending engine, never publish it or restart timers, and finish closure.");
    return detail;
   });

   WaitReadyCase("pre_cancel",false,true,false);
   WaitReadyCase("ready_and_cancel",true,true,false);
   WaitReadyCase("ready_passes",true,false,false);
   WaitReadyCase("cancel_while_waiting",false,false,true);

   Run("startup_command_chinese_spaces",delegate {
    string path=Path.Combine(fixtureRoot,"中文 空格 路径","HD820 游戏5.1助手.exe");
    string command=(string)lifetimeType.GetMethod("StartupCommand",StaticFlags).Invoke(null,new object[]{path});
    string[] parsed=ParseCommand(command);
    Require(command=="\""+path+"\" --background --autostart","Unexpected startup command.");
    Require(parsed.Length==3 && parsed[0]==path && parsed[1]=="--background" && parsed[2]=="--autostart","Windows command parser must preserve the entire Chinese/spaced executable path and both flags.");
    return new Dictionary<string,object>{{"Command",command},{"WindowsArgv",parsed},{"RegistryWritten",false},{"Validation","Legacy StartupCommand compatibility function only; CommandLineToArgvW, no real logon or production launch. v3.1 uses a scheduled task action rather than this Run command."}};
   });

   SignalReady("show");SignalReady("exit");
   Run("single_instance_signal_missing_random_event",delegate {
    string name=RandomName("missing");
    var clock=Stopwatch.StartNew();bool sent=Signal(name);clock.Stop();
    Require(!sent,"Missing random event must report failure.");
    return new Dictionary<string,object>{{"SignalReturned",sent},{"ElapsedMs",clock.ElapsedMilliseconds},{"OnlyRandomFixtureEvent",true}};
   });
   Run("single_instance_signal_retries_for_delayed_random_owner",delegate {
    string name=RandomName("delayed");bool sent=false;Exception threadError=null;
    var thread=new Thread(delegate(){try{sent=Signal(name);}catch(Exception error){threadError=error;}});
    thread.IsBackground=true;thread.Start();Thread.Sleep(125);
    using(var signal=new EventWaitHandle(false,EventResetMode.AutoReset,name)) {
     Require(thread.Join(2500),"Signal helper did not finish.");
     if(threadError!=null)throw threadError;
     bool observed=signal.WaitOne(1000);
     Require(sent && observed,"Signal helper must find the delayed random owner and set its event.");
     return new Dictionary<string,object>{{"SignalReturned",sent},{"WaitObserved",observed},{"OwnerDelayMs",125},{"OnlyRandomFixtureEvent",true}};
    }
   });

   Run("fixture_and_safety_boundaries",delegate {
    int visible=VisibleWindows();
    bool routeAbsent=!File.Exists(Path.Combine(fixtureRoot,"routing-before.xml"));
    bool preferencesAbsent=!File.Exists(Path.Combine(fixtureRoot,"bass-settings.txt"));
    bool allNull=true;foreach(var form in forms)if(Get(form,"engine")!=null)allNull=false;
    // Read only. In v3.1 this connects to Task Scheduler COM and compares the
    // registered action with Application.ExecutablePath (the fixture harness).
    bool startupGetter=(bool)lifetimeType.GetProperty("StartupEnabled",StaticFlags).GetValue(null,null);
    hashAfter=Hash(targetPath);
    Require(routeAbsent && preferencesAbsent && allNull && visible==0 && maximumVisibleWindows==0 && hashAfter==hashBefore,"Fixture or audio/window/production immutability boundary failed.");
    return new Dictionary<string,object>{{"AppDomainBaseDirectory",AppDomain.CurrentDomain.BaseDirectory},{"FixtureRoutingFileAbsent",routeAbsent},{"FixturePreferencesAbsent",preferencesAbsent},{"AllEngineFieldsNull",allNull},{"VisibleProcessWindows",visible},{"MaximumSampledVisibleProcessWindows",maximumVisibleWindows},{"ProductionExeUnchanged",hashAfter==hashBefore},{"RegistrySetterInvoked",false},{"StartupSetterInvoked",false},{"TaskSchedulerMutationInvoked",false},{"ReadOnlyStartupGetterSucceeded",true},{"StartupEnabledGetterValueInFixture",startupGetter},{"StartupGetterExecutablePath",Application.ExecutablePath},{"StartupGetterScope","Getter compares scheduled action against fixture Application.ExecutablePath; a false value here is not evidence that the production task is disabled."},{"ProductionProgramMainInvoked",false},{"ProductionGlobalIpcCreatedOrSignaled",false}};
   });
  } catch(Exception error) {
   failures++;cases.Add(new Dictionary<string,object>{{"Name","fatal"},{"Passed",false},{"Error",Flatten(error)}});
  } finally {
   foreach(var form in forms)try {
    Set(form,"busy",false);Set(form,"exitRequested",true);
    var tray=(NotifyIcon)Get(form,"tray");tray.Visible=false;tray.Dispose();
    ((System.Windows.Forms.Timer)Get(form,"timer")).Stop();
    ((System.Windows.Forms.Timer)Get(form,"timer")).Dispose();
    ((System.Windows.Forms.Timer)Get(form,"lifecycleTimer")).Stop();
    ((System.Windows.Forms.Timer)Get(form,"lifecycleTimer")).Dispose();
    ((ManualResetEvent)Get(form,"startupCancellation")).Dispose();
    ((ContextMenuStrip)Get(form,"trayMenu")).Dispose();form.Dispose();
   } catch {}
   foreach(var unstarted in unstartedEngines)try{unstarted.Dispose();}catch{}
   var result=new Dictionary<string,object>{{"Schema","hd820.tray.lifecycle.fixture.v3"},{"CheckedAt",DateTimeOffset.Now.ToString("o")},{"TargetExe",targetPath},{"ExeSha256Before",hashBefore},{"ExeSha256After",hashAfter},{"FixtureBaseDirectory",AppDomain.CurrentDomain.BaseDirectory},{"FailureCount",failures},{"CaseCount",cases.Count},{"Passed",failures==0},{"Cases",cases},{"Boundaries",new string[]{"Production EXE loaded by reflection; production Program.Main never invoked.","MainForm constructor performs read-only startup status checks (Task Scheduler COM in v3.1; legacy HKCU Run in v3) and read-only audio endpoint enumeration; autoStart=false and published engine remains null.","StartupEnabled is also directly read by reflection; Application.ExecutablePath is the fixture harness, so its false value does not mean the production scheduled task is disabled.","All settings/routing paths resolve to the fixture AppDomain base, with routing/preferences absent.","No StartBridge, BridgeEngine.Start, virtual stimulus, default-route setter, AppLifetime.SetStartup, Task Scheduler mutation, or production named IPC operation invoked.","One BridgeEngine constructed with validated silence bytes and dummy endpoint/ffmpeg paths only to test pending completion cancellation; Start is never invoked.","No Application.Run/DoEvents; lifecycle handlers invoked directly on the STA thread, and only hidden forms are created.","WindowsShutDown and TaskManagerClosing are synthetic FormClosingEventArgs, not real OS shutdown/task termination.","Busy and completion states are reflection fixtures, not a real audio worker race or route restoration test.","WaitUntilReady uses fixture ManualResetEvents and a cancellation-only helper thread; no audio thread.","StartupCommand quoting tests legacy compatibility via CommandLineToArgvW; v3.1 uses a scheduled task action, and task registration/toggle/real logon startup are outside this fixture scope."}}};
   File.WriteAllText(output,new JavaScriptSerializer{MaxJsonLength=1000000}.Serialize(result),new System.Text.UTF8Encoding(false));
  }
  return failures==0?0:1;
 }

 static Form NewForm() {
  var ctor=formType.GetConstructor(InstanceFlags,null,new Type[]{typeof(bool),typeof(bool),typeof(EventWaitHandle),typeof(EventWaitHandle)},null);
  var form=(Form)ctor.Invoke(new object[]{true,false,null,null});forms.Add(form);
  Require(Get(form,"engine")==null && !(bool)Get(form,"autoStartPending"),"Fixture form must not start audio or autostart.");
  Require(VisibleWindows()==0,"Fixture constructor must not show a window.");
  return form;
 }
 static void UserClosing(bool busy) {
  Run("user_closing_hides_without_exit_"+(busy?"busy":"idle"),delegate {
   var form=NewForm();form.Show();Set(form,"busy",busy);
   // A visible-state intent is simulated, without ever displaying a test window.
   Set(form,"startHidden",false);form.ShowInTaskbar=true;
   var closing=new FormClosingEventArgs(CloseReason.UserClosing,false);
   Invoke(form,"HandleClosing",form,closing);
   var detail=State(form);detail["Cancel"]=closing.Cancel;
   Require(closing.Cancel && !form.Visible && !IsWindowVisible(form.Handle) && (bool)Get(form,"startHidden") && !(bool)Get(form,"exitRequested") && !CancellationSet(form) && !form.IsDisposed,"UserClosing must hide/cancel without requesting real exit or cancelling startup.");
   Require((bool)Get(form,"busy")==busy && ((NotifyIcon)Get(form,"tray")).Visible,"Hide must preserve busy state and tray.");
   return detail;
  });
 }
 static void SystemClosing(CloseReason reason,bool busy) {
  Run(reason.ToString()+"_does_not_cancel_"+(busy?"busy":"idle"),delegate {
   var form=NewForm();form.Show();Set(form,"busy",busy);Set(form,"autoStartPending",true);
   var closing=new FormClosingEventArgs(reason,false);Invoke(form,"HandleClosing",form,closing);
   var detail=State(form);detail["Cancel"]=closing.Cancel;detail["Reason"]=reason.ToString();
   Require(!closing.Cancel && (bool)Get(form,"exitRequested") && CancellationSet(form) && !(bool)Get(form,"autoStartPending") && !((NotifyIcon)Get(form,"tray")).Visible && !TimerEnabled(form,"timer") && !TimerEnabled(form,"lifecycleTimer"),"System close must not cancel even if busy, and must cancel startup/stop both timers/hide tray.");
   return detail;
  });
 }
 static void WaitReadyCase(string name,bool initiallyReady,bool initiallyCancelled,bool delayedCancellation) {
  Run("WaitUntilReady_"+name,delegate {
   using(var ready=new ManualResetEvent(initiallyReady))using(var cancel=new ManualResetEvent(initiallyCancelled)) {
    Thread signalThread=null;
    if(delayedCancellation){signalThread=new Thread(delegate(){Thread.Sleep(125);cancel.Set();});signalThread.IsBackground=true;signalThread.Start();}
    var clock=Stopwatch.StartNew();Exception observed=null;
    try{bridgeType.GetMethod("WaitUntilReady",StaticFlags).Invoke(null,new object[]{ready,cancel,"fixture timeout"});}catch(TargetInvocationException error){observed=error.InnerException;}
    clock.Stop();if(signalThread!=null)Require(signalThread.Join(2000),"Fixture cancellation helper did not finish.");
    bool shouldCancel=initiallyCancelled || delayedCancellation;
    Require(shouldCancel?observed is OperationCanceledException:observed==null,"Unexpected WaitUntilReady cancellation/ready result.");
    Require(clock.ElapsedMilliseconds<1500,"Fixture cancellation/readiness should not wait for the six-second production timeout.");
    return new Dictionary<string,object>{{"InitiallyReady",initiallyReady},{"InitiallyCancelled",initiallyCancelled},{"DelayedCancellation",delayedCancellation},{"ObservedException",observed==null?null:observed.GetType().FullName},{"ElapsedMs",clock.ElapsedMilliseconds},{"OnlyFixtureEvents",true}};
   }
  });
 }
 static void SignalReady(string action) {
  Run("single_instance_"+action+"_signal_random_event",delegate {
   string name=RandomName(action);
   using(var signal=new EventWaitHandle(false,EventResetMode.AutoReset,name)) {
    bool sent=Signal(name),observed=signal.WaitOne(1000),autoReset=!signal.WaitOne(0);
    Require(sent && observed && autoReset,"Signal helper must set only the random AutoReset event.");
    return new Dictionary<string,object>{{"Action",action},{"SignalReturned",sent},{"WaitObserved",observed},{"AutoReset",autoReset},{"OnlyRandomFixtureEvent",true}};
   }
  });
 }
 static bool Signal(string name){return (bool)lifetimeType.GetMethod("Signal",StaticFlags).Invoke(null,new object[]{name});}
 static string RandomName(string suffix){return "Local\\HD820TrayLifecycleFixture_"+Guid.NewGuid().ToString("N")+"_"+suffix;}
 static object Get(object value,string name){return formType.GetField(name,InstanceFlags).GetValue(value);}
 static void Set(object value,string name,object fieldValue){formType.GetField(name,InstanceFlags).SetValue(value,fieldValue);}
 static void Invoke(object value,string name,params object[] args){formType.GetMethod(name,InstanceFlags).Invoke(value,args);}
 static bool TimerEnabled(Form form,string field){return ((System.Windows.Forms.Timer)Get(form,field)).Enabled;}
 static bool CancellationSet(Form form){return ((ManualResetEvent)Get(form,"startupCancellation")).WaitOne(0);}
 static object CancellationState(Form form){try{return (object)CancellationSet(form);}catch(ObjectDisposedException){return "Disposed";}}
 static Dictionary<string,object> State(Form form){return new Dictionary<string,object>{{"HandleCreated",form.IsHandleCreated},{"Visible",form.Visible},{"ShowInTaskbar",form.ShowInTaskbar},{"IsDisposed",form.IsDisposed},{"ExitRequested",Get(form,"exitRequested")},{"StartupCancellation",CancellationState(form)},{"Busy",Get(form,"busy")},{"StartHidden",Get(form,"startHidden")},{"AutoStartPending",Get(form,"autoStartPending")},{"TrayVisible",((NotifyIcon)Get(form,"tray")).Visible},{"EngineNull",Get(form,"engine")==null},{"PendingEngineNull",Get(form,"pendingEngine")==null},{"AudioTimerEnabled",TimerEnabled(form,"timer")},{"LifecycleTimerEnabled",TimerEnabled(form,"lifecycleTimer")}};}
 static void Run(string name,Func<object> action){try{object detail=action();cases.Add(new Dictionary<string,object>{{"Name",name},{"Passed",true},{"Detail",detail}});}catch(Exception error){failures++;cases.Add(new Dictionary<string,object>{{"Name",name},{"Passed",false},{"Error",Flatten(error)}});}}
 static void Require(bool value,string message){if(!value)throw new InvalidOperationException(message);}
 static string Flatten(Exception error){while(error is TargetInvocationException && error.InnerException!=null)error=error.InnerException;return error.ToString();}
 static string Hash(string path){using(var hash=SHA256.Create())using(var file=File.OpenRead(path))return BitConverter.ToString(hash.ComputeHash(file)).Replace("-","");}
 static int VisibleWindows(){int count=0;uint mine=(uint)Process.GetCurrentProcess().Id;EnumWindows(delegate(IntPtr hwnd,IntPtr data){uint pid;GetWindowThreadProcessId(hwnd,out pid);if(pid==mine && IsWindowVisible(hwnd))count++;return true;},IntPtr.Zero);maximumVisibleWindows=Math.Max(maximumVisibleWindows,count);return count;}
 static string[] ParseCommand(string command){int count;IntPtr values=CommandLineToArgvW(command,out count);if(values==IntPtr.Zero)throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());try{var result=new string[count];for(int i=0;i<count;i++)result[i]=Marshal.PtrToStringUni(Marshal.ReadIntPtr(values,i*IntPtr.Size));return result;}finally{LocalFree(values);}}
}
