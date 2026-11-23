# MiPlay Cast Android

[Android MiPlay 投送协议完整移植计划](../docs/android-miplay-full-port-plan.md)

这是使用 Kotlin、Jetpack Compose 和 Material 3 的 Android 投送端。首页用于发现音箱与控制投送；右上角齿轮打开独立设置页，可以切换 MiPlay / DLNA、选定音源和调整声道及播放预留。音源选择默认推荐媒体应用，也可按名称查找其他应用。推荐依据是应用类别和常见媒体应用包名，实际可捕获性由 Android 音频策略决定。

## 已完成

- 同时发送 MiPlay `_mi-connect._udp.local` / `_lyra-mdns._udp.local` mDNS 和 DLNA SSDP 查询；纯 MiPlay 音箱不再依赖 DLNA 描述才能出现
- 根据发现数据选择 MiPlay 控制流程。传统音箱使用 Legacy S12，FusionPlay 使用双向鉴权、加密的播放控制及 WFD v2，连接端口以接收端广播为准
- 在手机上提供反向 RTSP、辅助连接、音频连接和 40 字节 UDP 时钟同步
- 使用 Android MediaCodec 编码 48 kHz 双声道 AAC-LC，并按既有实现封装为 ADTS、MPEG-TS、RTP 和 MiPlay TCP 帧
- 保留抓包得到的首包突发、21.333 ms 稳态节奏、90 kHz 时间戳和 PAT/PMT 刷新位置
- 读取设备描述并解析 AVTransport、RenderingControl 和 ConnectionManager 地址
- 使用 Android 10 起提供的 AudioPlaybackCapture 捕获 48 kHz、16 位、立体声播放音频。Android 13 起可以在设置中改用 Shizuku 音频捕获，并在「音频来源」开启「仅音箱播放」
- Shizuku 投送到 MiPlay 时，从当前播放器的媒体会话读取歌名、艺人、专辑、时长和应用提供的封面。投送选定应用时只读取该应用的会话。整机模式在只有一个符合条件的会话时发送歌曲信息
- 可捕获全部可录制媒体，也可按应用 UID 只捕获一个应用；DRM、通话或主动禁止捕获的应用仍会保持静音
- MiPlay 组会话支持动态加入和退出；共享一条 AAC/PTS 时间线，成员各自维护发送队列和封包状态。协议上限为 63 台，真实可用数量仍需按设备和网络验收
- 发现时读取音箱名称及原生立体声组合身份。左右成员组标识一致时显示为一张卡片，整组选择和调音量。手动立体声拆分可单独选择两台设备
- 支持 0–2000 ms MiPlay 时间偏移、暂停/继续、停止、总音量和每台音箱独立音量。前台服务通知可暂停、继续和停止
- 在设置中保存协议、音源、声道路由与播放预留。每次启动由用户选择音箱并开始投送
- DLNA 模式在手机上启动 PCM/WAV HTTP 直播，并通过 UPnP `SetAVTransportURI`、`Play`、`Stop` 控制音箱
- 使用前台服务维持投送，通知栏可以暂停、继续和停止
- 适配浅色、深色主题以及手机/平板宽度

## 系统限制

默认的 Android 系统授权模式需要录音权限和本次投送的捕获确认。选择 Shizuku 音频捕获时，请先以调试模式启动 Shizuku，并在首次投送时允许 MiPlay Cast 使用它。两种方式都只捕获 Android 允许的媒体、游戏和未标明用途的音频。选定应用时按应用 UID 筛选。

「仅音箱播放」只在 Shizuku 捕获模式可用。投送时临时将手机内置扬声器的媒体音量调为零，结束时恢复。手动改变音量后以手动设置为准。打开开关时，音频采集仍使用原来的应用筛选和系统捕获规则。

Windows 的声学延迟按钮会主动向音箱播放探测声。Android 版已删除自动启动测试音，本版本不会在开始投送时插入任何测试信号；延迟只提供手动时间偏移设置。Windows 的本地扬声器静音依赖 Windows 音频策略，Android 的公开 AudioPlaybackCapture API不能替其他应用静音本机输出。

## SystemUI 投送设置

旧版 SystemUI 集成的设置入口暂时隐藏。普通投送在首页开始，使用 Android 自带的捕获授权，无须 Root 或 LSPosed。下列说明保留供后续开发使用。

- 显示音箱：关闭后不再搜索或连接新音箱，已连接的音箱仍保留音量和停止操作。
- 缓冲延迟：0–2000 ms，默认 1000 ms。这是发送端时间偏移，不是实测播放延迟。
- 声道输出：原始立体声、仅左声道、仅右声道。各音箱使用相同输出；分别播放左右声道仍在主界面的立体声拆分中配置。
- 模块与 Root：查看投送状态、作用域说明，并手动请求 Root 重启 SystemUI。重启前会二次确认。

这些参数与主界面分别保存。开关不需要重启，音频参数在下次新建投送时生效；向正在投送的会话加入第二台音箱时，沿用该会话的参数。

当前入口使用 Root Remote Submix 系统内录，不弹出 MediaProjection 授权框，也不支持按应用 UID 筛选。首次使用需要在 Root 管理器中允许本应用。首次启用 LSPosed 模块或更新 APK 后，需要重新加载“系统界面”作用域。

## 构建

在 `android` 目录运行：

```powershell
.\gradlew.bat :app:testDebugUnitTest :app:assembleDebug
```

调试 APK 输出到 `app\build\outputs\apk\debug\app-debug.apk`。

## 真机验收

1. 手机和音箱连接同一 Wi-Fi，关闭访客网络/AP 隔离。
2. 安装 APK，选择一台或多台搜索到的音箱；需要左右声道拆分时再开启“立体声拆分”。
3. 保持默认的 MiPlay，选中音箱后点击「开始投送」，按选择的捕获方式确认系统或 Shizuku 授权。需要左右声道拆分时，先在设置中启用，再按左、右顺序选择两台音箱。
4. 投送后可在首页暂停、停止和调整音量。标准 MiPlay 会话可动态加入和移除音箱，沿用当前捕获授权。左右声道拆分时需先停止再重新选组。
5. 在支持被捕获的音乐或视频应用里播放内容。
6. 确认状态依次经过控制初始化、反向 RTSP、辅助连接、音频连接和首个 AAC 单元。
7. 状态里的 `PCM nonZero` 必须持续增加且 `peak` 大于 0；如果两项一直为 0，说明所选应用不允许 Android 捕获，不是 MiPlay 网络链路已坏。
8. 实际听音并确认播放至少持续 30 秒，再验证暂停/继续和音量；回到应用或点击通知里的“停止”结束。

静态构建和单元测试会校验首个 MiPlay 媒体包与 Windows 端字节向量一致，但连接成功、`state=2` 或持续发送都不能代替实际听音。每次改动后仍要用真实音箱验收声音和延迟。
