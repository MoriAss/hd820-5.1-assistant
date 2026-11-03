# HD820 5.1 Assistant

Windows 上的实时多声道音频桥：把应用提供的 5.1 / 7.1 PCM 转成 AC3，通过支持该格式的 HD820 USB 数字输出送到解码器，再由六路模拟接口连接功放。

这是用户态音频助手，不替换厂商内核驱动，不修改 USB 硬件固件。当前版本为 **3.3.0**。

## 音频链路

```text
游戏 / 音乐 / 浏览器（应用须实际提供多声道 PCM）
  → Voicemeeter 多声道播放入口（48 kHz）
  → WASAPI loopback
  → 声道映射 → 音量 / 可选低音管理 → 时钟漂移校正
  → FFmpeg AC3 640 kbps → IEC 61937
  → HD820 支持 AC3 的独占输出 → 六路模拟 RCA → 5.1 音箱
```

- 5.1 声道保留，7.1 的侧后环绕按现有映射合并到 5.1。
- 六路 RMS / 峰值表取自编码前 PCM；它显示软件电平，不测量音箱声压。
- 可选的 80 Hz 低音补充把主声道低频加入 LFE，不创造原生 LFE 音轨。
- FPS 模式降低内部排队；设备自身缓冲和解码延迟仍然存在。
- 关闭窗口进入托盘，最小化保留任务栏；当前用户登录任务可自动运行。
- 停止时只恢复仍指向助手入口的默认角色，保留用户运行期间另行选择的输出。

## 需要什么

- Windows x64，.NET Framework 4.x（构建目标 C# 5）。
- 已安装、已配置为 48 kHz 多声道输出的 [Voicemeeter](https://vb-audio.com/Voicemeeter/)。本仓库不自动安装或修改第三方驱动。
- [FFmpeg](https://ffmpeg.org/download.html) Windows 可执行文件，须包含 `ac3` 编码器和 `spdif` muxer。
- HD820 及其能够接受独占 AC3 的 Windows 端点。仅凭设备名称不能确认兼容，其他 USB 声卡不保证可用。

首次在界面选择正确的多声道入口和 HD820 数字输出，确认硬件接线后启动。不要把编码载体发给普通扬声器端点。应用如果只提供双声道，本程序不会恢复不存在的独立环绕内容。

## 构建与运行

在可写目录解压或克隆，然后用 Windows PowerShell：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
```

输出位于 `dist`。把 `ffmpeg.exe` 放在 EXE 同目录或 `bin` 子目录，也可以加入 PATH。另可在 EXE 同目录的 `ffmpeg-path.txt` 中填写绝对路径，或相对于 EXE 目录的路径。

```powershell
# 只在本机构建时记录 FFmpeg 路径；该配置不会进入 Git。
.\build.ps1 -OutputRoot .\dist -FfmpegPath C:\tools\ffmpeg\bin\ffmpeg.exe
```

构建不会安装驱动、启用自启动或修改默认音频设备。正在运行的目标 EXE 不会被构建脚本覆盖。

命令行入口：

| 参数 | 作用 |
| --- | --- |
| `--background --autostart` | 后台启动并尝试连接已选链路 |
| `--show` | 显示控制面板或唤醒现有实例 |
| `--exit` | 请求现有实例退出并恢复路由 |
| `--enable-startup` / `--disable-startup` | 管理当前用户的登录任务 |
| `--list` | 将端点清单写入本机 `logs` |
| `--restore` | 按本机路由快照尝试恢复 |
| `--probe-output` | 初始化已选数字输出以检查支持情况，可能短暂占用设备 |
| `--test-speakers 18` | 在已确认的链路上播放逐声道测试音 |
| `--verify-chain` | 生成诊断测试信号并保存本机链路证据 |

音频测试命令会出声，不属于下面的离线测试。不要在同一输出上同时运行其他独占播放器。

## 测试

```powershell
python -m pip install -r .\tests\requirements.txt
python .\tests\run_tests.py --ffmpeg C:\tools\ffmpeg\bin\ffmpeg.exe
```

测试覆盖 PCM 映射、滤波、长时漂移模拟、AC3 编解码后六路分离、溢出时声道对齐、编码器退出、载体损坏拒绝、实时电平和界面生命周期。测试不启动物理音频输出、不调用生产 `Main` 或注册登录任务；结果位于被 Git 忽略的 `test-results`。

GitHub Actions 配置在 Windows runner 上构建并运行同一套测试。一次离线通过不代表所有设备兼容，也不能证明用户实际听到的间歇卡顿已消除。

## 当前边界

- AC3 是有损编码，每帧 1536 个采样，在 48 kHz 下本身为 32 ms；采集、缓冲和硬件还会增加延迟。本项目不宣称零延迟或已测得完整端到端延迟。
- 输出为普通 5.1；不保留 Atmos 对象或高度信息，不给应用伪造 Atmos 授权，也不绕过音乐服务保护。
- 一台 HD820 的历史实测支持六路独立出声，但不保证所有驱动、所有固件和所有应用。macOS / PS5 没有可用移植版。
- 旧版的间歇卡顿尚未归因。本轮修复明确的软件缺陷，并补充长间隙与 GC 关联指标；硬件端是否仍卡顿需要持续试听。
- 配置、设备 ID、运行日志、路由快照和个人文件不随仓库上传。

查看 [架构和诊断说明](docs/ARCHITECTURE.md)、[调试记录](docs/DEBUG-20261103.md) 和 [第三方依赖](THIRD_PARTY_NOTICES.md)。

此仓库公开提供源码，尚未附加开源许可证；不代表授予第三方依赖的分发许可。
