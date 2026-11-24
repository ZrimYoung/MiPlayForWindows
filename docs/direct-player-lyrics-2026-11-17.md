# 播放器歌词直接读取实验（2026-11-17）

## 用户确认的来源要求

只读取 Apple Music / QQ 音乐自己提供、展示或缓存的歌词。不向其他服务搜索歌名，不自动匹配同名歌曲，不用其他平台的歌词替代。读取失败时明确不可用。

本轮曾尝试 QQ 网络搜索兜底，用户澄清后已从应用、设置和对应自动测试中撤掉；该方案生成但未打开的 0.5.7 测试包也已移除，没有替换正在运行的版本。独立的旧网络可行性探针不属于产品运行路径。

## Apple Music

- 用户已授权只读 Apple events。在线歌曲的 `currentTrack.lyrics` 实测为空，即使 Music 自身可显示歌词；不能将空值解释成歌曲没有歌词。
- 本机 `~/Library/Caches/com.apple.Music/Cache.db` 可只读访问。歌词相关响应在 `cfurl_cache_response` / `cfurl_cache_receiver_data`，部分正文存于 `fsCachedData`。只读取响应正文，不读取请求对象、Cookie、账户凭据或请求头，不发起网络请求。
- 实际歌曲响应包含 `songs → relationships.syllable-lyrics.data[].attributes.ttmlLocalizations`；其中是 Music 自身取得的 TTML，有原始行/字时间，翻译放在头部元数据。
- 本机八条可读取记录都有带时间的歌词行；另外六条索引的正文已经缺失。缓存随 Music 清理而失效是正常情况，不回填网络搜索结果。
- Swift 只读解析器已用实际缓存确认：catalogue ID `1496696600` 的记录转成 51 行 LRC、1925 UTF-8 字节。只记录计数，不保存歌词正文。转换保留行开始时间，字级内容归并为原行；不把头部翻译或作者信息并入歌词。
- 关联必须使用当前 Music 曲目明确的 store ID，并核对媒体来源、曲目快照和时长。不得把 AppleScript databaseID、persistentID 或 MediaRemote 队列 UUID 当成 catalogue ID，也不按标题在缓存中搜索近似曲目。
- 生产读取器已加入严格受限的缓存路径：只有活动 Music 的 `iTunesStoreIdentifier` 和歌曲快照一致才查询；字段不存在、缓存缺失或解析失败均不给歌词。
- 用户重新播放后，`--current` 实测 `verifiedCatalogueID=true`、脚本字段为空、缓存歌词 1925 UTF-8 字节 / 51 行。随后 `--reader` 走实际异步读取器，当前曲目身份相符，返回 1940 字节 / 57 行，状态为「已读取 Apple Music 本地歌词及时间轴」。没有触发播放或切歌。
- 完整媒体监听器另一次返回 `tech.zrim.fusionplay.mac`，用户确认当时正在向 Mac 投送。监听器不给该媒体附加 Apple Music 歌词，属于正确的来源隔离，不改为强制覆盖媒体源。后续独立投放到 ZrBox 5 的用户实测结果见下方。

## QQ 音乐

- 当前系统媒体快照有歌名、专辑、进度和队列 UUID，没有确认可作为 QQ 歌曲 ID 的字段，不能拿该 UUID查询歌曲或猜歌词。
- 应用未提供 AppleScript 歌词字典。静态符号存在 `LyricManager`、`getLyricDirPath`、本地 LRC/QRC 相关方法，只能证明内部有这些功能，不代表外部已有可用接口。
- 应用容器缓存返回系统 PermissionError；没有绕过权限或修改容器。常见容器外缓存目录未发现。
- 最初辅助功能返回 `-25211`。本次工具实际由 `/Applications/ChatGPT.app` 宿主启动，不能要求用户寻找独立 Codex.app 条目。用户授权后，通过标准 `AXIsProcessTrustedWithOptions` 请求触发系统提示，复核已为 `true`，QQ 窗口可读；没有修改权限数据库或代替用户勾选设置。
- 首轮主窗口只读枚举 557 个节点，未发现歌词区域。用户打开歌词页后，发现 `AXTable` / `AXColumn`，列标识为 `KSLyricColumn`；窗口关闭或不可见时 AXWindows 可能为空，不能将暂时不可读解释成没歌词。
- 用户保持页面打开后，可复现探针读到完整 139 行、4492 UTF-8 字节，`incomplete=false`、时间标记行数 0。正文在内存中统计后丢弃，不输出或保存。页面的标题、歌手也可读。
- 表格、行、单元格和静态文本的公开属性中未发现时间字段；行索引和文本长度不是歌词时间。页面有进度比例控件，但不能据此重建每行时间戳。该轮没有点击、滚动、切歌或暂停。
- QQ 歌词正文直接读取已验证，可靠时间轴仍未取得；尚未启用生产自动转发或同步歌词。容器缓存继续受系统权限限制，未启用搜索匹配兜底。

## 验证

- `tests/test-mac-media.sh` 通过：保留既有媒体/控制验证，新增原始 TTML 时间、词片段、头部翻译排除、XML/实体拒绝、大小限制、精确 ID、版本时长、本地 SQLite 只读读取及路径边界测试。测试歌词是合成文本。
- `tests/probes/apple-cache/main.swift`：显式 ID 模式只读缓存并输出计数；`--current` 核验 Music 与系统媒体 ID；`--reader` 核验异步读取器；`--monitor` 核验媒体监听及来源隔离，选项仅设置在进程的 volatile domain，不写用户偏好。均不播放、不切歌、不输出歌词正文。
- 0.5.7 为直接读取测试包，保留默认关闭的「读取 Apple Music 歌词（实验）」选项；没有 QQ 在线查询功能。未触碰音频编码或连接认证。
- `tests/probes/qq-lyrics-readonly.swift` 根据实际歌词列标识定位，仅统计行完整性、字节与时间标记，不触发权限提示或任何界面动作。
- 0.5.7 原生构建、打包自检、本地 IPC、签名和 ZIP 校验通过；16 项选定媒体协议/运行循环测试通过。该记录为打包时状态；随后已打开并由用户开始投放。详细结果在忽略目录 `artifacts/desktop/0.5.7/validation.json`；用户后续确认电视歌词显示、基本同步，以及暂停/继续、跳转、切歌正常。慢加载歌词补送发现滞后，详见验证记录的 0.5.8 修正；用户随后确认 0.5.8 慢加载歌词自动补上、电视来源名为 ZrMac Air，本轮短时交互通过。
