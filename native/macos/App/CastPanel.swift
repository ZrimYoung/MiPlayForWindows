import SwiftUI
import AppKit

private let accent = Color(red: 0.20, green: 0.43, blue: 0.95)

private struct DesktopBackdrop: NSViewRepresentable {
    func makeNSView(context: Context) -> NSVisualEffectView {
        let view = NSVisualEffectView()
        view.material = .hudWindow; view.blendingMode = .behindWindow; view.state = .active
        return view
    }
    func updateNSView(_ nsView: NSVisualEffectView, context: Context) {}
}
private struct AmbientBackground: View {
    @Environment(\.colorScheme) private var scheme
    @Environment(\.accessibilityReduceTransparency) private var reduceTransparency
    var body: some View {
        GeometryReader { proxy in
            ZStack {
                if reduceTransparency { Color(nsColor: .windowBackgroundColor) }
                else {
                    DesktopBackdrop()
                    (scheme == .dark ? Color(red: 0.075, green: 0.085, blue: 0.12) : Color(red: 0.96, green: 0.97, blue: 0.99)).opacity(0.90)
                    RadialGradient(colors: [Color(red: 0.35, green: 0.55, blue: 0.95).opacity(scheme == .dark ? 0.23 : 0.16), .clear],
                                   center: .topLeading, startRadius: 0, endRadius: 500)
                    RadialGradient(colors: [Color(red: 0.69, green: 0.48, blue: 0.88).opacity(scheme == .dark ? 0.16 : 0.12), .clear],
                                   center: .trailing, startRadius: 0, endRadius: 440)
                    RadialGradient(colors: [Color(red: 0.30, green: 0.76, blue: 0.69).opacity(scheme == .dark ? 0.12 : 0.12), .clear],
                                   center: .bottomLeading, startRadius: 0, endRadius: 380)
                }
            }.clipped()
        }.ignoresSafeArea().allowsHitTesting(false)
    }
}
private struct SmallGlass: ViewModifier {
    func body(content: Content) -> some View {
        if #available(macOS 26.0, *) {
            content.glassEffect(.regular.interactive(), in: .capsule)
        } else {
            content.background(.ultraThinMaterial, in: Capsule())
                .overlay(Capsule().strokeBorder(.white.opacity(0.25), lineWidth: 0.5))
        }
    }
}

struct CastPanel: View {
    @ObservedObject var engine: EngineClient
    @ObservedObject private var media: NowPlayingMonitor
    @Environment(\.colorScheme) private var scheme
    @Environment(\.accessibilityReduceMotion) private var reduceMotion
    init(engine: EngineClient) { self.engine = engine; self.media = engine.media }
    private var selectedCount: Int { engine.snapshot.speakers.filter(\.selected).count }
    var body: some View {
        VStack(alignment: .leading, spacing: 22) {
            header
            nowPlaying
            localOutput
            VStack(spacing: 12) {
                HStack {
                    Text("播放到").font(.system(size: 12, weight: .semibold)).foregroundStyle(.secondary)
                    Spacer()
                    HStack(spacing: 5) {
                        Circle().fill(engine.connected ? Color.green : .gray).frame(width: 4, height: 4)
                        Text(engine.snapshot.speakers.filter(\.online).isEmpty ? "自动发现中" : "\(engine.snapshot.speakers.filter(\.online).count) 台在线")
                    }.font(.system(size: 10)).foregroundStyle(.secondary)
                    Button { engine.send("scan") } label: { Image(systemName: "arrow.clockwise").frame(width: 22, height: 22) }
                        .buttonStyle(.plain).foregroundStyle(.secondary).help(engine.snapshot.discoveryMessage)
                        .accessibilityLabel("重新扫描音箱").disabled(!engine.connected || engine.snapshot.scanning)
                }.padding(.horizontal, 3)
                ScrollView {
                    LazyVStack(spacing: 10) {
                        if engine.snapshot.speakers.isEmpty { emptyState }
                        ForEach(engine.snapshot.speakers) { card in SpeakerRow(card: card, engine: engine) }
                    }.padding(2)
                }.scrollIndicators(.hidden)
            }
            if !engine.error.isEmpty {
                HStack(alignment: .top, spacing: 8) {
                    Image(systemName: "exclamationmark.circle.fill")
                    Text(engine.error).font(.system(size: 11)).textSelection(.enabled)
                    Spacer(minLength: 0)
                    Button { engine.error = "" } label: { Image(systemName: "xmark") }.buttonStyle(.plain).accessibilityLabel("关闭提示")
                }.foregroundStyle(.red).padding(10).background(.regularMaterial, in: RoundedRectangle(cornerRadius: 10))
            }
            footer
        }
        .padding(.horizontal, 24).padding(.top, 20).padding(.bottom, 18)
        .frame(minWidth: 420, maxWidth: .infinity, minHeight: 610, maxHeight: .infinity)
        .background(AmbientBackground()).tint(accent)
        .animation(reduceMotion ? nil : .smooth(duration: 0.25), value: selectedCount)
    }
    private var header: some View {
        HStack(alignment: .center, spacing: 10) {
            Image(systemName: "airplay.audio").font(.system(size: 21, weight: .medium)).foregroundStyle(accent)
            Text("MiPlay").font(.system(size: 23, weight: .semibold, design: .rounded)).tracking(-0.6)
            Spacer()
            if selectedCount > 0 {
                Button { engine.stopAll() } label: {
                    HStack(spacing: 6) { Image(systemName: "stop.fill").font(.system(size: 9)).foregroundStyle(scheme == .dark ? Color.white : Color.black); Text("停止全部").font(.system(size: 11, weight: .medium)) }
                        .padding(.horizontal, 13).padding(.vertical, 9).modifier(SmallGlass())
                }.buttonStyle(.plain).foregroundStyle(scheme == .dark ? Color.white : Color.primary)
            }
            Menu {
                Toggle("显示系统媒体信息（实验）", isOn: $media.enabled)
                Divider()
                Button("系统音频权限…") { openPermission() }
                Button("导出诊断…") { engine.send("export") }
            } label: {
                Image(systemName: "ellipsis").font(.system(size: 14, weight: .semibold))
                    .frame(width: 33, height: 33).modifier(SmallGlass())
            }.menuStyle(.borderlessButton).menuIndicator(.hidden).fixedSize().help("更多设置")
        }
    }
    private var nowPlaying: some View {
        HStack(spacing: 17) {
            Group {
                if let artwork = media.item?.artwork {
                    Image(nsImage: artwork).resizable().aspectRatio(contentMode: .fill)
                } else {
                    ZStack {
                        RoundedRectangle(cornerRadius: 16).fill(.regularMaterial)
                        Image(systemName: "waveform").font(.system(size: 28, weight: .light)).foregroundStyle(accent.opacity(0.8))
                    }
                }
            }
            .frame(width: 74, height: 74).clipShape(RoundedRectangle(cornerRadius: 16))
            .overlay(RoundedRectangle(cornerRadius: 16).strokeBorder(.white.opacity(scheme == .dark ? 0.12 : 0.6), lineWidth: 0.75))
            .shadow(color: .black.opacity(0.07), radius: 12, x: 0, y: 5)
            VStack(alignment: .leading, spacing: 7) {
                Text(media.item?.title ?? "系统音频").font(.system(size: 21, weight: .semibold)).tracking(-0.4).lineLimit(2)
                Text(subtitle).font(.system(size: 12)).foregroundStyle(.secondary).lineLimit(1)
                HStack(spacing: 5) {
                    Image(systemName: selectedCount > 0 ? "dot.radiowaves.left.and.right" : "laptopcomputer").font(.system(size: 10))
                    Text(selectedCount > 0 ? "正在投放 · \(selectedCount) 台音箱" : "点选下方音箱，开始投放")
                        .font(.system(size: 10, weight: .medium))
                }.foregroundStyle(selectedCount > 0 ? accent : .secondary)
            }.frame(maxWidth: .infinity, alignment: .leading)
        }.padding(.vertical, 7)
    }
    private var subtitle: String {
        guard let item = media.item else { return "此 Mac 正在播放的所有声音" }
        return [item.artist, item.source].filter { !$0.isEmpty }.joined(separator: " · ")
    }
    private var localOutput: some View {
        HStack(spacing: 12) {
            Image(systemName: engine.snapshot.muteLocalOutput ? "speaker.slash" : "laptopcomputer")
                .font(.system(size: 18, weight: .regular)).foregroundStyle(.secondary).frame(width: 26)
            VStack(alignment: .leading, spacing: 4) {
                Text("本机同时出声").font(.system(size: 12, weight: .medium))
                Text(engine.snapshot.muteLocalOutput ? "仅音箱播放 · 停止后恢复本机" : "关闭可尝试静音本机，仅在音箱播放")
                    .font(.system(size: 10)).foregroundStyle(.secondary)
            }
            Spacer(minLength: 0)
            Toggle("本机同时出声", isOn: Binding(get: { !engine.snapshot.muteLocalOutput }, set: { engine.send("mute", value: !$0) }))
                .labelsHidden().toggleStyle(.switch).controlSize(.small)
                .disabled(!engine.connected || !engine.snapshot.supportsLocalMute)
                .help("实验功能：系统采集期间静音本机，停止全部投放后恢复。")
        }.padding(.horizontal, 15).padding(.vertical, 13)
            .background(.quaternary.opacity(0.35), in: RoundedRectangle(cornerRadius: 15))
            .overlay(RoundedRectangle(cornerRadius: 15).strokeBorder(.primary.opacity(0.04), lineWidth: 0.5))
    }
    private var emptyState: some View {
        VStack(spacing: 12) {
            Image(systemName: "hifispeaker.2").font(.system(size: 30, weight: .ultraLight)).foregroundStyle(.secondary)
            Text("正在寻找附近音箱").font(.system(size: 14, weight: .medium))
            Text("让音箱和 Mac 连接同一网络。\n发现后自动显示，支持同时选择多台。")
                .font(.system(size: 11)).foregroundStyle(.secondary).multilineTextAlignment(.center).lineSpacing(4)
        }.frame(maxWidth: .infinity).padding(.vertical, 35)
    }
    private var footer: some View {
        HStack(spacing: 5) {
            Image(systemName: "menubar.rectangle").font(.system(size: 10))
            Text("关闭窗口后，可在菜单栏继续控制").font(.system(size: 10))
            Spacer()
            if media.enabled && media.item == nil {
                Image(systemName: "music.note").help("尝试读取系统媒体信息；无可用信息时显示系统音频。")
            }
        }.foregroundStyle(.tertiary)
    }
    private func openPermission() {
        NSWorkspace.shared.open(URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_ScreenCapture")!)
    }
}

private struct SpeakerRow: View {
    let card: SpeakerCard
    @ObservedObject var engine: EngineClient
    @Environment(\.colorScheme) private var scheme
    @Environment(\.accessibilityReduceMotion) private var reduceMotion
    @State private var expanded = false
    @State private var volume = 30.0
    @State private var delay = 1000.0
    @State private var draggingVolume = false
    @State private var delayDirty = false
    @State private var hovering = false
    private var status: String {
        if !card.online && !card.selected { return "离线 · 保留上次设置" }
        if card.state == "Idle" { return "点选加入投放" }
        if card.state == "Casting" { return card.message.contains("没有声音") ? "当前没有声音输入" : "正在投放" }
        return card.message
    }
    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Button { engine.send("select", id: card.id, value: !card.selected) } label: {
                HStack(spacing: 12) {
                    Image(systemName: "hifispeaker.fill").font(.system(size: 21, weight: .regular))
                        .foregroundStyle(card.selected ? accent : Color.secondary)
                        .frame(width: 38, height: 42)
                        .background(card.selected ? accent.opacity(0.08) : Color.primary.opacity(0.035), in: RoundedRectangle(cornerRadius: 11))
                    VStack(alignment: .leading, spacing: 4) {
                        Text(card.name).font(.system(size: 13, weight: .semibold)).lineLimit(2).multilineTextAlignment(.leading)
                        HStack(spacing: 5) {
                            if card.state == "Connecting" || card.state == "Reconnecting" { ProgressView().controlSize(.mini) }
                            Text(status).font(.system(size: 10)).foregroundStyle(card.state == "Error" ? Color.red : Color.secondary)
                                .lineLimit(2).multilineTextAlignment(.leading)
                        }
                    }
                    Spacer(minLength: 4)
                    Image(systemName: card.selected ? "checkmark.circle.fill" : "circle")
                        .font(.system(size: 21, weight: .regular)).foregroundStyle(card.selected ? accent : .primary.opacity(0.16))
                }.contentShape(Rectangle())
            }
            .buttonStyle(.plain).disabled(!engine.connected || (!card.online && !card.selected))
            .accessibilityLabel("\(card.selected ? "停止投放到" : "投放到")\(card.name)")
            if card.selected {
                HStack(spacing: 10) {
                    Image(systemName: "speaker.wave.1.fill").font(.system(size: 10)).foregroundStyle(.secondary)
                    Slider(value: $volume, in: 0...100, onEditingChanged: { editing in
                        draggingVolume = editing
                        if !editing { engine.send("volume", id: card.id, value: Int(volume.rounded())) }
                    }).controlSize(.small).accessibilityLabel("\(card.name)音量")
                    Text(card.volume == nil && !draggingVolume ? "—" : "\(Int(volume))%")
                        .font(.system(size: 10, weight: .medium)).monospacedDigit().foregroundStyle(.secondary).frame(width: 32, alignment: .trailing)
                }
                HStack {
                    Image(systemName: "timer").font(.system(size: 10))
                    Text("播放延迟").font(.system(size: 10))
                    Spacer()
                    Button {
                        withAnimation(reduceMotion ? nil : .smooth(duration: 0.2)) { expanded.toggle() }
                    } label: {
                        HStack(spacing: 5) {
                            Text("\(card.delayMilliseconds) ms").monospacedDigit()
                            Image(systemName: expanded ? "chevron.up" : "chevron.down").font(.system(size: 8, weight: .semibold))
                        }.font(.system(size: 10, weight: .medium)).padding(.horizontal, 9).padding(.vertical, 5)
                            .background(.primary.opacity(0.045), in: Capsule())
                    }.buttonStyle(.plain).help("调节这台音箱的延迟")
                }.foregroundStyle(.secondary)
                if expanded { delayControls.transition(.opacity.combined(with: .move(edge: .top))) }
            }
        }
        .padding(15)
        .background {
            RoundedRectangle(cornerRadius: 18).fill(card.selected ? AnyShapeStyle(Color.white.opacity(scheme == .dark ? 0.075 : 0.65)) : AnyShapeStyle(.quaternary.opacity(hovering ? 0.4 : 0.16)))
        }
        .overlay(RoundedRectangle(cornerRadius: 18).strokeBorder(card.selected ? accent.opacity(scheme == .dark ? 0.27 : 0.19) : .primary.opacity(0.055), lineWidth: 0.75))
        .shadow(color: .black.opacity(card.selected ? (scheme == .dark ? 0.12 : 0.035) : 0), radius: 10, x: 0, y: 4)
        .onHover { hovering = $0 }
        .onAppear { volume = Double(card.volume ?? 30); delay = Double(card.delayMilliseconds); expanded = engine.preview && CommandLine.arguments.contains("--expanded") && card.id == "art" }
        .onChange(of: card.volume) { _, value in if !draggingVolume, let value { volume = Double(value) } }
        .onChange(of: card.delayMilliseconds) { _, value in
            if Int(delay) == value { delayDirty = false }
            if !delayDirty { delay = Double(value) }
        }
    }
    private var delayControls: some View {
        VStack(alignment: .leading, spacing: 10) {
            Divider().opacity(0.5)
            HStack {
                Text("\(Int(delay)) ms").font(.system(size: 13, weight: .semibold)).monospacedDigit()
                Spacer()
                Stepper("播放延迟", value: $delay, in: 0...2000, step: 10).labelsHidden().controlSize(.small)
            }
            Slider(value: $delay, in: 0...2000).controlSize(.small).accessibilityLabel("\(card.name)播放延迟")
                .onChange(of: delay) { _, _ in delayDirty = true }
            HStack(spacing: 7) {
                ForEach([200, 500, 1000], id: \.self) { value in
                    Button("\(value) ms") { delay = Double(value); applyDelay() }.buttonStyle(.bordered).controlSize(.small)
                }
                Spacer(minLength: 0)
                Button("应用") { applyDelay() }.buttonStyle(.borderedProminent).controlSize(.small)
            }
            Text("更低响应更快，卡顿时调高。仅此音箱短暂重连，设置自动保存；数值不代表实测总延迟。")
                .font(.system(size: 10)).foregroundStyle(.secondary).lineSpacing(3)
        }
    }
    private func applyDelay() { engine.send("delay", id: card.id, value: Int((delay / 10).rounded()) * 10) }
}
