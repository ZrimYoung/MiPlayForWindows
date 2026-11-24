#import <AppKit/AppKit.h>
#import <AVFoundation/AVFoundation.h>
#import <CoreAudio/CoreAudio.h>
#import <CoreAudio/AudioHardwareTapping.h>
#import <CoreAudio/CATapDescription.h>
#import <AudioToolbox/AudioToolbox.h>
#import <dispatch/dispatch.h>
#include <mach/mach_time.h>
#include <atomic>
#include <array>
#include <algorithm>
#include <cmath>
#include <cstring>
#include <memory>
#include <vector>
#include <libproc.h>
#include <unistd.h>
#include "MiPlayAudio.h"
#include "MiPlayPcmAdapter.h"

namespace {
constexpr unsigned Slots = 32, MaxFrames = 2048, MaxChannels = 32, MaxSampleBytes = 4;
constexpr unsigned ReadCapacity = 65536, OutputFrameBytes = 4;
struct Block {
    // Resized once, to the observed stream's size, before the IOProc starts.
    std::vector<unsigned char> bytes;
    unsigned frames = 0;
    uint64_t endTimeNs = 0;
    bool discontinuity = false;
};
struct Capture {
    AudioObjectID tap = kAudioObjectUnknown, aggregate = kAudioObjectUnknown;
    AudioDeviceIOProcID io = nullptr;
    AudioStreamBasicDescription format{};
    void *legacyAdapter = nullptr;
    std::array<Block, Slots> blocks;
    std::atomic<unsigned> write{0}, read{0};
    dispatch_semaphore_t ready = dispatch_semaphore_create(0);
    dispatch_queue_t formatQueue = nullptr;
    AudioObjectPropertyListenerBlock formatListener = nil;
    std::shared_ptr<std::atomic<Capture *>> formatContext;
    bool formatWatching = false;
    std::atomic<bool> waiting{false};
    bool pendingDiscontinuity = false;
    std::vector<AudioChannelLabel> channelLabels;
    std::atomic<uint64_t> capturedFrames{0}, droppedFrames{0}, callbackCount{0};
    std::atomic<unsigned> maximumQueuedBlocks{0};
    std::atomic<uint64_t> maximumCallbackNanoseconds{0};
    std::atomic<int> fault{0};
    mach_timebase_info_data_t timebase{};
    bool running = false;
    pid_t processPID = 0;
    uint64_t processBirth = 0, processRefreshNs = 0;
    uint64_t formatRefreshNs = 0;
    bool includeChildren = false;
    CATapDescription *description;
    NSArray<NSNumber *> *processObjects;
    id mediaActivity;
    Capture() { mach_timebase_info(&timebase); }
    ~Capture() {
        if (formatContext) formatContext->store(nullptr, std::memory_order_release);
        if (running) AudioDeviceStop(aggregate, io);
        if (io) AudioDeviceDestroyIOProcID(aggregate, io);
        if (formatWatching) {
            AudioObjectPropertyAddress address{kAudioTapPropertyFormat, kAudioObjectPropertyScopeGlobal, kAudioObjectPropertyElementMain};
            AudioObjectRemovePropertyListenerBlock(tap, &address, formatQueue, formatListener);
            // No callback holding this pointer may outlive the native queue.
            dispatch_sync(formatQueue, ^{});
            formatListener = nil;
        }
        if (aggregate != kAudioObjectUnknown) AudioHardwareDestroyAggregateDevice(aggregate);
        if (tap != kAudioObjectUnknown) AudioHardwareDestroyProcessTap(tap);
        if (legacyAdapter) mp_pcm_converter_destroy(legacyAdapter);
        if (mediaActivity) [[NSProcessInfo processInfo] endActivity:mediaActivity];
    }
    uint64_t nanos(uint64_t ticks) const { return (uint64_t)((__uint128_t)ticks * timebase.numer / timebase.denom); }
};

template<typename T> void RecordMaximum(std::atomic<T> &counter, T value) {
    T previous = counter.load(std::memory_order_relaxed);
    while (value > previous && !counter.compare_exchange_weak(previous, value, std::memory_order_relaxed)) { }
}

// Capture retains source precision/rate/channels. Only planar packing changes.
int PrepareSource(Capture *capture) {
    auto format = capture->format;
    if (format.mFormatID != kAudioFormatLinearPCM || format.mChannelsPerFrame == 0 ||
        format.mChannelsPerFrame > MaxChannels || (format.mBitsPerChannel != 16 && format.mBitsPerChannel != 24 && format.mBitsPerChannel != 32) ||
        !std::isfinite(format.mSampleRate) || format.mSampleRate < 8000 || format.mSampleRate > 192000 ||
        (format.mFormatFlags & kAudioFormatFlagIsBigEndian) ||
        ((format.mFormatFlags & kAudioFormatFlagIsFloat) ? format.mBitsPerChannel != 32
            : !(format.mFormatFlags & kAudioFormatFlagIsSignedInteger))) return -2;
    const auto sourceFrameBytes = (format.mFormatFlags & kAudioFormatFlagIsNonInterleaved) ? format.mBitsPerChannel / 8
        : format.mChannelsPerFrame * format.mBitsPerChannel / 8;
    if (format.mBytesPerFrame != sourceFrameBytes) return -2;
    format.mFormatFlags &= ~kAudioFormatFlagIsNonInterleaved;
    format.mBytesPerFrame = format.mChannelsPerFrame * format.mBitsPerChannel / 8;
    format.mBytesPerPacket = format.mBytesPerFrame;
    format.mFramesPerPacket = 1;
    if (!capture->ready) return -3;
    for (auto &block : capture->blocks) block.bytes.resize(MaxFrames * format.mBytesPerFrame);
    return 0;
}

std::vector<AudioObjectID> ObjectList(AudioObjectID object, AudioObjectPropertySelector selector,
                                      AudioObjectPropertyScope scope = kAudioObjectPropertyScopeGlobal) {
    AudioObjectPropertyAddress address{selector, scope, kAudioObjectPropertyElementMain};
    UInt32 bytes = 0;
    if (AudioObjectGetPropertyDataSize(object, &address, 0, nullptr, &bytes) != noErr || bytes > 1024 * 1024) return {};
    std::vector<AudioObjectID> objects(bytes / sizeof(AudioObjectID));
    if (bytes && AudioObjectGetPropertyData(object, &address, 0, nullptr, &bytes, objects.data()) != noErr) return {};
    objects.resize(bytes / sizeof(AudioObjectID));
    return objects;
}
NSString *StringProperty(AudioObjectID object, AudioObjectPropertySelector selector) {
    AudioObjectPropertyAddress address{selector, kAudioObjectPropertyScopeGlobal, kAudioObjectPropertyElementMain};
    CFStringRef value = nullptr; UInt32 size = sizeof(value);
    if (AudioObjectGetPropertyData(object, &address, 0, nullptr, &size, &value) != noErr || !value) return @"";
    return CFBridgingRelease(value);
}

// PreferredChannelLayout describes the whole device. Only retain it when the
// tapped stream is the entire output; never guess a surround layout by count.
void ReadSourceLayout(Capture *capture, NSString *uid) {
    for (auto device : ObjectList(kAudioObjectSystemObject, kAudioHardwarePropertyDevices)) {
        if (![StringProperty(device, kAudioDevicePropertyDeviceUID) isEqualToString:uid]) continue;
        const auto streams = ObjectList(device, kAudioDevicePropertyStreams, kAudioObjectPropertyScopeOutput);
        if (streams.size() != 1) return;
        AudioObjectPropertyAddress starting{kAudioStreamPropertyStartingChannel,
            kAudioObjectPropertyScopeGlobal, kAudioObjectPropertyElementMain};
        UInt32 firstChannel = 0, startBytes = sizeof(firstChannel);
        if (AudioObjectGetPropertyData(streams[0], &starting, 0, nullptr, &startBytes, &firstChannel) != noErr || firstChannel != 1) return;
        AudioObjectPropertyAddress address{kAudioDevicePropertyPreferredChannelLayout,
            kAudioObjectPropertyScopeOutput, kAudioObjectPropertyElementMain};
        UInt32 bytes = 0;
        if (AudioObjectGetPropertyDataSize(device, &address, 0, nullptr, &bytes) != noErr ||
            bytes < offsetof(AudioChannelLayout, mChannelDescriptions) || bytes > 65536) return;
        std::vector<unsigned char> storage(bytes);
        if (AudioObjectGetPropertyData(device, &address, 0, nullptr, &bytes, storage.data()) != noErr) return;
        if (bytes < offsetof(AudioChannelLayout, mChannelDescriptions)) return;
        const auto *layout = reinterpret_cast<const AudioChannelLayout *>(storage.data());
        if (layout->mChannelLayoutTag != kAudioChannelLayoutTag_UseChannelDescriptions) {
            const bool bitmap = layout->mChannelLayoutTag == kAudioChannelLayoutTag_UseChannelBitmap;
            const auto property = bitmap ? kAudioFormatProperty_ChannelLayoutForBitmap : kAudioFormatProperty_ChannelLayoutForTag;
            const UInt32 specifier = bitmap ? layout->mChannelBitmap : layout->mChannelLayoutTag;
            UInt32 expandedBytes = 0;
            if (AudioFormatGetPropertyInfo(property, sizeof(specifier), &specifier, &expandedBytes) != noErr ||
                expandedBytes < offsetof(AudioChannelLayout, mChannelDescriptions) || expandedBytes > 65536) return;
            storage.resize(expandedBytes);
            if (AudioFormatGetProperty(property, sizeof(specifier), &specifier, &expandedBytes, storage.data()) != noErr) return;
            bytes = expandedBytes;
            layout = reinterpret_cast<const AudioChannelLayout *>(storage.data());
        }
        const auto count = layout->mNumberChannelDescriptions;
        if (count > MaxChannels || count != capture->format.mChannelsPerFrame ||
            offsetof(AudioChannelLayout, mChannelDescriptions) + count * sizeof(AudioChannelDescription) > bytes) return;
        for (UInt32 c = 0; c < count; ++c) {
            const auto &description = layout->mChannelDescriptions[c];
            if (description.mChannelLabel == kAudioChannelLabel_Unknown ||
                description.mChannelLabel == kAudioChannelLabel_UseCoordinates) {
                capture->channelLabels.clear(); return;
            }
            capture->channelLabels.push_back(description.mChannelLabel);
        }
        return;
    }
}

NSDictionary *RawFormat(Capture *capture) {
    NSMutableArray *labels = [NSMutableArray arrayWithCapacity:capture->channelLabels.size()];
    for (auto label : capture->channelLabels) [labels addObject:@(label)];
    return @{@"sampleRate": @(capture->format.mSampleRate), @"channels": @(capture->format.mChannelsPerFrame),
        @"bitsPerSample": @(capture->format.mBitsPerChannel),
        @"sampleType": (capture->format.mFormatFlags & kAudioFormatFlagIsFloat) ? @"Float" : @"SignedInteger",
        @"interleaved": @YES, @"channelLabels": labels,
        @"channelLayoutNamespace": capture->channelLabels.empty() ? (id)NSNull.null : @"coreaudio"};
}

void WakeReader(Capture *capture) {
    if (capture->waiting.exchange(false, std::memory_order_seq_cst)) dispatch_semaphore_signal(capture->ready);
}

bool SameSourceFormat(const AudioStreamBasicDescription &a, const AudioStreamBasicDescription &b) {
    return a.mFormatID == b.mFormatID && a.mSampleRate == b.mSampleRate && a.mChannelsPerFrame == b.mChannelsPerFrame &&
        a.mBitsPerChannel == b.mBitsPerChannel && a.mFormatFlags == b.mFormatFlags && a.mBytesPerFrame == b.mBytesPerFrame &&
        a.mFramesPerPacket == b.mFramesPerPacket && a.mBytesPerPacket == b.mBytesPerPacket;
}

void ObserveSourceFormat(Capture *capture, const AudioStreamBasicDescription &current) {
    if (!SameSourceFormat(current, capture->format)) {
        capture->fault.store(-13);
        WakeReader(capture);
    }
}
pid_t ProcessPID(AudioObjectID object) {
    AudioObjectPropertyAddress address{kAudioProcessPropertyPID, kAudioObjectPropertyScopeGlobal, kAudioObjectPropertyElementMain};
    pid_t pid = 0; UInt32 size = sizeof(pid);
    return AudioObjectGetPropertyData(object, &address, 0, nullptr, &size, &pid) == noErr ? pid : 0;
}
bool ProcessInfo(pid_t pid, proc_bsdinfo &info) {
    return pid > 1 && proc_pidinfo(pid, PROC_PIDTBSDINFO, 0, &info, sizeof(info)) == sizeof(info);
}
uint64_t ProcessBirth(pid_t pid) {
    proc_bsdinfo info{};
    return ProcessInfo(pid, info) ? (uint64_t)info.pbi_start_tvsec * 1000000ULL + info.pbi_start_tvusec : 0;
}
bool DescendsFrom(pid_t pid, pid_t root) {
    for (int depth = 0; pid > 1 && depth < 64; ++depth) {
        if (pid == root) return true;
        proc_bsdinfo info{}; if (!ProcessInfo(pid, info) || info.pbi_ppid == pid) return false;
        pid = info.pbi_ppid;
    }
    return false;
}
NSString *BundlePath(pid_t pid) {
    return [NSRunningApplication runningApplicationWithProcessIdentifier:pid].bundleURL.path ?: @"";
}
pid_t ApplicationPID(pid_t pid) {
    pid_t current = pid;
    for (int depth = 0; current > 1 && depth < 64; ++depth) {
        NSRunningApplication *app = [NSRunningApplication runningApplicationWithProcessIdentifier:current];
        if (app && app.activationPolicy == NSApplicationActivationPolicyRegular) return current;
        proc_bsdinfo info{}; if (!ProcessInfo(current, info) || current == info.pbi_ppid) break;
        current = info.pbi_ppid;
    }
    // XPC helpers can be reparented to launchd; an app-bundle boundary is the fallback.
    NSString *path = BundlePath(pid);
    for (NSRunningApplication *app in NSWorkspace.sharedWorkspace.runningApplications) {
        NSString *root = app.bundleURL.path;
        if (root.length && app.activationPolicy == NSApplicationActivationPolicyRegular &&
            [path hasPrefix:[root stringByAppendingString:@"/"]]) return app.processIdentifier;
    }
    return pid;
}
NSArray<NSNumber *> *SelectedProcesses(pid_t pid, bool includeChildren) {
    NSMutableArray<NSNumber *> *selected = [NSMutableArray array];
    NSString *rootPath = BundlePath(pid);
    for (auto object : ObjectList(kAudioObjectSystemObject, kAudioHardwarePropertyProcessObjectList)) {
        pid_t candidate = ProcessPID(object);
        if (candidate <= 1 || candidate == getpid()) continue;
        bool match = candidate == pid;
        if (includeChildren && !match) {
            match = DescendsFrom(candidate, pid);
            if (!match && rootPath.length)
                match = [BundlePath(candidate) hasPrefix:[rootPath stringByAppendingString:@"/"]];
        }
        if (match) [selected addObject:@(object)];
    }
    return [selected sortedArrayUsingSelector:@selector(compare:)];
}
int CopyJSON(id value, char *json, int capacity) {
    if (!json || capacity < 2) return -1;
    NSData *data = [NSJSONSerialization dataWithJSONObject:value options:0 error:nil];
    if (!data || data.length >= (NSUInteger)capacity) return -2;
    memcpy(json, data.bytes, data.length); json[data.length] = 0;
    return (int)data.length;
}

OSStatus CaptureIO(AudioObjectID, const AudioTimeStamp *, const AudioBufferList *input,
                   const AudioTimeStamp *inputTime, AudioBufferList *, const AudioTimeStamp *, void *context) {
    auto *capture = static_cast<Capture *>(context);
    if (capture->fault.load(std::memory_order_relaxed) != 0) return noErr;
    if (!input || input->mNumberBuffers == 0) return noErr;
    const auto channels = capture->format.mChannelsPerFrame;
    const auto sampleBytes = capture->format.mBitsPerChannel / 8;
    unsigned inputChannels = 0;
    unsigned frames = UINT32_MAX;
    for (unsigned i = 0; i < input->mNumberBuffers; ++i) {
        const auto &buffer = input->mBuffers[i];
        if (buffer.mNumberChannels == 0 || !buffer.mData) return noErr;
        inputChannels += buffer.mNumberChannels;
        frames = std::min(frames, buffer.mDataByteSize / (sampleBytes * buffer.mNumberChannels));
    }
    if (inputChannels != channels) { capture->fault.store(-4); WakeReader(capture); return noErr; }
    const auto callbackStarted = mach_absolute_time();
    capture->callbackCount.fetch_add(1, std::memory_order_relaxed);
    capture->capturedFrames.fetch_add(frames, std::memory_order_relaxed);
    const uint64_t startNs = capture->nanos(inputTime && (inputTime->mFlags & kAudioTimeStampHostTimeValid)
        ? inputTime->mHostTime : mach_absolute_time());
    // The real-time callback only copies into a bounded SPSC queue: no managed calls, allocation or encoding.
    for (unsigned offset = 0; offset < frames; offset += MaxFrames) {
        auto write = capture->write.load(std::memory_order_relaxed);
        const auto next = (write + 1) % Slots;
        const auto read = capture->read.load(std::memory_order_acquire);
        if (next == read) {
            capture->droppedFrames.fetch_add(frames - offset, std::memory_order_relaxed);
            capture->pendingDiscontinuity = true;
            break;
        }
        auto &block = capture->blocks[write];
        block.frames = std::min(MaxFrames, frames - offset);
        block.discontinuity = capture->pendingDiscontinuity;
        capture->pendingDiscontinuity = false;
        if (input->mNumberBuffers == 1) {
            const auto *data = static_cast<const unsigned char *>(input->mBuffers[0].mData);
            const auto frameBytes = channels * sampleBytes;
            std::memcpy(block.bytes.data(), data + offset * frameBytes, block.frames * frameBytes);
        } else {
            unsigned channelOffset = 0;
            for (unsigned b = 0; b < input->mNumberBuffers; ++b) {
                const auto &source = input->mBuffers[b];
                const auto *data = static_cast<const unsigned char *>(source.mData);
                for (unsigned f = 0; f < block.frames; ++f)
                    std::memcpy(block.bytes.data() + ((f * channels) + channelOffset) * sampleBytes,
                        data + (offset + f) * source.mNumberChannels * sampleBytes,
                        source.mNumberChannels * sampleBytes);
                channelOffset += source.mNumberChannels;
            }
        }
        block.endTimeNs = startNs + (uint64_t)((offset + block.frames) * 1e9 / capture->format.mSampleRate);
        capture->write.store(next, std::memory_order_seq_cst);
        RecordMaximum(capture->maximumQueuedBlocks, (next + Slots - read) % Slots);
    }
    WakeReader(capture);
    RecordMaximum(capture->maximumCallbackNanoseconds, capture->nanos(mach_absolute_time() - callbackStarted));
    return noErr;
}

}

int mp_default_output(char *uid, int capacity) {
    @autoreleasepool {
        AudioObjectPropertyAddress address{kAudioHardwarePropertyDefaultOutputDevice, kAudioObjectPropertyScopeGlobal, kAudioObjectPropertyElementMain};
        AudioObjectID device = kAudioObjectUnknown;
        UInt32 size = sizeof(device);
        auto status = AudioObjectGetPropertyData(kAudioObjectSystemObject, &address, 0, nullptr, &size, &device);
        if (status != noErr) return status;
        address.mSelector = kAudioDevicePropertyDeviceUID;
        CFStringRef value = nullptr;
        size = sizeof(value);
        status = AudioObjectGetPropertyData(device, &address, 0, nullptr, &size, &value);
        if (status != noErr) return status;
        bool copied = value && CFStringGetCString(value, uid, capacity, kCFStringEncodingUTF8);
        if (value) CFRelease(value);
        return copied ? 0 : -1;
    }
}

int mp_outputs_json(char *json, int capacity) {
    @autoreleasepool {
        NSMutableArray *result = [NSMutableArray array];
        for (auto device : ObjectList(kAudioObjectSystemObject, kAudioHardwarePropertyDevices)) {
            if (ObjectList(device, kAudioDevicePropertyStreams, kAudioObjectPropertyScopeOutput).empty()) continue;
            NSString *uid = StringProperty(device, kAudioDevicePropertyDeviceUID);
            NSString *name = StringProperty(device, kAudioObjectPropertyName);
            if (uid.length) [result addObject:@{@"id": uid, @"name": name.length ? name : uid}];
        }
        return CopyJSON(result, json, capacity);
    }
}
int mp_processes_json(char *json, int capacity) {
    @autoreleasepool {
        NSMutableDictionary<NSNumber *, NSDictionary *> *byPID = [NSMutableDictionary dictionary];
        for (auto object : ObjectList(kAudioObjectSystemObject, kAudioHardwarePropertyProcessObjectList)) {
            AudioObjectPropertyAddress address{kAudioProcessPropertyIsRunningOutput, kAudioObjectPropertyScopeGlobal, kAudioObjectPropertyElementMain};
            UInt32 running = 0, size = sizeof(running);
            if (AudioObjectGetPropertyData(object, &address, 0, nullptr, &size, &running) != noErr || !running) continue;
            pid_t pid = ApplicationPID(ProcessPID(object));
            if (pid <= 1 || pid == getpid()) continue;
            NSRunningApplication *app = [NSRunningApplication runningApplicationWithProcessIdentifier:pid];
            NSString *name = app.localizedName;
            if (!name.length) {
                char processName[1024] = {}; proc_name(pid, processName, sizeof(processName));
                name = [NSString stringWithUTF8String:processName];
            }
            if (!name.length) continue;
            byPID[@(pid)] = @{@"id": [@(pid) stringValue], @"name": name, @"processId": @(pid),
                             @"bundleIdentifier": app.bundleIdentifier ?: @""};
        }
        NSArray *result = [byPID.allValues sortedArrayUsingComparator:^NSComparisonResult(NSDictionary *a, NSDictionary *b) {
            return [a[@"name"] localizedStandardCompare:b[@"name"]];
        }];
        return CopyJSON(result, json, capacity);
    }
}
static int StartCapture(const char *uid, pid_t pid, bool includeChildren, int mute, void **handle) {

    @autoreleasepool {
        if (!handle || !uid) return -1;
        *handle = nullptr;
        if (@available(macOS 14.2, *)) {
            auto capture = std::make_unique<Capture>();
            NSArray<NSNumber *> *objects = pid > 0 ? SelectedProcesses(pid, includeChildren) : @[];
            capture->processPID = pid; capture->includeChildren = includeChildren;
            capture->processBirth = pid > 0 ? ProcessBirth(pid) : 0;
            if (pid > 0 && (!capture->processBirth || objects.count == 0)) return -11;
            CATapDescription *description = pid > 0
                ? [[CATapDescription alloc] initStereoMixdownOfProcesses:objects]
                : [[CATapDescription alloc] initExcludingProcesses:@[] andDeviceUID:[NSString stringWithUTF8String:uid] withStream:0];
            capture->description = description; capture->processObjects = objects;
            description.name = @"MiPlay Desktop Audio";
            description.privateTap = YES;
            description.muteBehavior = mute ? CATapMutedWhenTapped : CATapUnmuted;
            auto status = AudioHardwareCreateProcessTap(description, &capture->tap);
            if (status != noErr) return status;
            AudioObjectPropertyAddress address{kAudioTapPropertyFormat, kAudioObjectPropertyScopeGlobal, kAudioObjectPropertyElementMain};
            UInt32 size = sizeof(capture->format);
            status = AudioObjectGetPropertyData(capture->tap, &address, 0, nullptr, &size, &capture->format);
            if (status != noErr) return status;
            if (pid <= 0) ReadSourceLayout(capture.get(), [NSString stringWithUTF8String:uid]);
            if (auto prepared = PrepareSource(capture.get()); prepared != 0) return prepared;
            capture->formatQueue = dispatch_queue_create("com.zrim.miplaydesktop.capture-format", DISPATCH_QUEUE_SERIAL);
            if (!capture->formatQueue) return -5;
            capture->formatContext = std::make_shared<std::atomic<Capture *>>(capture.get());
            auto formatContext = capture->formatContext;
            capture->formatListener = ^(UInt32, const AudioObjectPropertyAddress *) {
                auto *context = formatContext->load(std::memory_order_acquire);
                if (!context) return;
                AudioStreamBasicDescription current{};
                AudioObjectPropertyAddress property{kAudioTapPropertyFormat, kAudioObjectPropertyScopeGlobal, kAudioObjectPropertyElementMain};
                UInt32 bytes = sizeof(current);
                if (AudioObjectGetPropertyData(context->tap, &property, 0, nullptr, &bytes, &current) != noErr) {
                    context->fault.store(-14); WakeReader(context);
                } else ObserveSourceFormat(context, current);
            };
            status = AudioObjectAddPropertyListenerBlock(capture->tap, &address, capture->formatQueue, capture->formatListener);
            if (status != noErr) return status;
            capture->formatWatching = true;
            NSDictionary *aggregate = @{
                @kAudioAggregateDeviceNameKey: @"MiPlay Desktop Capture",
                @kAudioAggregateDeviceUIDKey: NSUUID.UUID.UUIDString,
                @kAudioAggregateDeviceIsPrivateKey: @YES,
                @kAudioAggregateDeviceIsStackedKey: @NO,
                @kAudioAggregateDeviceTapAutoStartKey: @YES,
                @kAudioAggregateDeviceTapListKey: @[@{@kAudioSubTapUIDKey: description.UUID.UUIDString,
                    @kAudioSubTapDriftCompensationKey: @YES}]
            };
            status = AudioHardwareCreateAggregateDevice((__bridge CFDictionaryRef)aggregate, &capture->aggregate);
            if (status != noErr) return status;
            status = AudioDeviceCreateIOProcID(capture->aggregate, CaptureIO, capture.get(), &capture->io);
            if (status != noErr) return status;
            status = AudioDeviceStart(capture->aggregate, capture->io);
            if (status != noErr) return status;
            capture->running = true;
            // Keep a background/menu-bar cast latency-sensitive without
            // preventing idle system sleep or changing user power settings.
            capture->mediaActivity = [[NSProcessInfo processInfo]
                beginActivityWithOptions:(NSActivityUserInitiatedAllowingIdleSystemSleep | NSActivityLatencyCritical)
                reason:@"MiPlay system audio casting"];
            *handle = capture.release();
            return 0;
        }
        return -10;
    }
}

int mp_start(const char *uid, int mute, void **handle) { return StartCapture(uid, 0, false, mute, handle); }
int mp_start_process(const char *uid, int pid, int includeChildren, int mute, void **handle) {
    if (pid <= 1) { if (handle) *handle = nullptr; return -11; }
    return StartCapture(uid, pid, includeChildren != 0, mute, handle);
}

static int RefreshSource(Capture *capture) {
        if (auto fault = capture->fault.load(); fault != 0) return fault;
        // Worker-thread refresh only; no process discovery inside the audio callback.
        const auto now = mp_time_nanos();
        if (capture->tap != kAudioObjectUnknown && now - capture->formatRefreshNs >= 1000000000ULL) {
            capture->formatRefreshNs = now;
            AudioStreamBasicDescription current{};
            AudioObjectPropertyAddress address{kAudioTapPropertyFormat, kAudioObjectPropertyScopeGlobal, kAudioObjectPropertyElementMain};
            UInt32 size = sizeof(current);
            auto status = AudioObjectGetPropertyData(capture->tap, &address, 0, nullptr, &size, &current);
            if (status != noErr) return -14;
            if (!SameSourceFormat(current, capture->format)) return -13;
        }
        if (capture->processPID > 0 && now - capture->processRefreshNs >= 1000000000ULL) {
            capture->processRefreshNs = now;
            if (ProcessBirth(capture->processPID) != capture->processBirth) return -11;
            NSArray<NSNumber *> *objects = SelectedProcesses(capture->processPID, capture->includeChildren);
            if (objects.count && ![objects isEqualToArray:capture->processObjects]) {
                capture->description.processes = objects;
                CATapDescription *description = capture->description;
                AudioObjectPropertyAddress address{kAudioTapPropertyDescription, kAudioObjectPropertyScopeGlobal, kAudioObjectPropertyElementMain};
                if (AudioObjectSetPropertyData(capture->tap, &address, 0, nullptr, sizeof(description), &description) != noErr) return -12;
                capture->processObjects = objects;
            }
        }
        return 0;
}

int mp_read(void *handle, unsigned char *pcm, int capacity, uint64_t *endTimeNs) {
    @autoreleasepool {
        auto *capture = static_cast<Capture *>(handle);
        if (!capture || !pcm || !endTimeNs || capacity < static_cast<int>(ReadCapacity)) return -1;
        if (auto status = RefreshSource(capture); status != 0) return status;
        const auto read = capture->read.load(std::memory_order_relaxed);
        if (read == capture->write.load(std::memory_order_acquire)) return 0;
        const auto &block = capture->blocks[read];
        if (!capture->legacyAdapter) {
            NSData *data = [NSJSONSerialization dataWithJSONObject:RawFormat(capture) options:0 error:nil];
            NSString *json = [[NSString alloc] initWithData:data encoding:NSUTF8StringEncoding];
            if (auto status = mp_pcm_converter_create(json.UTF8String, &capture->legacyAdapter); status != 0) return status;
        }
        if (block.discontinuity) mp_pcm_converter_reset(capture->legacyAdapter);
        const int result = mp_pcm_convert(capture->legacyAdapter, block.bytes.data(),
            (int)(block.frames * capture->format.mChannelsPerFrame * capture->format.mBitsPerChannel / 8), pcm, capacity);
        *endTimeNs = block.endTimeNs;
        capture->read.store((read + 1) % Slots, std::memory_order_release);
        return result;
    }
}

int mp_raw_format_json(void *handle, char *json, int capacity) {
    @autoreleasepool {
        auto *capture = static_cast<Capture *>(handle);
        return capture ? CopyJSON(RawFormat(capture), json, capacity) : -1;
    }
}

int mp_read_raw(void *handle, unsigned char *pcm, int capacity, uint64_t *endTimeNs, int *discontinuity) {
    @autoreleasepool {
        auto *capture = static_cast<Capture *>(handle);
        if (!capture || !pcm || !endTimeNs || !discontinuity || capacity < 0) return -1;
        if (auto status = RefreshSource(capture); status != 0) return status;
        const auto read = capture->read.load(std::memory_order_relaxed);
        if (read == capture->write.load(std::memory_order_acquire)) return 0;
        const auto &block = capture->blocks[read];
        const auto count = block.frames * capture->format.mChannelsPerFrame * (capture->format.mBitsPerChannel / 8);
        if ((unsigned)capacity < count) return -7;
        std::memcpy(pcm, block.bytes.data(), count);
        *endTimeNs = block.endTimeNs;
        *discontinuity = block.discontinuity ? 1 : 0;
        capture->read.store((read + 1) % Slots, std::memory_order_release);
        return (int)count;
    }
}

int mp_wait_capture(void *handle, int timeoutMs) {
    auto *capture = static_cast<Capture *>(handle);
    if (!capture || timeoutMs < 0) return -1;
    auto available = [&]() { return capture->read.load(std::memory_order_relaxed) != capture->write.load(std::memory_order_seq_cst); };
    if (auto fault = capture->fault.load(); fault != 0) return fault;
    if (available()) return 1;
    // A publish can signal just before the second availability check skips the
    // wait. Discard such old tokens before arming a new wait, so they cannot
    // accumulate into an idle busy loop after playback pauses.
    while (dispatch_semaphore_wait(capture->ready, DISPATCH_TIME_NOW) == 0) { }
    capture->waiting.store(true, std::memory_order_seq_cst);
    // Double-check after advertising the wait to close the lost-wakeup window.
    if (!available() && capture->fault.load() == 0)
        dispatch_semaphore_wait(capture->ready, dispatch_time(DISPATCH_TIME_NOW, (int64_t)std::min(timeoutMs, 20) * NSEC_PER_MSEC));
    capture->waiting.store(false, std::memory_order_seq_cst);
    if (auto fault = capture->fault.load(); fault != 0) return fault;
    return available() ? 1 : 0;
}
int mp_capture_info_json(void *handle, char *json, int capacity) {
    @autoreleasepool {
        auto *capture = static_cast<Capture *>(handle);
        if (!capture) return -1;
        const auto &format = capture->format;
        const auto read = capture->read.load(std::memory_order_relaxed);
        const auto write = capture->write.load(std::memory_order_acquire);
        NSDictionary *rawFormat = RawFormat(capture);
        NSDictionary *adapter = nil;
        if (capture->legacyAdapter) {
            char json[4096];
            int length = mp_pcm_converter_info_json(capture->legacyAdapter, json, sizeof(json));
            if (length > 0) adapter = [NSJSONSerialization JSONObjectWithData:[NSData dataWithBytes:json length:length] options:0 error:nil];
        }
        return CopyJSON(@{
            @"sourceFormat": @{@"sampleRate": @(format.mSampleRate), @"channels": @(format.mChannelsPerFrame),
                @"bitsPerSample": @(format.mBitsPerChannel),
                @"sampleType": (format.mFormatFlags & kAudioFormatFlagIsFloat) ? @"Float" : @"SignedInteger",
                @"interleaved": @(!(format.mFormatFlags & kAudioFormatFlagIsNonInterleaved))},
            @"deliveredFormat": capture->legacyAdapter
                ? @{@"sampleRate": @48000, @"channels": @2, @"bitsPerSample": @16,
                    @"sampleType": @"SignedInteger", @"interleaved": @YES} : rawFormat,
            @"sourceChannelLabels": rawFormat[@"channelLabels"],
            @"channelLayoutNamespace": rawFormat[@"channelLayoutNamespace"],
            @"capturedFrames": @(capture->capturedFrames.load(std::memory_order_relaxed)),
            @"droppedFrames": @(capture->droppedFrames.load(std::memory_order_relaxed)),
            @"callbackCount": @(capture->callbackCount.load(std::memory_order_relaxed)),
            @"queuedBlocks": @((write + Slots - read) % Slots),
            @"maximumQueuedBlocks": @(capture->maximumQueuedBlocks.load(std::memory_order_relaxed)),
            @"convertedBlocks": adapter[@"convertedBlocks"] ?: @0,
            @"maximumCallbackNanoseconds": @(capture->maximumCallbackNanoseconds.load(std::memory_order_relaxed)),
            @"maximumConversionNanoseconds": adapter[@"maxConversionNanoseconds"] ?: @0,
            @"bufferAllocations": adapter[@"bufferAllocations"] ?: @0,
            @"conversionBypassed": adapter[@"conversionBypassed"] ?: @NO,
            @"downmixPolicy": adapter[@"downmixPolicy"] ?: (id)NSNull.null
        }, json, capacity);
    }
}
void mp_stop(void *handle) { @autoreleasepool { delete static_cast<Capture *>(handle); } }
void mp_wait_nanos(uint64_t nanos) {
    mach_timebase_info_data_t timebase;
    mach_timebase_info(&timebase);
    mach_wait_until(mach_absolute_time() + (uint64_t)((__uint128_t)nanos * timebase.denom / timebase.numer));
}
uint64_t mp_time_nanos(void) {
    mach_timebase_info_data_t timebase;
    mach_timebase_info(&timebase);
    return (uint64_t)((__uint128_t)mach_absolute_time() * timebase.numer / timebase.denom);
}
void *mp_watch_sleep(void (*callback)(void)) {
    id observer = [NSWorkspace.sharedWorkspace.notificationCenter addObserverForName:NSWorkspaceWillSleepNotification
        object:nil queue:nil usingBlock:^(NSNotification *) { callback(); }];
    return (__bridge_retained void *)observer;
}
void mp_unwatch_sleep(void *observer) {
    if (!observer) return;
    id value = (__bridge_transfer id)observer;
    [NSWorkspace.sharedWorkspace.notificationCenter removeObserver:value];
}

#ifdef MIPLAY_BRIDGE_TEST
#include <cmath>
#include <cstdio>
#include <cassert>
#include <thread>
#include <chrono>

void TestCaptureCopy(unsigned channels, bool interleaved) {
    Capture capture;
    // AVAudioFormat's common-format initializer does not infer a surround
    // layout. Exercise the HAL copy contract directly, without inventing one.
    const auto frameBytes = (interleaved ? channels : 1) * sizeof(float);
    capture.format = {48000, kAudioFormatLinearPCM,
        (AudioFormatFlags)(kAudioFormatFlagsNativeFloatPacked | (interleaved ? 0 : kAudioFormatFlagIsNonInterleaved)),
        (UInt32)frameBytes, 1, (UInt32)frameBytes, channels, 32, 0};
    assert(PrepareSource(&capture) == 0);
    const unsigned frames = MaxFrames + 257;
    std::vector<float> samples(frames * channels);
    struct { UInt32 count; AudioBuffer buffers[MaxChannels]; } list{};
    static_assert(offsetof(decltype(list), buffers) == offsetof(AudioBufferList, mBuffers));
    list.count = interleaved ? 1 : channels;
    for (unsigned c = 0; c < channels; ++c)
        for (unsigned f = 0; f < frames; ++f)
            samples[interleaved ? f * channels + c : c * frames + f] = (float)(f * channels + c) + .125f;
    for (unsigned b = 0; b < list.count; ++b)
        list.buffers[b] = {interleaved ? channels : 1, (UInt32)(frames * (interleaved ? channels : 1) * sizeof(float)),
            samples.data() + (interleaved ? 0 : b * frames)};
    AudioTimeStamp time{};
    time.mFlags = kAudioTimeStampHostTimeValid;
    time.mHostTime = mach_absolute_time();
    assert(CaptureIO(0, nullptr, reinterpret_cast<AudioBufferList *>(&list), &time, nullptr, nullptr, &capture) == noErr);
    unsigned consumed = 0;
    for (unsigned b = 0; b < capture.write.load(); ++b) {
        const auto &block = capture.blocks[b];
        for (unsigned f = 0; f < block.frames; ++f)
            for (unsigned c = 0; c < channels; ++c) {
                float actual;
                std::memcpy(&actual, block.bytes.data() + (f * channels + c) * sizeof(float), sizeof(float));
                assert(actual == (float)((consumed + f) * channels + c) + .125f);
            }
        consumed += block.frames;
        assert(block.endTimeNs == capture.nanos(time.mHostTime) + (uint64_t)(consumed * 1e9 / 48000));
    }
    assert(consumed == frames && capture.capturedFrames == frames && capture.droppedFrames == 0);
    assert(capture.maximumQueuedBlocks == 2 && capture.callbackCount == 1);
    char json[4096];
    int length = mp_capture_info_json(&capture, json, sizeof(json));
    assert(length > 0);
    NSDictionary *diagnostic = [NSJSONSerialization JSONObjectWithData:[NSData dataWithBytes:json length:length] options:0 error:nil];
    assert([diagnostic[@"sourceFormat"][@"channels"] unsignedIntValue] == channels);
    assert([diagnostic[@"sourceFormat"][@"interleaved"] boolValue] == interleaved);
    assert([diagnostic[@"deliveredFormat"][@"channels"] unsignedIntValue] == channels);
    assert([diagnostic[@"deliveredFormat"][@"interleaved"] boolValue]);
    assert(mp_capture_info_json(&capture, json, 8) == -2);
    std::vector<unsigned char> raw(MaxFrames * channels * sizeof(float));
    unsigned readFrames = 0;
    while (readFrames < frames) {
        uint64_t timestamp = 0; int discontinuity = -1;
        assert(mp_read_raw(&capture, raw.data(), 1, &timestamp, &discontinuity) == -7);
        const int bytes = mp_read_raw(&capture, raw.data(), (int)raw.size(), &timestamp, &discontinuity);
        assert(bytes > 0 && discontinuity == 0);
        for (unsigned f = 0; f < (unsigned)bytes / channels / sizeof(float); ++f)
            for (unsigned c = 0; c < channels; ++c) {
                float value;
                std::memcpy(&value, raw.data() + (f * channels + c) * sizeof(float), sizeof(float));
                assert(value == (float)((readFrames + f) * channels + c) + .125f);
            }
        readFrames += bytes / channels / sizeof(float);
        assert(timestamp == capture.nanos(time.mHostTime) + (uint64_t)(readFrames * 1e9 / 48000));
    }
    assert(capture.legacyAdapter == nullptr);
    std::printf("%uch %s capture: channel order, split blocks, timestamps and format diagnostics PASS\n",
        channels, interleaved ? "interleaved" : "planar");
}

void TestQueueOverflow() {
    Capture capture;
    AVAudioFormat *format = [[AVAudioFormat alloc] initWithCommonFormat:AVAudioPCMFormatFloat32
        sampleRate:48000 channels:2 interleaved:YES];
    capture.format = *format.streamDescription;
    assert(PrepareSource(&capture) == 0);
    float samples[128]{};
    AudioBufferList list{1, {{2, sizeof(samples), samples}}};
    for (unsigned i = 0; i < Slots + 4; ++i)
        CaptureIO(0, nullptr, &list, nullptr, nullptr, nullptr, &capture);
    assert(capture.callbackCount == Slots + 4 && capture.capturedFrames == (Slots + 4) * 64);
    assert(capture.maximumQueuedBlocks == Slots - 1 && capture.droppedFrames == 5 * 64);
    unsigned char raw[1024]; uint64_t timestamp; int discontinuity;
    while (mp_read_raw(&capture, raw, sizeof(raw), &timestamp, &discontinuity) > 0) assert(discontinuity == 0);
    CaptureIO(0, nullptr, &list, nullptr, nullptr, nullptr, &capture);
    assert(mp_read_raw(&capture, raw, sizeof(raw), &timestamp, &discontinuity) == sizeof(samples));
    assert(discontinuity == 1);
    CaptureIO(0, nullptr, &list, nullptr, nullptr, nullptr, &capture);
    assert(mp_read_raw(&capture, raw, sizeof(raw), &timestamp, &discontinuity) == sizeof(samples));
    assert(discontinuity == 0);
    // A rejected format cannot write beyond a block or publish partial data.
    auto write = capture.write.load();
    list.mBuffers[0].mNumberChannels = 1;
    CaptureIO(0, nullptr, &list, nullptr, nullptr, nullptr, &capture);
    assert(capture.fault == -4 && capture.write == write);
    std::puts("Bounded capture queue: exact dropped-frame counts and format rejection PASS");
}

void TestRawFormatsAndWait() {
    for (unsigned bits : {16u, 24u, 32u}) {
        Capture capture;
        capture.format = {96000, kAudioFormatLinearPCM, kAudioFormatFlagIsSignedInteger | kAudioFormatFlagIsPacked,
            bits / 8 * 8, 1, bits / 8 * 8, 8, bits, 0};
        assert(PrepareSource(&capture) == 0);
        std::vector<unsigned char> samples(128 * bits / 8 * 8);
        for (unsigned i = 0; i < samples.size(); ++i) samples[i] = (unsigned char)(i * 17);
        AudioBufferList list{1, {{8, (UInt32)samples.size(), samples.data()}}};
        CaptureIO(0, nullptr, &list, nullptr, nullptr, nullptr, &capture);
        std::vector<unsigned char> raw(samples.size()); uint64_t timestamp; int discontinuity;
        assert(mp_read_raw(&capture, raw.data(), (int)raw.size(), &timestamp, &discontinuity) == raw.size());
        assert(raw == samples);
    }
    Capture capture;
    AVAudioFormat *format = [[AVAudioFormat alloc] initWithCommonFormat:AVAudioPCMFormatFloat32
        sampleRate:48000 channels:2 interleaved:YES];
    capture.format = *format.streamDescription;
    assert(PrepareSource(&capture) == 0);
    auto start = std::chrono::steady_clock::now();
    assert(mp_wait_capture(&capture, 2) == 0);
    assert(std::chrono::steady_clock::now() - start < std::chrono::seconds(1));
    float samples[128]{};
    AudioBufferList list{1, {{2, sizeof(samples), samples}}};
    std::thread producer([&] {
        std::this_thread::sleep_for(std::chrono::milliseconds(2));
        CaptureIO(0, nullptr, &list, nullptr, nullptr, nullptr, &capture);
    });
    int ready = 0;
    for (int attempt = 0; attempt < 20 && !ready; ++attempt) ready = mp_wait_capture(&capture, 20);
    producer.join();
    assert(ready == 1 && mp_wait_capture(&capture, 0) == 1);
    unsigned char raw[1024]; uint64_t timestamp; int discontinuity;
    assert(mp_read_raw(&capture, raw, sizeof(raw), &timestamp, &discontinuity) > 0);
    for (int iteration = 0; iteration < 128; ++iteration) {
        // Reproduce publish+signal followed by an availability fast path.
        capture.waiting.store(true);
        CaptureIO(0, nullptr, &list, nullptr, nullptr, nullptr, &capture);
        assert(mp_wait_capture(&capture, 20) == 1);
        assert(mp_read_raw(&capture, raw, sizeof(raw), &timestamp, &discontinuity) > 0);
    }
    start = std::chrono::steady_clock::now();
    assert(mp_wait_capture(&capture, 2) == 0);
    assert(std::chrono::steady_clock::now() - start >= std::chrono::milliseconds(1));
    assert(dispatch_semaphore_wait(capture.ready, DISPATCH_TIME_NOW) != 0);
    ObserveSourceFormat(&capture, capture.format);
    assert(capture.fault == 0);
    capture.waiting.store(true);
    auto changed = capture.format;
    changed.mSampleRate = 96000;
    ObserveSourceFormat(&capture, changed);
    assert(mp_wait_capture(&capture, 20) == -13);
    auto write = capture.write.load();
    CaptureIO(0, nullptr, &list, nullptr, nullptr, nullptr, &capture);
    assert(capture.write == write);
    capture.fault.store(0);
    changed = capture.format;
    changed.mFormatID = kAudioFormatMPEG4AAC;
    ObserveSourceFormat(&capture, changed);
    assert(mp_wait_capture(&capture, 20) == -13);
    assert(mp_wait_capture(nullptr, 20) == -1);
    std::puts("Packed S16/S24/S32 raw data, event wakeup, stale signals and format-change stop PASS");
}

void TestPcmPassthrough() {
    Capture capture;
    AVAudioFormat *format = [[AVAudioFormat alloc] initWithCommonFormat:AVAudioPCMFormatInt16
        sampleRate:48000 channels:2 interleaved:YES];
    capture.format = *format.streamDescription;
    assert(PrepareSource(&capture) == 0);
    std::vector<int16_t> samples(MaxFrames * 2);
    for (unsigned i = 0; i < samples.size(); ++i) samples[i] = (int16_t)(i * 997);
    AudioBufferList list{1, {{2, (UInt32)(samples.size() * sizeof(int16_t)), samples.data()}}};
    CaptureIO(0, nullptr, &list, nullptr, nullptr, nullptr, &capture);
    unsigned char result[ReadCapacity];
    uint64_t timestamp;
    assert(mp_read(&capture, result, -1, &timestamp) == -1);
    assert(mp_read(&capture, result, sizeof(result) - 1, &timestamp) == -1);
    assert(mp_read(&capture, result, sizeof(result), &timestamp) == samples.size() * sizeof(int16_t));
    assert(std::memcmp(result, samples.data(), samples.size() * sizeof(int16_t)) == 0);
    assert(mp_read(&capture, result, sizeof(result), &timestamp) == 0);
    assert(capture.legacyAdapter != nullptr);
    std::puts("48kHz PCM16 stereo: byte-exact bypass, no converter buffers, safe read capacity PASS");
}

int main() {
    @autoreleasepool {
        for (unsigned channels : {2u, 6u, 8u, 16u, 32u})
            for (bool interleaved : {true, false}) TestCaptureCopy(channels, interleaved);
        TestQueueOverflow();
        TestPcmPassthrough();
        TestRawFormatsAndWait();
    }
    return 0;
}
#endif
