# 同频 AudioLink

Windows 多音频设备共享程序。捕获一个播放设备的系统声音，同时发送到多个耳机、音箱、USB / HDMI 输出或已配对的安卓手机。

## 下载

前往 **[Releases](https://github.com/Tsukimig/AudioLink/releases/latest)** 下载可运行版本。

| 文件 | 用途 |
| --- | --- |
| `AudioLink-Windows-x64-1.4.3.zip` | Windows x64 便携版，包含 .NET 桌面运行环境 |
| `AudioLink-Android-1.4.0.apk` | Android 8.0 及以上的配套接收端 |
| `SHA256SUMS.txt` | 下载文件的 SHA-256 校验值 |
| `release-manifest.json` | 版本号及对应源码提交 |

Windows 发布包完整解压后运行 `AudioLink.exe`，保留同目录依赖。APK 也包含在 Windows 包的 `Android` 文件夹。GitHub 的 **Source code** 是源码归档，不能直接运行。

电脑端当前版本 **1.4.3**，手机端当前版本 **1.4.0**。版本号分别管理，现有手机端无需随此次电脑更新重新安装。

## 功能

- 同时启用多个音频输出，分别调整音量、静音和 0–500 ms 同步微调。
- 设备发现、热插拔和断线恢复，保存设备选择及同步设置。
- 低延迟、均衡和高稳定模式；不同采样率与常见声道格式适配。
- 原音源同步模式：使用已有虚拟设备中转，使原音源设备也能追加延迟。
- 安卓接收端：通过 USB 网络共享或可信局域网播放电脑声音。
- 历史设备管理：清除历史 → 勾选 → 确认删除，支持取消。

1.4.3 已移除共享设备的全选按钮，各输出通过独立开关选择。详细更新见 [发布说明](docs/releases/v1.4.3.md)。

## 使用

1. 在「系统音源」选择正在播放声音的设备，通常为 Windows 默认输出。
2. 在「共享到」逐一开启所需设备，可同时开启多个。
3. 点击「开始共享」。设备滑块调整对应 Windows 设备音量；「共享总音量」调整程序转发的声音。
4. 给较快设备增加「同步微调」，对齐较慢设备；追求最低延迟时设为 0。

`Ctrl + Space` 开始 / 停止，`F5` 刷新。关闭窗口停止共享。设置及滚动日志位于 `%LOCALAPPDATA%\AudioLink`，音频和设置不自动上传。

### 原音源同步

在左侧打开「音源同步」，选择**已安装的虚拟播放设备**作为中转。开始共享后，普通系统输出临时切到中转，原音源和其他设备都经程序播放；停止后恢复原输出。播放器应使用系统默认输出。

程序不安装虚拟声卡；虚拟设备自身的转发行为由其驱动决定。异常退出恢复及并发默认设备更改均有专门处理。

### 安卓手机

安装配套 APK，手机通过数据线连接并开启「USB 网络共享」。电脑点击「连接手机」，在接收端填写电脑地址和六位连接码。配对后按普通共享设备使用。

无需 Root、USB 调试或录音权限。连接失败时检查 USB 网卡地址及防火墙的 UDP 45891。音频使用未额外加密的 UDP，应在 USB 直连或可信局域网使用。详见 [安卓说明](android/README.md)。

## 延迟与兼容性

面向 Windows 10 / 11 x64，安卓最低 Android 8.0。跨声卡、蓝牙和手机无法保证零延迟、零卡顿或采样级同步。目标延迟为软件估算，不包含未报告的无线、硬件和声学延迟。

音频处理使用 WASAPI、统一时间线、时钟漂移校正和 sinc 重采样。音量调整及采样率转换不保证位完全一致。出现断续时可切换「均衡」或「高稳定」；无声时检查音源、系统静音和独占播放。

验证范围及已知限制见 [验证记录](VERIFICATION.md)，界面可访问性限制见 [界面审阅](UI-REVIEW.md)。

## 构建

需要 Windows x64、PowerShell 7+、.NET SDK 8.0.4xx：

```powershell
./Build.ps1 -Portable -SkipTests
```

生成的完整运行文件夹为 `artifacts/AudioLink`。省略 `-SkipTests` 会执行核心与网络测试。安卓独立构建方法见 [安卓文档](android/README.md)。

源码结构：`src/AudioLink.App` 为 WPF / WASAPI 桌面端，`src/AudioLink.Core` 为音频处理，`src/AudioLink.Network` 为手机协议，`android/app` 为接收端。

发布维护方法见 [版本发布](docs/RELEASING.md)。

## 开源许可

采用 [MIT License](LICENSE)。Windows 音频访问依赖 NAudio 2.2.1；便携包包含 .NET 运行环境，相关声明见 [第三方许可](THIRD-PARTY-NOTICES.md)。
