# 开发者说明

普通使用者先看[首页安装指南](../README.md)。本项目使用Windows .NET Framework / C# 5，无需另装.NET SDK。

## 构建

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\build.ps1 -OutputRoot .\dist
```

可用`-FfmpegPath C:\tools\ffmpeg\bin\ffmpeg.exe`为本地构建写入路径。寻找顺序为`ffmpeg-path.txt`、程序同目录、`bin`子目录、PATH。已配置路径失效时明确报错，不静默换用其他位置。

脚本拒绝覆盖正在运行的目标EXE。设置、日志、设备ID和路由快照不进入发布包。单实例机制下，打开另一个版本的快捷方式不代表运行中的版本已替换。

## 命令行入口

| 参数 | 作用 |
| --- | --- |
| `--background --autostart` | 后台启动并尝试连接已选链路 |
| `--show` | 显示面板或唤醒现有实例 |
| `--exit` | 请求退出并恢复路由 |
| `--enable-startup` / `--disable-startup` | 管理当前用户登录任务 |
| `--list` | 将端点清单写入本机logs |
| `--restore` | 按路由快照尝试恢复 |
| `--probe-output` | 初始化所选数字输出检查支持，可能短暂占用设备 |
| `--test-speakers 18` | 在已确认链路上播放逐声道测试音 |
| `--verify-chain` | 生成诊断信号并保存本机链路证据 |

最后三个命令涉及实际音频链路，不属于离线回归。不要将编码载体送到未经确认的普通扬声器端点。

## 测试

```powershell
python -m pip install -r .\tests\requirements.txt
python .\tests\run_tests.py --ffmpeg C:\tools\ffmpeg\bin\ffmpeg.exe
```

结果保存在忽略的`test-results`。同一套测试由Windows GitHub Actions运行，覆盖13组回归和AC3编解码断言；UTF-8带/无BOM均核验实际六路内容。

源码和程序包附MIT LICENSE与第三方说明。FFmpeg、Voicemeeter驱动另行安装；不能将离线通过当成USB、解码器或模拟输出连续的证明，见[调试记录](DEBUG-20261103.md)。
