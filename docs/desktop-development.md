# MiPlay Desktop 开发与测试

## 0.6.2 音频调度与连接诊断（2026-11-24）

- Mac 的 `mach_wait_until` 由会话自有的后台定时线程执行，调用方异步等待，不再占用采集、编码、回连及其他音箱使用的线程池工作线程。取消最多等待一个 20 ms 原生睡眠片段，停止后回收线程。
- 共享采集层按实际帧到达搬运，不再引入第二套定时器或提前填静音；有界队列可保留约 128 ms 的短暂积压并立即排出，不固定增加播放延迟。编码层继续统一供给节拍及恢复静音。
- 原生采集期间登记延迟敏感的系统活动，停止/失败后释放；允许系统空闲睡眠，不修改电源设置。
- 保留已验证的共同 AAC/PTS 和逐帧同步协议。单台音频 TCP 写入阻塞限时由 3 s 改为 500 ms，失败先移除该成员的广播/发送等待，再进行传输清理；现有三次重连及用户停止取消规则继续生效。
- 连接诊断包含版本握手、反向 RTSP/辅助/音频连接及播放准备阶段和累计耗时；播放诊断分别记录等 AAC、写 socket、同步等待的区间最大值，以及共享源 PCM 溢出/欠载。原有每音箱 PCM 队列仅用于信号诊断，其溢出不能直接解释为编码音频丢帧。
- 本轮不改变音质、音量、立体声组合或已保存延迟。不将发送间隔、内部缓冲量或模拟测试当作实测声音延迟；连接提速和实际断续改善仍需真机复核。

## 0.6 交互与架构

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
open 'artifacts/desktop/0.6.2/osx-arm64/MiPlay Desktop.app'
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


## 0.6.0 从 Windows 主分支移植的桌面能力（2026-11-18）

- 音源：设置中选择系统输出或单应用，刷新列表后应用。系统模式支持跟随默认或固定输出设备；单应用模式选当前音频进程及可选辅助进程，不支持把任意 PID 当作持久身份。应用音源不跨应用重启恢复，设备固定 UID 可保存；切换音源停止全部会话，用户重新选择接收端。找不到所选设备/应用时报错，不回退捕获全部系统声音。
- Mac 使用 inclusive Core Audio Process Tap，只包含选定应用及辅助进程，动态更新辅助进程集合；暂停和短暂无声不等于应用退出。Windows 复用 main 的进程 loopback 和输出设备枚举。两端通过同一采集订阅转发音频；Mac 源静音适用于系统和单应用，Windows 仍禁用静音选项。
- 进程音源的媒体信息只允许来自对应 bundle 的系统媒体数据；捕获 QQ 时不会附上 Apple Music 歌词/封面。系统仅能提供另一播放器的媒体信息时，显示源范围并发送空媒体，不按名称猜测。
- 独立接收设备可选 Stereo / LeftAsMono / RightAsMono。配置改变停止全部，重新准备共享路由编码器后由用户重新选择。默认 Stereo 仍共用一个编码器；仅存在手动路由配置时准备三种路由。预备路由数与首次就绪设备数分开，不等待三个接收端才播放。官方立体声组合继续合并控制，不允许覆盖声道。
- DLNA 发现默认关闭，在设置开启后使用 SSDP / UPnP。设备 ID 使用 dlna: 前缀，与 MiPlay 独立保存。音频优先 PCM/WAV，格式不被接受或拉流失败时尝试 MP3 320 kbps；新增固定版本 LAME 静态编码器随包提供。关闭 DLNA 停止其会话，不停止 MiPlay。DLNA 不使用 MiPlay 同步或实时延迟，不自动混同两种协议下的同一设备。DLNA 本轮不接入 MiPlay 媒体歌词封面转发。
- Mac 主窗口与 Windows 设备卡增加折叠实时诊断，展示缓冲、发送、编码积压和队列异常。数值不代表声学端到端延迟。原诊断导出保留。
- 声学延迟测试按用户要求暂缓，本版本不申请麦克风权限或发送测试音。只有用户选择音箱后才开始采集/投放。

主窗口更多菜单仅保留投放设置入口、重新扫描、停止全部、权限入口和导出诊断。音质、同步模式、媒体信息和歌词开关统一在设置窗口，避免重复入口。


## 0.6.1 Mac 外观与设置侧栏（2026-11-18）

- 设置分为外观、音源、音频与连接、声道分配、封面、媒体与歌词、诊断；打开默认外观页。音源保留草稿和应用动作，切换分类不会发出投放命令。
- 外观可跟随系统、日间或夜间，应用到主窗口、菜单栏浮窗、设置及菜单，独立保存于本应用偏好。
- 背景可选专辑氛围或原生毛玻璃。专辑氛围在后台生成小尺寸模糊图，只在图片变化时更新，无图日间纯白/夜间深色；原生毛玻璃使用 NSVisualEffectView。减少透明度时使用纯色，减少动态效果时禁用过渡。
- 外观与封面连续性改动针对Mac；Windows继续交付同版本的音源/路由/DLNA测试包，未新增外观设置。

封面与元数据在同一录音的短空窗中保留已验证字段，明确清空和退出立即清除；新歌过渡只作用于显示，不把上一首封面发送给下一首。


## 0.6.2 Mac 采集性能与多声道准备（2026-11-24）

Mac 当前使用 Core Audio Process Tap，不创建视频录屏流；系统的“屏幕与系统音频录制”权限名称不代表应用在采集视频。采集性能、格式转换、编码、网络发送和界面负载需要分开观测，不能仅凭权限入口判断卡顿原因。

本轮继续使用现有 Objective-C++／Swift 采集层，不整包替换为另一个 Tap 包装库。四个已核对的项目提供不同层次的参考，以下判断来自其公开文档和源码，不是本机 CPU、听感或掉音率排名：

| 项目与定位 | 借鉴内容 | 采用边界 |
| --- | --- | --- |
| [CPAL](https://github.com/RustAudio/cpal)：跨平台音频输入输出库 | 设备与格式枚举、稳定身份、时间戳、缓冲溢出／不足报告、设备变化与资源生命周期；[Mac 回环实现](https://github.com/RustAudio/cpal/blob/master/src/host/coreaudio/macos/loopback.rs)同样使用 Process Tap 和私有聚合设备，[修复记录](https://github.com/RustAudio/cpal/blob/master/CHANGELOG.md)可作回归场景清单 | 当前 Mac 回环要求 14.6+，按设备 stream 0 建立采集，真实声道布局仍需设备验证。只借鉴设计，不为现有原生层引入 Rust；不能把换库视为性能提升的证据 |
| [AudioCap](https://github.com/insidegui/AudioCap)：单应用录音与 Tap API 示例 | PID 到音频进程的转换、读取 Tap 实际格式、创建与销毁顺序；适合作为最小采集对照。[实现](https://github.com/insidegui/AudioCap/blob/main/AudioCap/ProcessTap/ProcessTap.swift)较易核对 | 示例使用双声道混音，并在录音回调流程中包装缓冲、写文件，不能照搬为网络发送结构。权限检查含可关闭的私有 TCC API，本轮只参考公开采集逻辑 |
| [SystemAudioTap](https://github.com/heykadirhan/SystemAudioTap)：采集、缓冲、处理后在本机回放的模块 | 实时回调避免分配、锁和日志；后台读取健康计数；处理采样率变化、队列耗尽和采集失效后释放静音 | 当前为 Float32 双声道，使用独立采集／回放时钟；其自适应队列、拼接和淡出针对本机回放，不直接移植为 MiPlay 多音箱同步算法。项目较小，参考设计仍需自行验证 |
| [CoreAudioTapKit](https://github.com/CJStanfield/CoreAudioTapKit)：采集和本机输出共用一个 IOProc／主时钟的模块 | [预分配工作缓冲](https://github.com/CJStanfield/CoreAudioTapKit/blob/main/Sources/CoreAudioTapKit/SystemAudioTapEngine.swift)、[等待聚合设备就绪、定位 Tap 输入、关闭顺序](https://github.com/CJStanfield/CoreAudioTapKit/blob/main/Sources/CoreAudioTapKit/SystemTapCapture.swift)；可能阻塞的启动／停止放在后台 | 当前归一为双声道，并与本机回放耦合。输入定位需按我们的聚合设备拓扑判断；其单时钟设计不消除网络音箱的独立时钟，不能承诺提升远端同步精度 |

本轮改动范围与后续边界：

- 保留实时回调到有界队列的结构，优先复用格式转换缓冲；已经交错排列的输入按块复制，避免逐帧小块复制。格式变化或重新绑定设备后重建匹配格式的缓冲，避免跨格式复用。是否降低实际负载和掉音仍需测量。
- 诊断区分 Tap 的原始输入格式和交付给现有编码器的格式，保留真实采样率、样本类型和声道数的可见性。不能用“交付为双声道”推断输入本身只有两个声道，也不能把源格式诊断视为多声道发送已经实现。
- 当前 MiPlay 发送继续使用 48 kHz、S16、双声道 PCM 输入和既有 AAC-LC 路径；本轮不切换编码器或发送格式，不承诺多声道接收兼容，也不因减少复制而声称可听音质已提高。
- 后续多声道以实际设备流和声道布局为依据，采集、转换与编码边界分离。可增加普通 Core Audio 输入设备入口，用 [BlackHole](https://github.com/ExistentialAudio/BlackHole) 验证真正的 5.1／7.1 PCM；播放器必须实际输出多声道，发送协议和接收端也需分别验证。BlackHole 安装、音频路由和 [libASPL](https://github.com/gavv/libASPL) 自研虚拟设备均不属于本轮改动。
- Dolby／DTS 原始码流及 Atmos 对象元数据暂缓；系统解码后的多声道 PCM 不等同于保留原码。声学延迟测试继续暂缓，本轮不启动录制、控制播放器、连接手机或修改系统音频设置。

本节记录研究依据和实施范围，不构成自动测试、实际性能或音箱听音验收记录；验证结果另按实际执行情况登记。

## 0.6.3 原始 PCM 与发送适配分离（2026-11-24）

采集格式不以接收端能力为前提。系统音源按 Tap 实际提供的采样率、精度和声道顺序进入有界队列，保留 S16／packed S24／S32／Float32、8–192 kHz、最多32声道。平面输入只重新交错，不先混成双声道。队列按实际格式在启动时分配，音频回调不创建缓冲、转换或发送网络数据。

`IRawAudioSourceCatalog.CreateRawCapture` 和 `IRawAudioCaptureSource.StartRawAsync` 向同步 `IRawPcmSink` 提供 `RawPcmBlockView`：包含完整源 PCM、格式、声道标签、单调时钟结束时间及断点。视图借用生产者缓冲，只在 `Write` 调用内有效；异步消费者需自行持有拷贝。本版没有原始音频落盘或无限历史缓存。

旧发送接口通过独立 `MacPcmSendAdapter` 转为48 kHz／S16／双声道，再进入原共享编码、同步及MiPlay网络链路；后续多声道消费者可以直接接原始入口，无需放宽旧发送接口的全局常量。源格式和交付格式分别记入诊断。

转换策略明确记录在 `DownmixPolicy`：

- `MonoDuplicate`：单声道复制至左右；`Stereo`：常规左右，48 kHz S16可逐字节旁路。
- `SystemLayout`：按真实Core Audio声道标签转换；标准系统下混不包含独立LFE。设备布局只在完整匹配单一输出流时采用，不按6／8声道数猜测位置。
- `DiscreteAverage`：布局未知时按奇偶声道索引分至左右、各侧取均值，确保每个声道有贡献并保留余量。这是兼容混音，不声称重建5.1／7.1空间位置或Dolby对象。

采集专用线程等待原生事件，每次等待最多20 ms；停止先取消并等线程、转换器退出，再释放Tap和队列。格式变化通过Tap属性监听通知，检测后停止并提示重新开始；后台每秒检查作为补充，不宣称无缝热切换。断点会重置重采样历史并清理未完成的发送帧，避免跨断点拼接。事件握手使用顺序一致原子并清理旧信号，避免暂停后忙转。

当前保留单应用模式原有的跨设备立体声混音行为；系统已经交付双声道时，原始入口也只有双声道。采集器不会恢复播放器或系统先前已经混掉的声道。普通输入设备／BlackHole入口、全链路原生多声道发送、原生AAC对照和Dolby／DTS原码仍为后续工作；不以这些未完成项阻止本版原始采集。

离线验证入口：

```sh
bash native/macos/build.sh artifacts/native --test
MIPLAY_TEST_NATIVE_AUDIO="$PWD/artifacts/native/libMiPlayAudio.dylib" dotnet test tests/MiPlay.Mac.Tests/MiPlay.Mac.Tests.csproj -c Release
```

这两项仅使用合成数据和fake采集后端，不开始系统声音采集；真实音箱听音、性能和长期稳定性需另验收。

## 0.6.4 接收端音频控制端口（2026-11-24）

发现、身份查询和实际连接使用同一个明确的 `RendererDevice.MiPlayControlPort`。只对已核实的app-5紧凑描述符（payload恰4字节、前缀`04 83`）解析末两字节的大端非零端口；其他布局继续使用已验证的8899回退。`_mi-connect`的DNS SRV端口属于发现服务，UPnP HTTP端口也不参与音频端口选择；旧`MiPlayLegacyAppData`前两个字节的解释不用于此决策。

本机此次FusionPlay-Windows公告`appsData=gQAEBINJ0w==`，表示音频控制18899。只读TCP检查中，18899约4 ms建立连接，而8899超时，吻合此前`ControlConnect`五秒超时。身份缓存按IP和音频控制端口变化刷新。该修复解决已证实的端口选择错误，不等同于后续认证、音频回连或实际播放验收；小米电脑管家的SafetyAuth兼容性仍单独处理。

## 本机安装与后续更新

本机使用固定位置 `/Applications/MiPlay Desktop.app`。开发包构建后执行下列安装脚本，会验证签名、正常退出旧版并替换应用，再从“应用程序”启动；配置和权限数据目录继续沿用。安装失败时回滚已替换的旧应用。脚本默认读取打包脚本中的当前版本，也可通过 `--source` 指定 `.app`。

```sh
python3 scripts/package-desktop.py macos
python3 scripts/install-macos-local.py
```

此入口用于本机开发测试更新，不是联网自动更新服务。安装和后续开发更新已由用户授权，固定安装位置保持一致。
