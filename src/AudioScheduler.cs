using System;
using System.Runtime.InteropServices;

namespace Hd820GameBridge {
 internal sealed class AudioScheduler : IDisposable {
  [DllImport("avrt.dll",CharSet=CharSet.Unicode,SetLastError=true)]
  static extern IntPtr AvSetMmThreadCharacteristics(string taskName,out uint taskIndex);
  [DllImport("avrt.dll",SetLastError=true)]
  [return:MarshalAs(UnmanagedType.Bool)]
  static extern bool AvSetMmThreadPriority(IntPtr handle,int priority);
  [DllImport("avrt.dll",SetLastError=true)]
  [return:MarshalAs(UnmanagedType.Bool)]
  static extern bool AvRevertMmThreadCharacteristics(IntPtr handle);
  IntPtr handle;
  public string Status {get;private set;}
  public AudioScheduler(){
   try{uint index;handle=AvSetMmThreadCharacteristics("Audio",out index);
    if(handle==IntPtr.Zero){Status="MMCSS注册失败："+Marshal.GetLastWin32Error();return;}
    Status=AvSetMmThreadPriority(handle,1)?"MMCSS Audio / High":"MMCSS Audio；优先级设置失败："+Marshal.GetLastWin32Error();
   }catch(Exception e){Status="MMCSS不可用："+e.Message;}
  }
  public void Dispose(){if(handle!=IntPtr.Zero){AvRevertMmThreadCharacteristics(handle);handle=IntPtr.Zero;}}
 }
}
