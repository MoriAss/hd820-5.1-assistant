using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

internal static class TrayRestoreHarness {
 const BindingFlags Flags=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
 static Type type;static readonly List<object> cases=new List<object>();static int failures;static readonly List<object> events=new List<object>();static readonly Stopwatch timeline=Stopwatch.StartNew();
 [DllImport("user32.dll")]static extern IntPtr GetForegroundWindow();
 [DllImport("user32.dll")]static extern bool IsIconic(IntPtr window);
 static object Get(Form form,string field){return type.GetField(field,Flags).GetValue(form);}
 static void Set(Form form,string field,object value){type.GetField(field,Flags).SetValue(form,value);}
 static void Invoke(Form form,string method,params object[] args){type.GetMethod(method,Flags).Invoke(form,args);}
 static void Require(bool condition,string message){if(!condition)throw new InvalidOperationException(message);}
 static void Pump(int ms){var watch=Stopwatch.StartNew();while(watch.ElapsedMilliseconds<ms){Application.DoEvents();Thread.Sleep(5);}}
 static Form NewForm(){
  string root=AppDomain.CurrentDomain.BaseDirectory;
  Require(!File.Exists(Path.Combine(root,"routing-before.xml"))&&!File.Exists(Path.Combine(root,"bass-settings.txt")),"Fixture must have no production route/preferences.");
  var ctor=type.GetConstructor(Flags,null,new Type[]{typeof(bool),typeof(bool),typeof(EventWaitHandle),typeof(EventWaitHandle)},null);
  var form=(Form)ctor.Invoke(new object[]{true,false,null,null});
  foreach(string name in new[]{"timer","lifecycleTimer","meterTimer"})((System.Windows.Forms.Timer)Get(form,name)).Stop();
  ((NotifyIcon)Get(form,"tray")).Visible=false;
  form.StartPosition=FormStartPosition.Manual;form.Location=new Point(-10000,-10000);form.Opacity=0;form.Enabled=false;
  form.Resize+=delegate{Trace(form,"Resize");};form.VisibleChanged+=delegate{Trace(form,"VisibleChanged");};form.HandleCreated+=delegate{Trace(form,"HandleCreated");};
  form.Show();Require(!form.Visible,"Background constructor must remain hidden.");Require(Get(form,"engine")==null&&!(bool)Get(form,"autoStartPending"),"Fixture must never run audio.");
  return form;
 }
 static void Trace(Form form,string name){var restoring=type.GetField("restoringWindow",Flags);var hiding=type.GetField("hidingToTray",Flags);events.Add(new{At=timeline.ElapsedMilliseconds,Event=name,Visible=form.Visible,State=form.WindowState.ToString(),Taskbar=form.ShowInTaskbar,StartHidden=Get(form,"startHidden"),Restoring=restoring==null?null:restoring.GetValue(form),Hiding=hiding==null?null:hiding.GetValue(form)});}
 static Dictionary<string,object> State(Form form){return new Dictionary<string,object>{{"Visible",form.Visible},{"WindowState",form.WindowState.ToString()},{"ShowInTaskbar",form.ShowInTaskbar},{"StartHidden",Get(form,"startHidden")},{"IsDisposed",form.IsDisposed},{"EngineNull",Get(form,"engine")==null},{"NativeIsIconic",IsIconic(form.Handle)},{"MeterTimerEnabled",((System.Windows.Forms.Timer)Get(form,"meterTimer")).Enabled},{"Opacity",form.Opacity},{"Enabled",form.Enabled},{"Location",form.Location.ToString()}};}
 static void Restored(Form form){Require(form.Visible&&form.WindowState==FormWindowState.Normal&&!IsIconic(form.Handle)&&form.ShowInTaskbar&&!(bool)Get(form,"startHidden"),"Restore must leave a visible Normal non-iconic taskbar window with no suppression flag.");Require(((System.Windows.Forms.Timer)Get(form,"meterTimer")).Enabled,"Visible restored meter timer must run.");Require(Get(form,"engine")==null,"Audio engine must remain null.");}
 static void DisposeForm(Form form){try{Set(form,"exitRequested",true);form.Close();form.Dispose();}catch{}}
 static void Run(string name,Action<Form> setup,Action<Form> beforeRestore){
  Form form=null;IntPtr foreground=GetForegroundWindow();Dictionary<string,object> immediate=null;events.Clear();
  try{form=NewForm();setup(form);if(beforeRestore!=null)beforeRestore(form);Invoke(form,"ShowControlPanel");immediate=State(form);Restored(form);Pump(2050);Restored(form);cases.Add(new{Name=name,Passed=true,Immediate=immediate,AfterTwoSeconds=State(form),ForegroundUnchanged=GetForegroundWindow()==foreground,Events=events.ToArray()});}
  catch(Exception e){failures++;while(e is TargetInvocationException&&e.InnerException!=null)e=e.InnerException;cases.Add(new{Name=name,Passed=false,Immediate=immediate,State=form==null?null:State(form),Events=events.ToArray(),Error=e.ToString()});}
  finally{if(form!=null)DisposeForm(form);}
 }
 [STAThread]static int Main(string[] args){
  if(args.Length!=2)return 2;Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
  type=Assembly.LoadFrom(Path.GetFullPath(args[0])).GetType("Hd820GameBridge.MainForm",true);
  Run("background_restore",delegate(Form f){},null);
  Run("minimized_hidden_restore",delegate(Form f){f.WindowState=FormWindowState.Minimized;Invoke(f,"HideToTray");},null);
  Run("native_minimize_keeps_taskbar",delegate(Form f){Invoke(f,"ShowControlPanel");f.WindowState=FormWindowState.Minimized;Pump(50);Require(f.Visible&&f.WindowState==FormWindowState.Minimized&&f.ShowInTaskbar&&!(bool)Get(f,"startHidden"),"Native minimize must keep the form available in the taskbar.");Require(!((System.Windows.Forms.Timer)Get(f,"meterTimer")).Enabled,"Minimized meter drawing must stop.");},null);
  Run("user_closing_hidden_restore",delegate(Form f){Set(f,"startHidden",false);var closing=new FormClosingEventArgs(CloseReason.UserClosing,false);Invoke(f,"HandleClosing",f,closing);Require(closing.Cancel&&!f.Visible&&!f.ShowInTaskbar,"X close must still hide.");},null);
  Run("native_handle_resize_reentry",delegate(Form f){f.WindowState=FormWindowState.Minimized;Invoke(f,"HideToTray");},delegate(Form f){
   bool injected=false;f.HandleCreated+=delegate{if(!injected&&f.ShowInTaskbar){injected=true;f.WindowState=FormWindowState.Minimized;typeof(Form).GetMethod("OnResize",Flags).Invoke(f,new object[]{EventArgs.Empty});}};
  });
  Run("nested_hide_reentry",delegate(Form f){f.WindowState=FormWindowState.Minimized;Invoke(f,"HideToTray");},delegate(Form f){
   bool injected=false;f.HandleCreated+=delegate{if(!injected&&f.ShowInTaskbar){injected=true;Invoke(f,"HideToTray");}};
  });
  Run("repeat_close_restore_ten_times",delegate(Form f){
   for(int i=0;i<10;i++){Invoke(f,"ShowControlPanel");Restored(f);IntPtr handle=f.Handle;var closing=new FormClosingEventArgs(CloseReason.UserClosing,false);Invoke(f,"HandleClosing",f,closing);Pump(20);Require(closing.Cancel&&!f.Visible&&f.ShowInTaskbar&&(bool)Get(f,"startHidden")&&!((System.Windows.Forms.Timer)Get(f,"meterTimer")).Enabled,"X close must hide while preserving taskbar eligibility and stop meter drawing.");Require(f.Handle==handle,"Tray hiding must not recreate the native handle.");Invoke(f,"ShowControlPanel");Require(f.Handle==handle,"Repeated tray restoration must keep the native handle.");}
  },null);
  Run("restore_after_exit_is_ignored",delegate(Form f){Set(f,"exitRequested",true);Invoke(f,"ShowControlPanel");Require(!f.Visible&&!f.ShowInTaskbar,"Exit intent must prevent reopening.");Set(f,"exitRequested",false);},null);
  var result=new{Schema="hd820.tray.restore.fixture.20261028",Target=Path.GetFullPath(args[0]),Passed=failures==0,FailureCount=failures,CaseCount=cases.Count,Cases=cases,Boundaries=new[]{"Production Program.Main, global IPC, audio engine Start, routing setter and startup setter never called.","Forms are disabled, fully transparent, offscreen; foreground equality is recorded but not a correctness assertion.","All routing/preferences resolve to isolated fixture directory and must be absent.","MainForm constructor may only enumerate audio endpoints and read startup status.","Message loop pumps only fixture forms; no input injection or other-app UI operations."}};
  File.WriteAllText(args[1],new JavaScriptSerializer{MaxJsonLength=1000000}.Serialize(result));Console.WriteLine("Cases="+cases.Count+" Failures="+failures);return failures==0?0:1;
 }
}
