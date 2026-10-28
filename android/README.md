# 同频 AudioLink 安卓接收端 1.4.0

支持 Android 8.0 及以上，按 iQOO 9 / Android 13 的使用方式提供 USB 连接入口。手机无需 Root 或 USB 调试，使用支持数据传输的 USB 线与电脑连接，通过「USB 网络共享」建立本地音频通道。

## 使用

1. 电脑建议更新并运行配套 **AudioLink 1.4.0**，点击右上角「连接手机」。接收协议仍兼容电脑端 1.3.1；本机音源同步微调需要新版电脑端。
2. 手机安装 `AudioLink-Android.apk` 并打开。
3. 手机设置中搜索并开启「USB 网络共享」或「USB 共享网络」。电脑连接窗口点击「刷新」，选择 USB 网络对应的电脑地址，通常会优先列在第一项。
4. 手机填入电脑地址和六位连接码，点击「连接电脑」。连接码三分钟有效，过期可在电脑端点击「换一个」。
5. 手机会作为一个独立设备出现在电脑的「共享到」列表。启用所需的手机、耳机和音响，点击「开始共享」。

电脑列表中的手机滑块只调整发给该手机的音量，手机 App 的滑块调整本机播放音量；手机系统媒体音量仍可用音量键调节。支持多部手机与电脑本机多个设备同时输出。通知栏的「断开」可停止接收；关闭电脑端会停止发送。

若连接不上，确认填写的是电脑的 USB 网卡地址，且 Windows 防火墙允许 AudioLink 在该连接上接收 **UDP 45891**。开启 USB 网络共享后出现新的电脑地址，可刷新后重填。也可以通过同一可信局域网连接。音频不经过云端，App 不需要麦克风或录音权限。

## 传输与延迟

发送格式为 48 kHz / 双声道 / PCM 16 位，每包 5 ms。接收端使用固定容量的乱序缓冲、缺包渐变处理和 Android 标准媒体 AudioTrack，积压时丢弃旧声音以保持实时性。手机媒体播放缓冲、驱动和硬件仍会增加延迟；电脑显示的是软件估算，不能代表物理发声时间。可给较快的电脑输出增加同步微调来对齐手机。

1.3.1 会先填充 AudioTrack 再播放；网络队列暂空时等待，不提前消耗未到达的包。包含系统播放队列的接收缓冲初始约 60 ms，发生欠载后逐步增加，连续稳定后缓慢减少，通常在 40–120 ms 内自适应；驱动要求的最小值可能更大。电脑按手机反馈校正发送时钟，避免长时间播放后队列逐渐读空或积压。

1.4 不再强制低延迟路径或内置扬声器路由，让系统按普通媒体播放规则输出；若手机已连接蓝牙耳机或 USB 声卡，系统可能选择它们，想用手机喇叭时请在系统媒体输出中选择本机。连续约 300 ms 数字全零音频后暂停并清空 AudioTrack，保持连接和接收缓冲，恢复音乐后预填充再启动。手机 App 音量为零时也会进入静音待机。很小的非零音频不会被当作静音；平稳 PCM 不作降噪或音色处理，仅在启动 / 丢包 / 重同步边界淡入。

此改动针对“音量为零仍有顶部呲呲声”时播放通道持续开启的情况。暂停应用的输出不能保证消除真机功放或 USB 供电引起的噪声，手机系统关闭硬件通道也可能需要稍等片刻。验证范围见配套 `VERIFICATION.md`。

连接使用临时连接码和独立会话标识，只发送到已配对手机，不广播音频。UDP 音频没有额外加密，应在 USB 直连或可信局域网内使用。各版本的自动化测试范围和限制见电脑发布包 `VERIFICATION.md`；本次 1.4.0 模拟器未完成启动，仅完成生产 Java 代码与电脑发送链路验证，不能替代 iQOO 9 真机的 USB 与声学验证。

## 构建

使用 PowerShell 7+、JDK 17、Android SDK Build Tools 35.0.0 和 Android API 35。无需 Gradle、Android Studio 或第三方 Android 运行库。

```powershell
./Build-Android.ps1 -JdkPath 'C:\tools\jdk-17' -BuildToolsPath 'C:\Android\sdk\build-tools\35.0.0' -AndroidJarPath 'C:\Android\sdk\platforms\android-35\android.jar'
```

默认使用当前用户缓存中的便携构建工具。签名密钥保存在 `%USERPROFILE%\.cache\codex-audiolink-android\signing`，不包含在源码包或发布包中；保留该密钥可继续签名覆盖更新。

接收缓冲回归测试直接编译生产代码，包含空队列恢复、乱序、丢包、重复包、序号回绕、流重启、自适应上限和 60 秒 USB 成批交付模拟。在项目根目录使用 PowerShell 7 执行：

```powershell
$testClasses = 'C:\tools\receiver-tests'
New-Item -ItemType Directory -Path $testClasses -Force | Out-Null
& 'C:\tools\jdk-17\bin\javac.exe' --release 8 -encoding UTF-8 -d $testClasses ./android/app/src/com/audiolink/receiver/Protocol.java ./android/app/src/com/audiolink/receiver/JitterBuffer.java ./android/app/src/com/audiolink/receiver/PcmSilenceGate.java ./android/app/src/com/audiolink/receiver/PcmContinuity.java ./android/tests/ReceiverTests.java
& 'C:\tools\jdk-17\bin\java.exe' -cp $testClasses com.audiolink.receiver.ReceiverTests
```
