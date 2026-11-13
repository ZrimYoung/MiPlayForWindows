# MiPlay Desktop 开发与测试

## 结构

- `MiPlay.Core`：跨平台协议、PCM 缓冲、编码器接口、设备发现与单音箱会话服务。保留原命名空间以减少 Windows 回归。
- `MiPlay.Platform.Windows`：复用 WASAPI 采集、Windows 高精度定时以及休眠通知。
- `MiPlay.Platform.Mac` 与 `native/macos`：Core Audio Process Tap、无分配实时采集队列、后台重采样及休眠通知。
- `MiPlay.Desktop`：Avalonia 界面、托盘／菜单栏、独立配置和诊断导出。旧 WinUI 应用继续保留。

## 构建

安装 .NET 10 SDK。Mac 原生库需要 Xcode 命令行工具，运行系统要求 macOS 14.2+、Apple Silicon。
Windows 首版目标 Windows 11 x64。构建脚本支持 Python 3.9+，还需要 curl。

```sh
dotnet build MiPlay.Desktop.slnx -c Release
dotnet run --project src/MiPlay.Desktop/MiPlay.Desktop.csproj
```

从源码启动时需要可执行的 FFmpeg 在应用旁、PATH 或 `DLNACAST_FFMPEG` 指定位置。
Windows 必须提供 `aac_mf`，macOS 使用 `aac`。配置和日志写入 `MiPlayDesktop` 独立目录；不读取旧版 DLNACast 配置。

## 测试与打包

```sh
python3 scripts/prepare-desktop-ffmpeg.py macos
# 将上一条输出的完整路径设为 MIPLAY_TEST_FFMPEG
dotnet test tests/MiPlay.Tests/MiPlay.Tests.csproj -c Release
dotnet test tests/MiPlay.Desktop.Tests/MiPlay.Desktop.Tests.csproj -c Release
bash native/macos/build.sh artifacts/native --test
python3 scripts/package-desktop.py macos
```

Windows 对应使用 `windows` 参数，并运行旧测试项目验证回归。Mac 可用
`dotnet build src/MiPlay.Desktop/MiPlay.Desktop.csproj -p:DesktopPlatform=Windows`
进行交叉编译，但不能替代 Windows 运行测试。

测试包位于 `artifacts/desktop`，包含 .NET 运行时、固定版本 FFmpeg、依赖校验信息和验证说明。
Mac 编码器从官方固定源码构建，不依赖 Homebrew。Windows 编码器来自校验固定的 LGPL 构建。
公开分发前仍需确认上游主程序复用授权、完整第三方分发材料及平台签名。
CI 只上传测试产物，不自动发布 GitHub Release。

## 行为约定

- 只在用户开始时采集；首版单音箱、系统默认输出、本地继续发声。
- 默认输出变化时重新绑定采集；网络失败最多重试三次，按稳定设备标识重新发现，绝不换到其他音箱。
- 停止会取消连接、编码和重连；休眠后不自动恢复，重新启动应用也不自动播放。
- 诊断不含 PCM、认证密钥或原始协议帧；历史测试常量与真机结果分开管理。
- 当前认证协议能力限制见 `desktop-validation.md`，不能依据设备名称或广播就宣称可以播放。
