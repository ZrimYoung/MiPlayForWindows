# MiPlay Desktop 开发与测试

## 0.4 交互与架构

- Mac 窗口使用 SwiftUI，窗口生命周期、菜单栏和睡眠通知使用 AppKit。代码位于 `native/macos/App`，由系统 Swift 编译器构建；不使用 Avalonia 渲染 Mac 交付界面。
- `MiPlay.Engine` 是随应用打包的本地 .NET 协议进程，继续复用经过回归验证的 MiPlay 发送实现。Swift 通过匿名管道收发 JSON 行，没有本地 HTTP 服务、开放监听端口或云端调用。主窗口退出或崩溃导致输入管道关闭时，服务停止全部投放并释放采集。
- `MiPlay.Desktop` 保留为 Windows Avalonia 界面，旧 WinUI 程序继续保留作回归对照。
- `MiPlay.Core/Desktop/DesktopController` 统一管理设备卡片、独立会话、持续发现与设置。`SharedDesktopAudioPlatform` 使多台音箱共用一个原生采集，保留采集时间戳，每台发送端有独立的有界缓冲和编码器；一个接收端不会阻塞其他接收端。
- `MiPlay.Platform.Windows` 使用 WASAPI；`MiPlay.Platform.Mac` 与 `native/macos/MiPlayAudio.mm` 使用 Core Audio Process Tap、后台重采样和原生计时。

Mac 界面与系统采集采用原生语言；MiPlay 协议核心目前仍为 C#，不是全部改写成 Swift。

## 交互约定

参考用户提供的小米官方面板：点设备卡片加入投放，再点停止这一台；多张卡片可同时选中。选中后展开独立音量，延迟收进展开项；顶部显示系统声音和“仅音箱播放（实验）”，提供全部停止。

- 扫描：启动立即发现，每次查询约 3 秒，间隔 2 秒继续；18 秒未再发现标为离线，保留卡片及设置，不随每轮扫描清空或重排。已保存设备在下次启动先显示离线，重新发现后更新地址。
- 音量：按设备稳定标识保存 0–100 的用户设置；没有保存值时读取音箱当前音量，不主动设置默认音量。每次重新连接都应用保存值。
- 延迟：每台 0–2000 ms，提供 200 / 500 / 1000 ms 快捷值，默认沿用 1000 ms。滑杆先调整，点击应用；快捷值直接应用。仅重连这一台，其他设备继续。数值对应 MiPlay 播放预留时间，不是实测端到端延迟。低值是否稳定取决于网络和接收端，不能承诺 0 ms 总延迟。
- 多台可独立加入和退出；当前不承诺样本级同步。可手动微调各台延迟，后续需双音箱实际听音验证。
- Mac 静音选项：`CATapMutedWhenTapped`，只在实际采集期间静音被捕获的本机输出，最后一个采集订阅释放时恢复；不修改系统音量值。设置作用于所有投放音箱，默认关闭。Windows 暂不支持，界面明确禁用。
- 默认输出变化重新绑定唯一采集；每台网络失败最多重连三次，始终按稳定身份查找原音箱。用户停止立即取消连接或重连。
- 重启应用、发现已保存音箱和电脑唤醒均不自动投放。关闭窗口后继续运行，通过托盘／菜单栏停止或退出。
- 配置仍使用独立 `MiPlayDesktop` 目录。诊断不含音频、认证密钥或原始协议帧。

## 构建和测试

需要 .NET 10 SDK、Python 3.9+、curl；Mac 原生界面和采集库需要 Xcode 命令行工具，运行目标 macOS 14.2+ Apple Silicon；Windows 目标 Windows 11 x64。

```sh
dotnet test tests/MiPlay.Tests/MiPlay.Tests.csproj -c Release
dotnet test tests/MiPlay.Desktop.Tests/MiPlay.Desktop.Tests.csproj -c Release
bash native/macos/build.sh artifacts/native --test
python3 scripts/package-desktop.py macos
# 原生 Mac 应用
open 'artifacts/desktop/0.4.0/osx-arm64/MiPlay Desktop.app'
# Windows 构建（Mac 上只能交叉编译，运行测试需要 Windows）
python3 scripts/package-desktop.py windows
```

真实编码测试需将 `scripts/prepare-desktop-ffmpeg.py macos` 输出的可执行路径设为 `MIPLAY_TEST_FFMPEG`。Windows 对应 `windows`。Windows 编码为 `aac_mf`，Mac 为 `aac`。

`bash native/macos/build-app.sh artifacts/native-preview` 可独立编译原生界面。运行生成的 `MiPlay.Desktop --snapshot /absolute/path.png` 使用固定预览数据，生成界面图后退出，不启动发送服务、不扫描、不采集音频。

打包脚本包含运行时、固定版本 FFmpeg、原生依赖与测试说明；Mac 使用独立 Swift 主程序和单文件 .NET 发送服务，仅进行本地临时签名。CI 上传测试产物，不发布 Release。公开分发仍需确认上游复用授权、第三方分发材料与平台签名。

## 官方 API 依据

- [Core Audio taps](https://developer.apple.com/documentation/coreaudio/capturing-system-audio-with-core-audio-taps)：系统音频采集、静音行为及 `NSAudioCaptureUsageDescription`。
- [SwiftUI](https://developer.apple.com/documentation/swiftui)：Mac 原生控件和视图；AppKit 负责窗口与菜单栏生命周期。

## macOS 27 视觉与媒体信息

界面跟随系统深浅色，使用系统背景模糊、柔和径向渐变和轻量半透明设备卡片。原生 `glassEffect` 仅用于顶部小型控制，macOS 26+ 使用系统液态玻璃，旧系统回退为普通系统材质。尊重“减少透明度”和“减少动态效果”。参考 [Apple Liquid Glass 指引](https://developer.apple.com/documentation/swiftui/applying-liquid-glass-to-custom-views)。

Mac 的“更多”菜单可关闭“显示系统媒体信息（实验）”。实现先只读查询 MediaRemote，直接接口无信息时通过系统 `osascript` 查询同一媒体服务；兼容路线参考 [mac-now-playing 的接口说明](https://github.com/kernoeb/mac-now-playing)。这依赖私有系统接口，不能保证所有系统版本、播放器或应用商店分发可用。没有关闭 SIP、向系统服务注入代码或修改系统安全配置。

只显示歌曲、歌手、来源和系统提供的封面，不发送播放控制，不联网搜索封面，不记录歌曲或封面。每轮有超时；关闭信息显示、退出和发送服务异常结束都会取消读取。无信息时显示“系统音频”。本机探测返回 `empty_or_restricted`，可能没有活动媒体项或受到系统限制；尚未确认 QQ 音乐等播放器的实际信息和封面显示。

界面布局预览可通过 `--light` / `--dark` / `--expanded` 调整。`--snapshot` 是原生视图渲染，会遗漏由窗口合成器完成的玻璃效果；`--screen-snapshot` 需可用的窗口截图权限，本轮返回 `could not create image from window`，未取得最终合成截图。实际玻璃效果需打开应用确认。
