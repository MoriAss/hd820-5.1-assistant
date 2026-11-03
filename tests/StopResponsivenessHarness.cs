using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Hd820GameBridge;

internal static class StopResponsivenessHarness {
 const BindingFlags Flags=BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public;
 static void Set(object value,string field,object data){value.GetType().GetField(field,Flags).SetValue(value,data);}
 static object Get(object value,string field){return value.GetType().GetField(field,Flags).GetValue(value);}
 [STAThread]static int Main(string[] args){
  string root=AppDomain.CurrentDomain.BaseDirectory;
  if(File.Exists(Path.Combine(root,"routing-before.xml")) || File.Exists(Path.Combine(root,"bass-settings.txt")))throw new InvalidOperationException("Isolated fixture only.");
  Directory.CreateDirectory(Path.Combine(root,"logs"));
  using(var gate=new ManualResetEvent(false))using(var form=new MainForm(true,false,null,null)){
   ((NotifyIcon)Get(form,"tray")).Visible=false;form.StartPosition=FormStartPosition.Manual;form.Location=new System.Drawing.Point(-10000,-10000);form.Opacity=0;form.Show();
   var engine=new BridgeEngine("fixture-no-source","fixture-no-output","unused",File.ReadAllBytes(args[0]),false);
   var simulatedWorker=new Thread(delegate(){gate.WaitOne();}){IsBackground=true};simulatedWorker.Start();Set(engine,"captureThread",simulatedWorker);Set(form,"engine",engine);
   int ticks=0;using(var pulse=new System.Windows.Forms.Timer{Interval=20}){
    pulse.Tick+=delegate{ticks++;};pulse.Start();
    bool busyWhileWaiting=false;
    var release=new Thread(delegate(){Thread.Sleep(100);busyWhileWaiting=(bool)Get(form,"busy");Thread.Sleep(250);gate.Set();}){IsBackground=true};release.Start();
    var watch=Stopwatch.StartNew();typeof(MainForm).GetMethod("StopBridge",Flags).Invoke(form,null);long callMs=watch.ElapsedMilliseconds;
    var completion=Stopwatch.StartNew();while((bool)Get(form,"busy") && completion.ElapsedMilliseconds<3000){Application.DoEvents();Thread.Sleep(5);}Application.DoEvents();
    bool cleanup=Get(form,"engine")==null && !simulatedWorker.IsAlive;
    bool expected=callMs>=250 && (args.Length>1 && args[1]=="baseline"?!busyWhileWaiting:busyWhileWaiting);
    Set(form,"exitRequested",true);form.Close();
    Console.WriteLine(new JavaScriptSerializer().Serialize(new{Passed=expected&&cleanup,StopCallMs=callMs,BusyDuringWait=busyWhileWaiting,UiTimerTicks=ticks,CleanupComplete=cleanup,Scope="Never started engine, audio hardware, routes, startup setters or production IPC; synthetic capture worker held 350ms. Stop remains bounded synchronous; this verifies the reentry guard, not asynchronous responsiveness."}));return expected&&cleanup?0:1;
   }
  }
 }
}
