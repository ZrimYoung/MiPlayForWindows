#import <AppKit/AppKit.h>
#import <AVFoundation/AVFoundation.h>
#import <CoreAudio/CoreAudio.h>
#import <CoreAudio/AudioHardwareTapping.h>
#import <CoreAudio/CATapDescription.h>
#include <mach/mach_time.h>
#include <atomic>
#include <array>
#include <algorithm>
#include <cstring>
#include <memory>
#include "MiPlayAudio.h"

namespace {
constexpr unsigned Slots = 32, MaxFrames = 2048, MaxChannels = 8, MaxSampleBytes = 4;
struct Block {
    std::array<unsigned char, MaxFrames * MaxChannels * MaxSampleBytes> bytes;
    unsigned frames = 0;
    uint64_t endTimeNs = 0;
};
struct Capture {
    AudioObjectID tap = kAudioObjectUnknown, aggregate = kAudioObjectUnknown;
    AudioDeviceIOProcID io = nullptr;
    AudioStreamBasicDescription format{};
    AVAudioFormat *inputFormat;
    AVAudioConverter *converter;
    std::array<Block, Slots> blocks;
    std::atomic<unsigned> write{0}, read{0};
    std::atomic<int> fault{0};
    mach_timebase_info_data_t timebase{};
    bool running = false;
    Capture() { mach_timebase_info(&timebase); }
    ~Capture() {
        if (running) AudioDeviceStop(aggregate, io);
        if (io) AudioDeviceDestroyIOProcID(aggregate, io);
        if (aggregate != kAudioObjectUnknown) AudioHardwareDestroyAggregateDevice(aggregate);
        if (tap != kAudioObjectUnknown) AudioHardwareDestroyProcessTap(tap);
    }
    uint64_t nanos(uint64_t ticks) const { return (uint64_t)((__uint128_t)ticks * timebase.numer / timebase.denom); }
};

OSStatus CaptureIO(AudioObjectID, const AudioTimeStamp *, const AudioBufferList *input,
                   const AudioTimeStamp *inputTime, AudioBufferList *, const AudioTimeStamp *, void *context) {
    auto *capture = static_cast<Capture *>(context);
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
    if (inputChannels != channels) { capture->fault.store(-4); return noErr; }
    const uint64_t startNs = capture->nanos(inputTime && (inputTime->mFlags & kAudioTimeStampHostTimeValid)
        ? inputTime->mHostTime : mach_absolute_time());
    // The real-time callback only copies into a bounded SPSC queue: no managed calls, allocation or encoding.
    for (unsigned offset = 0; offset < frames; offset += MaxFrames) {
        auto write = capture->write.load(std::memory_order_relaxed);
        const auto next = (write + 1) % Slots;
        if (next == capture->read.load(std::memory_order_acquire)) break;
        auto &block = capture->blocks[write];
        block.frames = std::min(MaxFrames, frames - offset);
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
        block.endTimeNs = startNs + (uint64_t)((offset + block.frames) * 1e9 / capture->format.mSampleRate);
        capture->write.store(next, std::memory_order_release);
    }
    return noErr;
}

int ConvertBlock(Capture *capture, const Block &block, unsigned char *pcm, int capacity) {
    AVAudioPCMBuffer *source = [[AVAudioPCMBuffer alloc] initWithPCMFormat:capture->inputFormat frameCapacity:block.frames];
    if (!source) return -5;
    source.frameLength = block.frames;
    std::memcpy(source.mutableAudioBufferList->mBuffers[0].mData, block.bytes.data(),
                block.frames * capture->inputFormat.streamDescription->mBytesPerFrame);
    AVAudioPCMBuffer *output = [[AVAudioPCMBuffer alloc] initWithPCMFormat:capture->converter.outputFormat frameCapacity:capacity / 4];
    if (!output) return -5;
    __block bool supplied = false;
    NSError *error = nil;
    auto status = [capture->converter convertToBuffer:output error:&error withInputFromBlock:
        ^AVAudioBuffer *(AVAudioPacketCount count, AVAudioConverterInputStatus *inputStatus) {
            if (supplied) { *inputStatus = AVAudioConverterInputStatus_NoDataNow; return nil; }
            supplied = true;
            *inputStatus = AVAudioConverterInputStatus_HaveData;
            return source;
        }];
    if (error || status == AVAudioConverterOutputStatus_Error) return -6;
    int bytes = (int)output.frameLength * 4;
    if (bytes > capacity) return -7;
    if (bytes) std::memcpy(pcm, output.audioBufferList->mBuffers[0].mData, bytes);
    return bytes;
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

int mp_start(const char *uid, void **handle) {
    @autoreleasepool {
        if (!handle || !uid) return -1;
        *handle = nullptr;
        if (@available(macOS 14.2, *)) {
            auto capture = std::make_unique<Capture>();
            CATapDescription *description = [[CATapDescription alloc] initExcludingProcesses:@[]
                andDeviceUID:[NSString stringWithUTF8String:uid] withStream:0];
            description.name = @"MiPlay Desktop Audio";
            description.privateTap = YES;
            description.muteBehavior = CATapUnmuted;
            auto status = AudioHardwareCreateProcessTap(description, &capture->tap);
            if (status != noErr) return status;
            AudioObjectPropertyAddress address{kAudioTapPropertyFormat, kAudioObjectPropertyScopeGlobal, kAudioObjectPropertyElementMain};
            UInt32 size = sizeof(capture->format);
            status = AudioObjectGetPropertyData(capture->tap, &address, 0, nullptr, &size, &capture->format);
            if (status != noErr) return status;
            auto format = capture->format;
            if (format.mFormatID != kAudioFormatLinearPCM || format.mChannelsPerFrame == 0 ||
                format.mChannelsPerFrame > MaxChannels || (format.mBitsPerChannel != 16 && format.mBitsPerChannel != 32) ||
                format.mSampleRate < 8000 || format.mSampleRate > 192000) return -2;
            format.mFormatFlags &= ~kAudioFormatFlagIsNonInterleaved;
            format.mBytesPerFrame = format.mChannelsPerFrame * format.mBitsPerChannel / 8;
            format.mBytesPerPacket = format.mBytesPerFrame;
            format.mFramesPerPacket = 1;
            capture->inputFormat = [[AVAudioFormat alloc] initWithStreamDescription:&format];
            AVAudioFormat *output = [[AVAudioFormat alloc] initWithCommonFormat:AVAudioPCMFormatInt16 sampleRate:48000 channels:2 interleaved:YES];
            capture->converter = [[AVAudioConverter alloc] initFromFormat:capture->inputFormat toFormat:output];
            if (!capture->converter) return -3;
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
            *handle = capture.release();
            return 0;
        }
        return -10;
    }
}

int mp_read(void *handle, unsigned char *pcm, int capacity, uint64_t *endTimeNs) {
    @autoreleasepool {
        auto *capture = static_cast<Capture *>(handle);
        if (!capture || !pcm || !endTimeNs || capacity < 65536) return -1;
        if (auto fault = capture->fault.load(); fault != 0) return fault;
        const auto read = capture->read.load(std::memory_order_relaxed);
        if (read == capture->write.load(std::memory_order_acquire)) return 0;
        const auto &block = capture->blocks[read];
        const int result = ConvertBlock(capture, block, pcm, capacity);
        *endTimeNs = block.endTimeNs;
        capture->read.store((read + 1) % Slots, std::memory_order_release);
        return result;
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
int main() {
    @autoreleasepool {
        for (double rate : {44100., 48000., 96000.}) {
            Capture capture;
            capture.inputFormat = [[AVAudioFormat alloc] initWithCommonFormat:AVAudioPCMFormatFloat32 sampleRate:rate channels:2 interleaved:YES];
            AVAudioFormat *output = [[AVAudioFormat alloc] initWithCommonFormat:AVAudioPCMFormatInt16 sampleRate:48000 channels:2 interleaved:YES];
            capture.converter = [[AVAudioConverter alloc] initFromFormat:capture.inputFormat toFormat:output];
            Block block;
            block.frames = 1024;
            unsigned char result[65536];
            int total = 0;
            double energy = 0;
            for (int n = 0; n < 100; ++n) {
                for (unsigned f = 0; f < block.frames; ++f) {
                    float value = (float)(.3 * std::sin(2 * M_PI * 440 * (n * block.frames + f) / rate));
                    std::memcpy(block.bytes.data() + f * 8, &value, 4);
                    std::memcpy(block.bytes.data() + f * 8 + 4, &value, 4);
                }
                const int bytes = ConvertBlock(&capture, block, result, sizeof(result));
                if (bytes < 0 || bytes % 4 != 0) return 1;
                total += bytes / 4;
                for (int f = 0; f < bytes / 4; ++f) {
                    int16_t left, right;
                    std::memcpy(&left, result + f * 4, 2);
                    std::memcpy(&right, result + f * 4 + 2, 2);
                    if (left != right || std::abs((int)left) > 11000) return 2;
                    energy += (double)left * left / (32768. * 32768.);
                }
            }
            const double expected = 102400. * 48000. / rate;
            if (std::abs(total - expected) > 2048) return 3;
            if (energy / total < .03 || energy / total > .06) return 4;
            std::printf("%.0f Hz -> 48000 Hz: %d stereo frames, PASS\n", rate, total);
        }
    }
    return 0;
}
#endif
