# HyperOS 官方妙播只读调查（2026-11-15）

## 范围与证据

用户提供无线 ADB，并亲自操作官方妙播。手机端只执行设备属性、进程／包列表、服务状态查询、读取既有日志和 `adb pull` 安装包；未安装、启动／停止应用、点击界面、改设置、清空日志、提高日志级别、Root、抓取认证凭据或改变投放状态。

设备为 Xiaomi 15 Pro（型号 2410DPN6CC），Android 17，HyperOS `OS4.0.0.19.XOBCNXM`。
Mi Connect `6.0.456.10`，MiLink `18.2.4.0.2611031737`。
原始状态、安装包和离线反汇编仅留在被 Git 忽略的 `artifacts/phone-observation/2026-11-15/`，不随源码或测试包分发。

用户报告已完成要求的操作。其截图显示 `ZrSpeaker Pro` 和 `ZrSpeakerStereo` 同时勾选、分别显示音量条；这里只确认界面状态，不推断两者的精确同步或真实端到端延迟。

以下为第一轮观察。后续已继续核对当前官方组件的时间同步、历史帧补发，并完成用户加入／退出听音与本地编码对照，见 [低延迟与多音箱同步研究](hyperos-latency-sync-research-2026-11-15.md)。后续记录区分了静态路径、现场快照和实际听音，仍未声称已测得端到端延迟。

## 已确认的调用关系

| 观察点 | 证据与结论 |
|---|---|
| 官方入口 | `dumpsys activity services com.milink.service` 显示 `com.android.systemui` 绑定 `com.miui.miplay.audio.service.CoreService`，运行在 `com.milink.service:audio`，受 `com.miui.miplay.audio.ACCESS_STATE` 权限保护。不是第三方跨平台应用可直接调用的普通服务。 |
| 系统采集能力 | SystemUI 插件 `MiPlayController.supportMiPlayAudio` 同时检查 CoreService 与 `miui.media.MiuiAudioPlaybackRecorder` 类是否存在。说明官方能力依赖系统组件；不能直接移植到 Mac。 |
| 下层互联服务 | Mi Connect 服务状态显示 MiLink 绑定 `MiCpp2CppBinder` 及 Continuity `DeviceService`。这证实系统服务关系，尚不能证明此次音箱连接具体选择了哪一个协议分支。 |
| 发现模型 | APK 中音频 `IMiPlayClient` 提供 `startDiscovery`、`startDiscoveryWithCaller`、`isDiscovering`、`getDevices`、`onRefreshDeviceInfo`、`stopDiscovery`；回调接口有 found / update / lost。`MiPlayAudioService.startDiscovery` 调用缓存命中记录、初始化检查及下层 discovery；`DiscoveryControl` 将 native 回调转换成 found / updated / lost。官方采用缓存与事件回调维护设备列表，而非只靠界面反复整表刷新。 |
| 多设备控制 | 音频 Binder 接口 `onPlay` / `play`、`stop`、`setVolume`、`getVolume`、`setMediaInfo` 均接受设备 ID 数组；另有 `getStereoDevices`。这说明服务可以按设备集合发出操作，不能据此推断时间同步算法。 |
| 音量交互 | SystemUI 插件 `MiPlayDeviceVolumeController.safeSetVolume` 使用 100 ms 最小发送间隔、保留 pendingVolume 并延后发出最后值。这个 100 ms 是控制节流，不是音频播放延迟。 |
| 媒体信息 | 手机媒体会话能返回播放状态与曲目信息，截图有专辑封面和进度。Mac QQ 音乐另行实测，不能用手机结果代替 Mac 验证。 |

## 本轮限制

- 现有 main/system 日志及两次有时限观察未提供可用的妙播事件；没有为获取日志而修改手机。
- Android 系统 NSD 快照没有出现此轮妙播的发现请求，因此不能用它判定没有 mDNS，或断言只使用 BLE。小米自有发现服务可能绕过此 NSD 入口，这仍是待验证解释。
- 未获得此次投放的握手报文、选用的传输／认证分支、采样时钟、音箱缓冲设置或多设备同步测量，不能声称已复现官方发送端。
- 静态代码中的 UWB、Lyra、Continuity 等分支只能证明组件具备相关路径，不代表本轮音箱使用了这些路径。

## 对桌面发送端的指导

1. 设备列表保留稳定 ID 和缓存，并将 found / updated / lost 作为持续更新事件；只有确认离线时才改变可用状态，滑动控件期间不重排卡片。
2. 单台音量可采用约 100 ms 节流并保证最后一个值送达；这是后续交互改进，不与现有每台播放延迟参数混用。
3. 保持每台设备独立状态、音量和延迟，暂不把多会话播放称为官方组播或精确同步。
4. 低延迟和新认证需要后续接收端记录或获授权的网络观测来验证。本轮不依据静态字段名修改发送参数。

本轮无需用户重复操作手机。后续可继续离线分析已提取组件，新的现场观测应先确定具体问题和可观察的证据来源。
