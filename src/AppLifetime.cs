using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace Hd820GameBridge {
 internal static class AppLifetime {
  internal const string MutexName = @"Local\HD820GameBridge_20261003";
  internal const string ShowEventName = @"Local\HD820GameBridge_Show_20261003";
  internal const string ExitEventName = @"Local\HD820GameBridge_Exit_20261003";
  internal const string StartupValueName = "HD820Game5_1Assistant";
  internal const string StartupKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
  internal const string StartupArguments = "--background --autostart";
  internal static string CurrentSid {get{using(var identity=WindowsIdentity.GetCurrent())return identity.User.Value;}}
  internal static string StartupTaskName {get{return StartupValueName+"_"+CurrentSid;}}

  sealed class SchedulerConnection : IDisposable {
   object service,folder;
   internal dynamic Folder {get{return folder;}}
   internal dynamic Service {get{return service;}}
   internal SchedulerConnection(){
    try{service=Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service",true));((dynamic)service).Connect();folder=((dynamic)service).GetFolder(@"\");}
    catch{Dispose();throw;}
   }
   public void Dispose(){Release(folder);Release(service);folder=service=null;}
  }
  static void Release(object value){if(value!=null && Marshal.IsComObject(value))Marshal.ReleaseComObject(value);}
  static bool IsCurrentUser(string user){
   if(String.IsNullOrEmpty(user))return false;
   if(String.Equals(user,CurrentSid,StringComparison.OrdinalIgnoreCase))return true;
   try{return ((SecurityIdentifier)new NTAccount(user).Translate(typeof(SecurityIdentifier))).Value==CurrentSid;}
   catch(IdentityNotMappedException){return false;}
  }
  internal static bool IsMissingTask(Exception error){
   // IDispatch can map HRESULT_FROM_WIN32(ERROR_FILE_NOT_FOUND) to
   // FileNotFoundException rather than COMException on a clean machine.
   return error!=null && error.HResult==unchecked((int)0x80070002);
  }
  static object FindTask(dynamic folder){return FindTask(folder,StartupTaskName);}
  internal static object FindTask(dynamic folder,string taskName){
   try{return folder.GetTask(taskName);}
   catch(Exception error){if(IsMissingTask(error))return null;throw;}
  }

  internal static string StartupCommand(string executable) {
   return "\"" + executable + "\" --background --autostart";
  }
  internal static bool StartupEnabled {
   get {
    using(var connection=new SchedulerConnection()){
     object taskObject=FindTask(connection.Folder),definitionObject=null,actionObject=null,triggerObject=null,principalObject=null,actionsObject=null,triggersObject=null;
     if(taskObject==null)return false;
     try{
      dynamic task=taskObject;definitionObject=task.Definition;dynamic definition=definitionObject;
      actionsObject=definition.Actions;triggersObject=definition.Triggers;dynamic actions=actionsObject,triggers=triggersObject;
      if(!task.Enabled || actions.Count!=1 || triggers.Count!=1)return false;
      actionObject=actions.Item(1);triggerObject=triggers.Item(1);principalObject=definition.Principal;
      dynamic action=actionObject,trigger=triggerObject,principal=principalObject;
      return action.Type==0 && String.Equals((string)action.Path,Application.ExecutablePath,StringComparison.OrdinalIgnoreCase)
       && (string)action.Arguments==StartupArguments && trigger.Type==9 && trigger.Enabled
       && IsCurrentUser((string)trigger.UserId) && IsCurrentUser((string)principal.UserId) && principal.LogonType==3 && principal.RunLevel==0;
     }finally{Release(principalObject);Release(triggerObject);Release(actionObject);Release(triggersObject);Release(actionsObject);Release(definitionObject);Release(taskObject);}
    }
   }
  }
  internal static void SetStartup(bool enabled) {
   using(var gate=new Mutex(false,MutexName+"_Startup_"+CurrentSid)){
   bool acquired=false;
   try{try{acquired=gate.WaitOne(5000);}catch(AbandonedMutexException){acquired=true;}if(!acquired)throw new TimeoutException("自启动设置正被另一个助手窗口更新，请稍后重试。");
   using(var connection=new SchedulerConnection()){
    if(enabled){
     object definitionObject=null,triggerObject=null,actionObject=null,registeredObject=null,infoObject=null,principalObject=null,settingsObject=null,triggersObject=null,actionsObject=null;
     try{
      definitionObject=connection.Service.NewTask(0);dynamic definition=definitionObject;
      infoObject=definition.RegistrationInfo;principalObject=definition.Principal;settingsObject=definition.Settings;triggersObject=definition.Triggers;actionsObject=definition.Actions;
      dynamic info=infoObject,principal=principalObject,settings=settingsObject,triggers=triggersObject,actions=actionsObject;
      info.Description="HD820游戏5.1助手：当前账号登录后在托盘自动开启5.1。";
      using(var identity=WindowsIdentity.GetCurrent())info.Author=identity.Name;
      principal.UserId=CurrentSid;principal.LogonType=3;principal.RunLevel=0;
      settings.Enabled=true;settings.StartWhenAvailable=true;
      settings.AllowDemandStart=true;settings.MultipleInstances=2;
      settings.DisallowStartIfOnBatteries=false;settings.StopIfGoingOnBatteries=false;
      settings.ExecutionTimeLimit="PT0S";
      triggerObject=triggers.Create(9);dynamic trigger=triggerObject;
      trigger.UserId=CurrentSid;trigger.Delay="PT15S";trigger.Enabled=true;
      actionObject=actions.Create(0);dynamic action=actionObject;
      action.Path=Application.ExecutablePath;action.Arguments=StartupArguments;
      action.WorkingDirectory=Path.GetDirectoryName(Application.ExecutablePath);
      // InteractiveToken keeps WASAPI capture and the tray in the logged-in user's session.
      registeredObject=connection.Folder.RegisterTaskDefinition(StartupTaskName,definition,6,CurrentSid,null,3,null);
     }finally{Release(registeredObject);Release(actionObject);Release(triggerObject);Release(actionsObject);Release(triggersObject);Release(settingsObject);Release(principalObject);Release(infoObject);Release(definitionObject);}
    }else{
     object taskObject=FindTask(connection.Folder);
     if(taskObject!=null)try{((dynamic)taskObject).Enabled=false;}finally{Release(taskObject);}
    }
   }
   // Remove the old Run entry only after successful registration/disable of our task.
   using (var key = Registry.CurrentUser.CreateSubKey(StartupKeyPath)) {
    key.DeleteValue(StartupValueName, false);
   }
   }finally{if(acquired)gate.ReleaseMutex();}
   }
  }
  internal static bool Signal(string name) {
   // The owner may still be constructing its UI when a second instance starts.
   for (int i = 0; i < 20; i++) {
    try { using (var signal = EventWaitHandle.OpenExisting(name)) return signal.Set(); }
    catch (WaitHandleCannotBeOpenedException) { Thread.Sleep(50); }
   }
   return false;
  }
 }
}
