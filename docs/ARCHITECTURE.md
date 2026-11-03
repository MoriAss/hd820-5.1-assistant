# 架构和诊断

## 线程与队列

WASAPI loopback 线程读取虚拟播放入口，映射为 FL、FR、FC、LFE、BL、BR，再执行音量、低音管理与小幅重采样。编码器输入队列把完整六路 PCM 帧交给 FFmpeg 子进程，读线程按 6144 字节收集 IEC 61937 AC3 burst。输出线程通过独占 WASAPI 接口将合法 burst 写到 HD820。

所有 PCM 丢弃均以完整六路帧为单位。FPS 模式遇到应急积压可丢弃旧音频；这会造成真实时间连续性损失，不能以“延迟控制”掩盖。正常稳态依靠小幅时钟校准控制排队，不把定期丢包当作同步手段。

输出端只接收校验通过的 AC3 burst。暂时缺帧时尽量等待已有缓冲继续播放，接近耗尽才补合法 AC3 静音帧；不会把原始零 PCM 当作编码静音。

## 关键指标

`logs/live-state.json` 为最新状态；具体字段以源码为准。它不是录音，也不包含原始 PCM，但仍属于本机运行信息，不要未经检查直接公开。

| 字段 | 含义 |
| --- | --- |
| `CapturedFrames` / `EncodedBursts` / `RenderedBursts` | 各阶段累计处理量 |
| `Underruns` | 已有声音后因缺少编码帧而插入静音的次数 |
| `OutputEmptyEvents` | 观察到设备 padding 从非零变零的次数 |
| `DroppedBursts` / `EmergencyQueueDrops` | 编码 burst 队列应急丢弃 |
| `DroppedInputFrames` / `InputOverflows` | 编码输入积压或拒绝 |
| `Discontinuities` | WASAPI 输入报告的不连续标记；首包也可能带标记 |
| `MaxCaptureGapMs` / `MaxOutputGapMs` | 线程轮询观察到的最大间隔，不是声卡实测延迟 |
| `BufferedAudioMs` | 软件推算的待播放音频量 |
| `ClockCorrectionPpm` | 当前小幅时钟速率校正 |
| `MeterSnapshot` | 编码前六路数字 RMS / 峰值，单位 dBFS |

长间隙与 GC 指标用于时间关联，不能单独证明垃圾回收、系统休眠或 USB 是卡顿原因。没有软件欠载也不能证明解码器与模拟输出连续。

`Underruns` 与 `OutputEmptyEvents` 必须分开看：补入静音可以维持设备缓冲非空，前者增长时后者仍可能为零。每个替补帧为32ms；累计次数不能说明这些间隔是否连续，也不能单独对应某次主观卡顿。

## 排查次序

1. 记录用户实际听到卡顿的时刻，并确认应用是否切歌、暂停、切设备。
2. 对齐新鲜的遥测时间戳，比较计数增量；不要把数小时累计数字误当作当前复现。
3. 检查各阶段是否持续前进，再观察长间隙、padding、队列、GC 之间的关联。
4. 软件端正常时，继续查驱动、USB、解码器及模拟链路；若没有该层证据，保留未定位结论。

修改 FPS 缓冲作对照时记录原设置，测试后恢复。改变缓冲不等同于延迟补偿：实时游戏音频不能提前播放还未产生的声音。
