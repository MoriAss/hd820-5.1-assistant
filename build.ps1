param([string]$OutputRoot, [string]$FfmpegPath)
$ErrorActionPreference='Stop'
$sourceRoot=$PSScriptRoot
if(Test-Path -LiteralPath (Join-Path $PSScriptRoot 'src\CoreAudio.cs')){$sourceRoot=Join-Path $PSScriptRoot 'src'}
if(-not $OutputRoot){$OutputRoot=Join-Path $PSScriptRoot 'dist'}
$OutputRoot=[IO.Path]::GetFullPath($OutputRoot)
New-Item -ItemType Directory -Path $OutputRoot -Force | Out-Null
$compilerPath=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if(-not (Test-Path -LiteralPath $compilerPath)){throw 'Windows x64 with .NET Framework 4.x is required.'}
$running=@(Get-CimInstance Win32_Process -Filter "Name='HD820游戏5.1助手.exe'" -ErrorAction SilentlyContinue | Where-Object {$_.ExecutablePath -eq (Join-Path $OutputRoot 'HD820游戏5.1助手.exe')})
if($running.Count -gt 0){throw 'The destination executable is running. Build to a new folder.'}
$sources=@('CoreAudio.cs','BassManager.cs','AudioScheduler.cs','PcmClockSynchronizer.cs','ChannelMeter.cs','ChannelMeterControl.cs','ModernUi.cs','BridgeEngine.cs','VirtualStimulus.cs','Program.cs','AppLifetime.cs','encoder\RealtimeAc3Encoder.cs','reviewer\AudioPolicy.cs') | ForEach-Object {Join-Path $sourceRoot $_}
foreach($optional in @('RuntimeConfig.cs','AssemblyInfo.cs','Preflight.cs')){if(Test-Path -LiteralPath (Join-Path $sourceRoot $optional)){$sources+=Join-Path $sourceRoot $optional}}
$iconPath=Join-Path $sourceRoot 'icon.ico'
& $compilerPath /nologo /target:winexe /platform:x64 /optimize+ /codepage:65001 /langversion:5 /win32icon:$iconPath /r:System.dll /r:System.Core.dll /r:Microsoft.CSharp.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Xml.dll /r:System.Web.Extensions.dll ('/out:'+(Join-Path $OutputRoot 'HD820游戏5.1助手.exe')) $sources
if($LASTEXITCODE -ne 0){throw 'C# compilation failed'}
Copy-Item -LiteralPath (Join-Path $sourceRoot 'encoder\silence-ac3-640k.spdif') -Destination $OutputRoot -Force
if($FfmpegPath){
 $FfmpegPath=(Resolve-Path -LiteralPath $FfmpegPath).Path
 if(-not (Test-Path -LiteralPath $FfmpegPath -PathType Leaf)){throw 'FFmpeg path must be an executable file.'}
 [IO.File]::WriteAllText((Join-Path $OutputRoot 'ffmpeg-path.txt'),$FfmpegPath,(New-Object Text.UTF8Encoding($false)))
}
Get-Item -LiteralPath (Join-Path $OutputRoot 'HD820游戏5.1助手.exe') | Select-Object FullName,Length
