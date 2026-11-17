# MiPlay Desktop 开发与测试

## 0.5 交互与架构

- Mac 窗口使用 SwiftUI，窗口生命周期、菜单栏和睡眠通知使用 AppKit。代码位于 `native/macos/App`，由系统 Swift 编译器构建；不使用 Avalonia 渲染 Mac 交付界面。
- `MiPlay.Engine` 是随应用打包的本地 .NET 协议进程，继续复用经过回归验证的 MiPlay 发送实现。Swift 通过匿名管道收发 JSON 行，没有本地 HTTP 服务、开放监听端口或云端调用。主窗口退出或崩溃导致输入管道关闭时，服务停止全部投放并释放采集。
- `MiPlay.Desktop` 保留为 Windows Avalonia 界面，旧 WinUI 程序继续保留作回归对照。
- `MiPlay.Core/Desktop/DesktopController` 统一管理设备卡片、持续发现与设置。默认通过 `DesktopCastGroup` 管理一组采集、共享 AAC 编码器与媒体时间线；各音箱保留自己的连接和音量。组内采用有界发送协调，慢连接达到超时后退出；仍需真机验证异常网络下对其他成员的影响。关闭“同步投放”后使用此前独立编码的兼容路径。
- `MiPlay.Platform.Windows` 使用 WASAPI；`MiPlay.Platform.Mac` 与 `native/macos/MiPlayAudio.mm` 使用 Core Audio Process Tap、后台重采样和原生计时。

Mac 界面与系统采集采用原生语言；MiPlay 协议核心目前仍为 C#，不是全部改写成 Swift。

## 交互约定

参考用户提供的小米官方面板：点设备卡片加入投放，再点停止这一台；多张卡片可同时选中。选中后展开独立音量，延迟收进展开项；顶部显示系统声音和“仅音箱播放（实验）”，提供全部停止。

0.5.2 的 Mac 菜单栏左键改为原生浮窗：媒体信息、音箱选择、已选项目音量；右键打开主窗口／停止全部／退出菜单。延迟及本机静音选项仅在主窗口。已识别立体声组合默认合并为一个项目、一个音量滑块，不显示左右独立音量；左右延迟在主窗口展开微调。组内使用同一绝对音量并记住，旧单台音量入口也联动整组。旧设置左右音量不同，则从停止状态启动整组时统一为较低保存值。

- 扫描：启动立即发现，每次查询约 3 秒，间隔 2 秒继续；18 秒未再发现标为离线，保留卡片及设置，不随每轮扫描清空或重排。已保存设备在下次启动先显示离线，重新发现后更新地址。
- 音量：按设备稳定标识保存 0–100 的用户设置；没有保存值时读取音箱当前音量，不主动设置默认音量。每次重新连接都应用保存值。
- 延迟：每台 0–2000 ms，提供 200 / 500 / 1000 ms 快捷值，默认沿用 1000 ms。滑杆先调整，点击应用；快捷值直接应用。仅重连这一台，其他设备继续。数值对应 MiPlay 播放预留时间，不是实测端到端延迟。低值是否稳定取决于网络和接收端，不能承诺 0 ms 总延迟。
- 多台可独立加入和退出；当前不承诺样本级同步。可手动微调各台延迟，后续需双音箱实际听音验证。
- “同步投放”默认开启并保存。Mac 位于更多菜单，Windows 位于主面板；切换时先停止全部投放，需重新点选音箱。多台同步时先设相同延迟，再按实际声音微调；每台延迟转换成共同时间基准上的独立偏移，不重置其他成员的媒体时间线。
- Mac 静音选项：`CATapMutedWhenTapped`，只在实际采集期间静音被捕获的本机输出，最后一个采集订阅释放时恢复；不修改系统音量值。设置作用于所有投放音箱，默认关闭。Windows 暂不支持，界面明确禁用。
- 默认输出变化重新绑定唯一采集；每台网络失败最多重连三次，始终按稳定身份查找原音箱。用户停止立即取消连接或重连。
- 重启应用、发现已保存音箱和电脑唤醒均不自动投放。关闭窗口后继续运行，通过托盘／菜单栏停止或退出。
- 配置仍使用独立 `MiPlayDesktop` 目录。诊断不含音频、认证密钥或原始协议帧。

## 构建和测试

0.5.3：默认同步模式通过已有控制连接每 2 秒读取音量、每 10 秒读取组合身份，按回复序号匹配；不增加投放期间的第二条控制连接，不把查询无回复视为投放失败。实体键音量更新显示和保存值，不自动回写音箱；成员音量不同仍显示“—”，不伪装成整组一致。兼容模式暂不主动读取实体键音量。

一侧失败后保留另一侧；缺失成员重新发现后显示补连按钮，由用户操作，只重连缺失成员。组合缺失字段不等于解散；确认 groupId／声道变化才停止旧组合并提示重选，不影响其他独立音箱。空闲设备随后刷新身份缓存。重启、普通重新发现和组合变化都不会自动开始新的投放。

Mac 媒体支持拖动进度，松手发送一次；时长／位置未知时禁用，拖动期间切歌则取消。媒体操作先核对来源播放器，再等待实际播放状态、曲目或位置变化；无响应提示确认，保留真实观察值。播放器退出立即清除信息，迟到事件不恢复旧封面；读取失败时降级并每 15 秒尝试恢复封面桥接。手机端不需要修改设置。

需要 .NET 10 SDK、Python 3.9+、curl；Mac 原生界面和采集库需要 Xcode 命令行工具，运行目标 macOS 14.2+ Apple Silicon；Windows 目标 Windows 11 x64。

```sh
dotnet test tests/MiPlay.Tests/MiPlay.Tests.csproj -c Release
dotnet test tests/MiPlay.Desktop.Tests/MiPlay.Desktop.Tests.csproj -c Release
bash native/macos/build.sh artifacts/native --test
python3 scripts/package-desktop.py macos
# 原生 Mac 应用
open 'artifacts/desktop/0.5.11/osx-arm64/MiPlay Desktop.app'
# Windows 构建（Mac 上只能交叉编译，运行测试需要 Windows）
python3 scripts/package-desktop.py windows
```

真实编码测试需将 `scripts/prepare-desktop-ffmpeg.py macos` 输出的可执行路径设为 `MIPLAY_TEST_FFMPEG`。Windows 对应 `windows`。Windows 编码为 `aac_mf`，Mac 为 `aac`。

0.5 共享路径从单台开始，后加入者在握手结束后才取得新鲜的历史音频窗口。连接预热期间持续排空旧编码输出；正式发布后保留完整帧序列。输入在途上限为 12 帧，初始发送仍沿用已有 14 帧（约 299 ms）预填充与启动节奏；它并非零缓冲方案。新增测试覆盖预热门控、慢加入不阻塞采集、退出首台、不同延迟下的共同时间基准、资源释放及真实 AAC 编码。

诊断导出增加输入帧年龄、编码在途数量、发送完成偏差和预热丢弃数量。这些是软件链路指标，不是声学延迟或声学同步精度。设计依据与限制见 [官方组件研究](hyperos-latency-sync-research-2026-11-15.md)。

`bash native/macos/build-app.sh artifacts/native-preview` 可独立编译原生界面。运行生成的 `MiPlay.Desktop --snapshot /absolute/path.png` 使用固定预览数据，生成界面图后退出，不启动发送服务、不扫描、不采集音频。

打包脚本包含运行时、固定版本 FFmpeg、原生依赖与测试说明；Mac 使用独立 Swift 主程序和单文件 .NET 发送服务，仅进行本地临时签名。CI 上传测试产物，不发布 Release。公开分发仍需确认上游复用授权、第三方分发材料与平台签名。

## 官方 API 依据

- [Core Audio taps](https://developer.apple.com/documentation/coreaudio/capturing-system-audio-with-core-audio-taps)：系统音频采集、静音行为及 `NSAudioCaptureUsageDescription`。
- [SwiftUI](https://developer.apple.com/documentation/swiftui)：Mac 原生控件和视图；AppKit 负责窗口与菜单栏生命周期。

## macOS 27 视觉与媒体信息

界面跟随系统深浅色，使用系统背景模糊、柔和径向渐变和轻量半透明设备卡片。原生 `glassEffect` 仅用于顶部小型控制，macOS 26+ 使用系统液态玻璃，旧系统回退为普通系统材质。尊重“减少透明度”和“减少动态效果”。参考 [Apple Liquid Glass 指引](https://developer.apple.com/documentation/swiftui/applying-liquid-glass-to-custom-views)。

Mac 的“更多”菜单可关闭“显示系统媒体信息（实验）”。0.4.1 改为只读观察 MediaRemote 的持续更新，能够接收稍晚到达的封面；此前的 `osascript` / `MRNowPlayingRequest` 路线无法取得封面，现在只保留作文字降级。桥接基于 [mediaremote-adapter](https://github.com/ungive/mediaremote-adapter)，固定源码版本与 BSD-3-Clause 许可见 `native/macos/MediaRemote/README.md`；上游只构建观察部分，不包含其播放控制或测试播放器。0.5.2 另加限定上一首、播放／暂停、下一首的 MiPlay 控制入口，仅在用户点击时调用，观察进程仍只读。系统 Perl 加载独立的 Objective-C 库，结果经管道传给 SwiftUI，关闭选项／退出时结束自己的子进程。封面离开主线程解码为最长边 512 px 图片，输入图片最多 8 MiB、单条消息最多 12 MiB，不联网搜索封面或保存媒体内容。这依赖私有系统接口，不能保证所有系统版本、播放器或应用商店分发可用。没有关闭 SIP、向系统服务注入代码或修改系统安全配置。

显示系统提供的歌曲、歌手、专辑、封面及播放进度，不联网搜索封面、不记录歌曲或封面。播放中根据系统时间戳外推进度，暂停后不增长。控制发送成功仅表示系统接受，不假设播放器状态已改变；状态来自观察结果。每轮有超时；关闭信息显示、退出和发送服务异常结束都会取消读取。无信息时显示“系统音频”。QQ 音乐媒体读取的实测和各版本边界见 `desktop-validation.md`。

原生浮窗预览：`MiPlay.Desktop --snapshot /absolute/path.png --menu-preview --stereo-fixture --media-fixture --light`（或 `--dark`）。`--popover-check --stereo-fixture --media-fixture` 使用模拟数据检查真实 NSPopover 打开、选择后调整高度、关闭和重新打开，不连接音箱，不读取或控制播放器。`bash tests/test-mac-media.sh` 检查信息解析及控制接口的只读可用性，不发送播放指令。

界面布局预览可通过 `--light` / `--dark` / `--expanded` 调整。`--snapshot` 是原生视图渲染，会遗漏由窗口合成器完成的玻璃效果；`--screen-snapshot` 需可用的窗口截图权限，本轮返回 `could not create image from window`，未取得最终合成截图。实际玻璃效果需打开应用确认。

## 0.5.4 音质与接收端媒体信息

Mac 主窗口「更多 → 投放音质」提供 256、320 和实验性 512 kbps AAC-LC；Windows 主面板提供对应下拉框。默认 256 kbps，选择作为全局设置保存，切换时停止投放，重新选择音箱后使用新编码器。512 kbps 仍是有损编码，接收端兼容性需要单独验证。本轮没有修改系统默认采样率或采样率转换质量，便于单独对比编码档位。

Mac 系统媒体经后台服务发送：SetMediaInfo 携带歌名、歌手、专辑、时长和 mArt JPEG Base64 封面（最长边 256 px、最多 48 KiB）；内容变化时才重发封面，进度每秒通过 0x56 更新，媒体播放状态通过 0x5e 更新。进度来自播放器，没有按音箱实际声学延迟校准。

无媒体或关闭读取时清空旧曲目并回退 System Audio；不停止系统声音采集。媒体数据不写配置或诊断，不启动封面 HTTP 服务。共享和兼容播放模式均使用持续控制读取处理异步应答，不把媒体无回复当作投放失败。Windows 本轮提供音质设置，系统媒体来源仍由 Mac 原生界面提供。

## 0.5.5 原生设置菜单与实时延迟

Mac 主窗口更多设置使用独立 AppKit 菜单，媒体和设备刷新不会重建展开中的菜单；三档音质平铺显示。音质切换仍会停止投放，需要重新选择接收端。

延迟拖动每 120 ms 合并发送最新值，作用于当前 RTSP TIME_OFFSET，不重建采集、编码或网络连接；恢复／重同步仍叠加当前用户偏移。立体声组合默认一个延迟控件，两成员一起调整。降低缓冲可能短暂跳音或卡顿；该值仍不是声学端到端测量。菜单栏浮窗不显示延迟。

接收端在版本／初始化握手阶段主动断开时单独提示，避免将其直接描述成普通网络离线或已经证明的认证不支持。

## 0.5.6 歌词实验

Mac 更多设置可开启「读取 Apple Music 歌词（实验）」，需要用户授予音乐应用的自动化访问；仅执行读取。当前在线歌曲实测返回空歌词，不能用该选项承诺 Apple Music 在线逐句歌词。提供文本时发送文本，有时间戳时原样保留。QQ 自动歌词尚未接入。

复核工具 `python3 tests/probes/lyrics-readonly.py music` 仅打印存在性与时间标记数；`python3 tests/probes/lyrics-readonly.py qq <songmid>` 使用官方域名、无登录信息，验证指定歌曲 LRC，不输出或保存正文。

后续来源要求已确定为播放器直接读取，不采用在线搜索匹配。当前新增 Apple Music 本地 TTML 缓存只读实验，真实缓存转换、当前歌曲 store ID 自动关联及异步读取已验证，接收端歌词显示与滚动仍待实测；QQ 歌词页正文已通过辅助功能只读验证，但尚未取得可靠时间轴，未接入生产自动发送。详见 [直接读取实验](direct-player-lyrics-2026-11-17.md)。

## Mac 封面设置与 WebP（0.5.10）

通过「更多设置 → 投放设置…」或 ⌘, 打开原生设置窗口。图片设置自动保存并即时补送当前封面；音质及同步模式更改仍会停止投放。当前输出一栏显示实际尺寸、格式、大小和是否原图直传。

`prepare-desktop-webp.py` 从 `packaging/desktop-dependencies.json` 的固定官方版本和 SHA-256 构建自包含动态库；libtool 构建使用无空格临时目录，避免工作区路径兼容问题。只依赖系统 libSystem，不依赖 Homebrew。上游 BSD 许可和 PATENTS 随包提供。

诊断 `--media-check --artwork-format webp` / `png` 使用进程内临时选项，`--media-check --artwork-refresh-check` 核对当前曲目从 JPEG 切到 WebP；均不修改真实设置、控制播放器或发送到接收端。测试使用合成图片，不保存当前曲目的原图或歌词正文。

依据：[Android 图片支持](https://developer.android.com/media/platform/supported-formats#image-formats)、[Apple 图片编码能力查询](https://developer.apple.com/documentation/imageio/cgimagedestinationcopytypeidentifiers())、[官方 libwebp 1.6.0](https://chromium.googlesource.com/webm/libwebp/+/refs/tags/v1.6.0)。平台支持不等同于具体妙播接收端支持。


## 主动获取 Apple Music 高清封面（0.5.11）

设置 → 封面 → 主动获取 Apple Music 高清封面，默认关闭，与歌词功能独立。开启后可能需要自动化权限。仅凭当前 Music 曲目的精确 ID 获取其官方高清图，不按歌名搜索。先使用系统封面，成功后自动替换，无需重连；关闭恢复系统封面。图片格式、尺寸、大小上限仍按现有设置处理，界面显示实际输出。QQ 高清来源尚未接入。

0.5.11 另增加 512 KB / 1 MB 图片大小上限（实验），保留 256 KB 默认值。大图按实际编码结果发送；如接收端不显示或更新慢，可随时调回 256 KB。
