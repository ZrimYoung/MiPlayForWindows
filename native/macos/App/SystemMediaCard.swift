import SwiftUI

/// Matches the compact layout of macOS Now Playing; uses the observed player state.
struct SystemMediaCard: View {
    @ObservedObject var media: NowPlayingMonitor
    @Environment(\.accessibilityReduceTransparency) private var reduceTransparency
    @State private var scrubbing = false
    @State private var scrubPosition = 0.0
    @State private var scrubIdentity = ""
    private var subtitle: String {
        guard let item = media.item else { return "此 Mac 正在播放的所有声音" }
        let text = [item.artist, item.album].filter { !$0.isEmpty }.joined(separator: " – ")
        return text.isEmpty ? item.source : text
    }
    var body: some View {
        VStack(spacing: 8) {
            HStack(spacing: 14) {
                artwork
                VStack(alignment: .leading, spacing: 5) {
                    VStack(alignment: .leading, spacing: 2) {
                        Text(media.item?.title ?? "系统音频")
                            .font(.system(size: 14, weight: .semibold)).lineLimit(1)
                            .help(media.item?.title ?? "系统音频")
                        Text(subtitle).font(.system(size: 11)).foregroundStyle(.secondary)
                            .lineLimit(1).help(subtitle)
                    }
                    HStack {
                        transport(.previous, symbol: "backward.end.fill", label: "上一首")
                        Spacer(minLength: 4)
                        transport(.toggle, symbol: media.item?.playing == true ? "pause.fill" : "play.fill",
                                  label: media.item?.playing == true ? "暂停" : "播放")
                        Spacer(minLength: 4)
                        transport(.next, symbol: "forward.end.fill", label: "下一首")
                    }.padding(.horizontal, 6).frame(height: 35)
                    progress
                }.frame(maxWidth: .infinity, alignment: .leading)
            }
            if !media.commandError.isEmpty {
                Text(media.commandError).font(.caption).foregroundStyle(.secondary)
                    .frame(maxWidth: .infinity, alignment: .leading)
            }
        }
        .padding(12)
        .background(reduceTransparency ? AnyShapeStyle(Color(nsColor: .controlBackgroundColor)) : AnyShapeStyle(.regularMaterial),
                    in: RoundedRectangle(cornerRadius: 17))
        .overlay(RoundedRectangle(cornerRadius: 17).strokeBorder(.primary.opacity(0.04), lineWidth: 0.5))
    }
    private var artwork: some View {
        Group {
            if let artwork = media.item?.artwork {
                Image(nsImage: artwork).resizable().aspectRatio(contentMode: .fill)
            } else {
                ZStack {
                    Color(nsColor: .quaternaryLabelColor)
                    Image(systemName: "music.note").font(.system(size: 34, weight: .light)).foregroundStyle(.secondary)
                }
            }
        }.frame(width: 112, height: 112).clipShape(RoundedRectangle(cornerRadius: 9))
            .accessibilityLabel("专辑封面")
    }
    private func transport(_ command: MediaTransportCommand, symbol: String, label: String) -> some View {
        Button { media.send(command) } label: {
            Image(systemName: symbol).font(.system(size: 20, weight: .semibold))
                .frame(width: 32, height: 30).contentShape(Rectangle())
        }.buttonStyle(.plain).foregroundStyle(.secondary).disabled(!media.canControl)
            .accessibilityLabel(label).help(label)
    }
    private var progress: some View {
        TimelineView(.periodic(from: .now, by: 1)) { context in
            let position = scrubbing ? scrubPosition : media.item?.position(at: context.date)
            let duration = media.item?.duration
            let known = position != nil && duration != nil && duration! > 0
            VStack(spacing: 4) {
                if known {
                    Slider(value: Binding(get: { min(position!, duration!) }, set: { scrubPosition = $0 }),
                           in: 0...duration!, onEditingChanged: { editing in
                        if editing {
                            scrubPosition = media.item?.position(at: Date()) ?? 0
                            scrubIdentity = media.item?.identity ?? ""
                            scrubbing = true
                        } else {
                            scrubbing = false
                            media.seek(to: scrubPosition, expectedIdentity: scrubIdentity)
                        }
                    }).controlSize(.mini).disabled(!media.canSeek)
                        .accessibilityLabel("播放进度").help("拖动跳转播放位置")
                } else { Capsule().fill(.primary.opacity(0.12)).frame(height: 4) }
                HStack {
                    Text(known ? Self.timeLabel(position!) : "--:--")
                    Spacer()
                    Text(known ? "−" + Self.timeLabel(max(0, duration! - position!)) : "--:--")
                }.font(.system(size: 10)).monospacedDigit().foregroundStyle(.secondary)
            }
            .onChange(of: media.item?.identity) { _, _ in scrubbing = false; scrubIdentity = "" }
        }
    }
    static func timeLabel(_ seconds: Double) -> String {
        guard seconds.isFinite, seconds >= 0, seconds < 86400 * 365 else { return "--:--" }
        let value = Int(seconds)
        return value >= 3600 ? String(format: "%d:%02d:%02d", value / 3600, value / 60 % 60, value % 60)
                             : String(format: "%02d:%02d", value / 60, value % 60)
    }
}
