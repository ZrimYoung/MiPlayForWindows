#import <AVFoundation/AVFoundation.h>
#import <CoreAudio/CoreAudioTypes.h>
#include <mach/mach_time.h>
#include <algorithm>
#include <cmath>
#include <cstdint>
#include <cstring>
#include <memory>
#include <vector>
#include "MiPlayPcmAdapter.h"

namespace {
constexpr unsigned AdapterMaxFrames = 2048, AdapterMaxChannels = 32;
constexpr int AdapterOutputCapacity = 65536;

struct PcmAdapter {
    unsigned channels = 0, bits = 0, sampleBytes = 0;
    bool floating = false, passthrough = false, discrete = false, mono = false, reversedStereo = false;
    NSString *policy;
    AVAudioConverter *converter;
    AVAudioPCMBuffer *input, *output;
    uint64_t convertedBlocks = 0, maximumConversionNanoseconds = 0;
    unsigned bufferAllocations = 0;
    mach_timebase_info_data_t timebase{};
    PcmAdapter() { mach_timebase_info(&timebase); }
    uint64_t nanos(uint64_t ticks) const {
        return (uint64_t)((__uint128_t)ticks * timebase.numer / timebase.denom);
    }
};

bool IsNumber(id value) {
    return [value isKindOfClass:NSNumber.class] && CFGetTypeID((__bridge CFTypeRef)value) != CFBooleanGetTypeID();
}
bool UnsignedValue(id value, unsigned maximum, unsigned *result) {
    if (!IsNumber(value)) return false;
    const double number = [value doubleValue];
    if (!std::isfinite(number) || number < 0 || number > maximum || std::floor(number) != number) return false;
    *result = (unsigned)number;
    return true;
}

// These labels have speaker-position semantics. Unknown, unused, coordinate-only
// and discrete labels cannot be inferred from channel count or channel order.
bool HasSpeakerMeaning(unsigned label) {
    return label >= kAudioChannelLabel_Left && label <= kAudioChannelLabel_TopBackRight;
}

AVAudioChannelLayout *SemanticLayout(NSDictionary *format, unsigned channels) {
    if (![format[@"channelLayoutNamespace"] isEqual:@"coreaudio"]) return nil;
    id values = format[@"channelLabels"];
    if (![values isKindOfClass:NSArray.class] || [values count] != channels) return nil;
    std::vector<unsigned char> storage(offsetof(AudioChannelLayout, mChannelDescriptions) + channels * sizeof(AudioChannelDescription), 0);
    auto *layout = reinterpret_cast<AudioChannelLayout *>(storage.data());
    layout->mChannelLayoutTag = kAudioChannelLayoutTag_UseChannelDescriptions;
    layout->mNumberChannelDescriptions = channels;
    for (unsigned i = 0; i < channels; ++i) {
        unsigned label;
        if (!UnsignedValue(values[i], UINT32_MAX, &label) || !HasSpeakerMeaning(label)) return nil;
        for (unsigned previous = 0; previous < i; ++previous)
            if (layout->mChannelDescriptions[previous].mChannelLabel == label) return nil;
        layout->mChannelDescriptions[i].mChannelLabel = label;
    }
    return [[AVAudioChannelLayout alloc] initWithLayout:layout];
}

bool StereoOrder(NSDictionary *format, unsigned first, unsigned second) {
    id values = format[@"channelLabels"];
    if (![format[@"channelLayoutNamespace"] isEqual:@"coreaudio"] ||
        ![values isKindOfClass:NSArray.class] || [values count] != 2) return false;
    unsigned a, b;
    return UnsignedValue(values[0], UINT32_MAX, &a) && UnsignedValue(values[1], UINT32_MAX, &b) && a == first && b == second;
}

float ReadSample(const unsigned char *value, const PcmAdapter &adapter) {
    if (adapter.floating) {
        float sample;
        std::memcpy(&sample, value, sizeof(sample));
        return std::isfinite(sample) ? sample : 0.f;
    }
    if (adapter.bits == 16) {
        int16_t sample;
        std::memcpy(&sample, value, sizeof(sample));
        return sample / 32768.f;
    }
    if (adapter.bits == 24) {
        uint32_t packed = value[0] | ((uint32_t)value[1] << 8) | ((uint32_t)value[2] << 16);
        int32_t sample = (int32_t)(packed << 8) / 256;
        return sample / 8388608.f;
    }
    int32_t sample;
    std::memcpy(&sample, value, sizeof(sample));
    return (float)(sample / 2147483648.);
}

int InitializeAdapter(NSDictionary *format, PcmAdapter *adapter) {
    if (!IsNumber(format[@"sampleRate"])) return -2;
    const double sampleRate = [format[@"sampleRate"] doubleValue];
    if (!std::isfinite(sampleRate) || sampleRate < 8000 || sampleRate > 192000 ||
        !UnsignedValue(format[@"channels"], AdapterMaxChannels, &adapter->channels) || adapter->channels == 0 ||
        !UnsignedValue(format[@"bitsPerSample"], 32, &adapter->bits) ||
        ![format[@"interleaved"] isEqual:@YES]) return -2;
    adapter->floating = [format[@"sampleType"] isEqual:@"Float"];
    if (adapter->floating ? adapter->bits != 32 :
        (![format[@"sampleType"] isEqual:@"SignedInteger"] ||
            (adapter->bits != 16 && adapter->bits != 24 && adapter->bits != 32))) return -2;
    adapter->sampleBytes = adapter->bits / 8;
    adapter->mono = adapter->channels == 1;
    AVAudioChannelLayout *layout = adapter->channels >= 2 ? SemanticLayout(format, adapter->channels) : nil;
    // Conventional stereo has a byte-exact path; an explicitly reversed or
    // otherwise labelled pair must still pass through the channel-layout map.
    if (adapter->channels == 2 && StereoOrder(format, kAudioChannelLabel_Left, kAudioChannelLabel_Right)) layout = nil;
    adapter->reversedStereo = adapter->channels == 2 && StereoOrder(format, kAudioChannelLabel_Right, kAudioChannelLabel_Left);
    // Normalize the explicit RL pair ourselves, then hand ordinary LR to the
    // converter. Do not combine a semantic layout permutation with channelMap.
    if (adapter->reversedStereo) layout = nil;
    adapter->passthrough = !adapter->floating && adapter->bits == 16 && adapter->channels == 2 && sampleRate == 48000 && !layout && !adapter->reversedStereo;
    adapter->discrete = adapter->channels > 2 && !layout;
    adapter->policy = layout || adapter->reversedStereo ? @"SystemLayout" : adapter->mono ? @"MonoDuplicate" :
        adapter->channels == 2 ? @"Stereo" : @"DiscreteAverage";
    if (adapter->passthrough) return 0;

    AVAudioFormat *inputFormat = layout
        ? [[AVAudioFormat alloc] initWithCommonFormat:AVAudioPCMFormatFloat32 sampleRate:sampleRate interleaved:YES channelLayout:layout]
        : [[AVAudioFormat alloc] initWithCommonFormat:AVAudioPCMFormatFloat32 sampleRate:sampleRate channels:2 interleaved:YES];
    AVAudioFormat *outputFormat = [[AVAudioFormat alloc] initWithCommonFormat:AVAudioPCMFormatInt16
        sampleRate:48000 channels:2 interleaved:YES];
    if (!inputFormat || !outputFormat) return -3;
    adapter->converter = [[AVAudioConverter alloc] initFromFormat:inputFormat toFormat:outputFormat];
    if (!adapter->converter) return -3;
    adapter->converter.downmix = layout != nil;
    adapter->input = [[AVAudioPCMBuffer alloc] initWithPCMFormat:inputFormat frameCapacity:AdapterMaxFrames];
    adapter->output = [[AVAudioPCMBuffer alloc] initWithPCMFormat:outputFormat frameCapacity:AdapterOutputCapacity / 4];
    if (!adapter->input || !adapter->output) return -5;
    adapter->bufferAllocations = 2;
    return 0;
}

void FillInput(PcmAdapter &adapter, const unsigned char *pcm, unsigned frames) {
    auto *output = static_cast<float *>(adapter.input.mutableAudioBufferList->mBuffers[0].mData);
    if (adapter.reversedStereo) {
        for (unsigned frame = 0; frame < frames; ++frame) {
            output[frame * 2] = ReadSample(pcm + (frame * 2 + 1) * adapter.sampleBytes, adapter);
            output[frame * 2 + 1] = ReadSample(pcm + frame * 2 * adapter.sampleBytes, adapter);
        }
        return;
    }
    if (!adapter.mono && !adapter.discrete && adapter.floating) {
        std::memcpy(output, pcm, frames * adapter.channels * sizeof(float));
        return;
    }
    for (unsigned frame = 0; frame < frames; ++frame) {
        if (adapter.discrete) {
            double left = 0, right = 0;
            for (unsigned channel = 0; channel < adapter.channels; ++channel) {
                const auto value = ReadSample(pcm + (frame * adapter.channels + channel) * adapter.sampleBytes, adapter);
                (channel % 2 == 0 ? left : right) += value;
            }
            output[frame * 2] = (float)(left / ((adapter.channels + 1) / 2));
            output[frame * 2 + 1] = (float)(right / (adapter.channels / 2));
        } else if (adapter.mono) {
            output[frame * 2] = output[frame * 2 + 1] = ReadSample(pcm + frame * adapter.sampleBytes, adapter);
        } else {
            for (unsigned channel = 0; channel < adapter.channels; ++channel)
                output[frame * adapter.channels + channel] = ReadSample(pcm + (frame * adapter.channels + channel) * adapter.sampleBytes, adapter);
        }
    }
}
}

int mp_pcm_converter_create(const char *formatJson, void **handle) {
    @autoreleasepool {
        if (!handle) return -1;
        *handle = nullptr;
        if (!formatJson) return -1;
        NSString *text = [NSString stringWithUTF8String:formatJson];
        if (!text || text.length > 16384) return -2;
        id format = [NSJSONSerialization JSONObjectWithData:[text dataUsingEncoding:NSUTF8StringEncoding] options:0 error:nil];
        if (![format isKindOfClass:NSDictionary.class]) return -2;
        auto adapter = std::make_unique<PcmAdapter>();
        const auto status = InitializeAdapter(format, adapter.get());
        if (status != 0) return status;
        *handle = adapter.release();
        return 0;
    }
}

int mp_pcm_convert(void *handle, const unsigned char *pcm, int byteCount, unsigned char *output, int capacity) {
    @autoreleasepool {
        auto *adapter = static_cast<PcmAdapter *>(handle);
        if (!adapter || !output || capacity < AdapterOutputCapacity || byteCount < 0 || (byteCount > 0 && !pcm)) return -1;
        if (byteCount == 0) return 0;
        const unsigned frameBytes = adapter->sampleBytes * adapter->channels;
        if ((unsigned)byteCount % frameBytes != 0 || (unsigned)byteCount / frameBytes > AdapterMaxFrames) return -1;
        const unsigned frames = (unsigned)byteCount / frameBytes;
        const auto started = mach_absolute_time();
        int result;
        if (adapter->passthrough) {
            std::memcpy(output, pcm, byteCount);
            result = byteCount;
        } else {
            adapter->input.frameLength = frames;
            FillInput(*adapter, pcm, frames);
            adapter->output.frameLength = 0;
            __block bool supplied = false;
            NSError *error = nil;
            const auto status = [adapter->converter convertToBuffer:adapter->output error:&error withInputFromBlock:
                ^AVAudioBuffer *(AVAudioPacketCount, AVAudioConverterInputStatus *inputStatus) {
                    if (supplied) { *inputStatus = AVAudioConverterInputStatus_NoDataNow; return nil; }
                    supplied = true;
                    *inputStatus = AVAudioConverterInputStatus_HaveData;
                    return adapter->input;
                }];
            if (error || status == AVAudioConverterOutputStatus_Error) return -6;
            result = (int)adapter->output.frameLength * 4;
            if (result > capacity) return -1;
            if (result) std::memcpy(output, adapter->output.audioBufferList->mBuffers[0].mData, result);
            ++adapter->convertedBlocks;
        }
        adapter->maximumConversionNanoseconds = std::max(adapter->maximumConversionNanoseconds,
            adapter->nanos(mach_absolute_time() - started));
        return result;
    }
}

int mp_pcm_converter_info_json(void *handle, char *json, int capacity) {
    @autoreleasepool {
        auto *adapter = static_cast<PcmAdapter *>(handle);
        if (!adapter || !json || capacity < 2) return -1;
        NSData *data = [NSJSONSerialization dataWithJSONObject:@{
            @"convertedBlocks": @(adapter->convertedBlocks),
            @"maxConversionNanoseconds": @(adapter->maximumConversionNanoseconds),
            @"bufferAllocations": @(adapter->bufferAllocations),
            @"conversionBypassed": @(adapter->passthrough),
            @"downmixPolicy": adapter->policy
        } options:0 error:nil];
        if (!data || data.length >= (NSUInteger)capacity) return -1;
        std::memcpy(json, data.bytes, data.length);
        json[data.length] = 0;
        return (int)data.length;
    }
}

void mp_pcm_converter_destroy(void *handle) { @autoreleasepool { delete static_cast<PcmAdapter *>(handle); } }

int mp_pcm_converter_reset(void *handle) {
    @autoreleasepool {
        auto *adapter = static_cast<PcmAdapter *>(handle);
        if (!adapter) return -1;
        [adapter->converter reset];
        adapter->input.frameLength = 0;
        adapter->output.frameLength = 0;
        return 0;
    }
}
