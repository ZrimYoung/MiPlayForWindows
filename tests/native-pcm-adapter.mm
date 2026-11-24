#import <Foundation/Foundation.h>
#import <CoreAudio/CoreAudioTypes.h>
#include <algorithm>
#include <array>
#include <cassert>
#include <cmath>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <string>
#include <vector>
#include "../native/macos/MiPlayPcmAdapter.h"

namespace {
constexpr unsigned Frames = 2048;
using Output = std::array<unsigned char, 65536>;

std::string Format(double rate, unsigned channels, unsigned bits = 32, bool floating = true, NSArray *labels = nil) {
    NSMutableDictionary *format = [@{@"sampleRate": @(rate), @"channels": @(channels), @"bitsPerSample": @(bits),
        @"sampleType": floating ? @"Float" : @"SignedInteger", @"interleaved": @YES} mutableCopy];
    if (labels) { format[@"channelLabels"] = labels; format[@"channelLayoutNamespace"] = @"coreaudio"; }
    NSData *data = [NSJSONSerialization dataWithJSONObject:format options:0 error:nil];
    return std::string((const char *)data.bytes, data.length);
}

struct Adapter {
    void *handle = nullptr;
    explicit Adapter(const std::string &format) { assert(mp_pcm_converter_create(format.c_str(), &handle) == 0 && handle); }
    ~Adapter() { mp_pcm_converter_destroy(handle); }
    Adapter(const Adapter &) = delete;
    Adapter &operator=(const Adapter &) = delete;
    int Convert(const void *source, int bytes, Output &output) {
        return mp_pcm_convert(handle, static_cast<const unsigned char *>(source), bytes, output.data(), (int)output.size());
    }
    NSDictionary *Info() {
        char json[2048];
        const int count = mp_pcm_converter_info_json(handle, json, sizeof(json));
        assert(count > 0);
        return [NSJSONSerialization JSONObjectWithData:[NSData dataWithBytes:json length:count] options:0 error:nil];
    }
};

std::array<int, 2> Peaks(const Output &output, int bytes) {
    assert(bytes >= 0 && bytes % 4 == 0);
    std::array<int, 2> result{};
    for (int i = 0; i < bytes / 2; ++i) {
        int16_t value;
        std::memcpy(&value, output.data() + i * 2, sizeof(value));
        result[i % 2] = std::max(result[i % 2], std::abs((int)value));
    }
    return result;
}

void TestPassthrough() {
    Adapter adapter(Format(48000, 2, 16, false));
    std::vector<int16_t> source(Frames * 2);
    for (unsigned i = 0; i < source.size(); ++i) source[i] = (int16_t)(i * 997);
    const auto saved = source;
    Output output;
    const int bytes = (int)(source.size() * sizeof(int16_t));
    assert(mp_pcm_convert(adapter.handle, (const unsigned char *)source.data(), bytes, output.data(), -1) == -1);
    assert(mp_pcm_convert(adapter.handle, (const unsigned char *)source.data(), bytes, output.data(), 65535) == -1);
    assert(adapter.Convert(source.data(), bytes - 1, output) == -1);
    assert(adapter.Convert(source.data(), bytes, output) == bytes);
    assert(std::memcmp(output.data(), source.data(), bytes) == 0 && source == saved);
    assert([adapter.Info()[@"bufferAllocations"] unsignedIntValue] == 0);
    assert([adapter.Info()[@"conversionBypassed"] boolValue]);
    assert([adapter.Info()[@"convertedBlocks"] unsignedLongLongValue] == 0);
    assert(adapter.Convert(nullptr, 0, output) == 0);
    Adapter explicitStereo(Format(48000, 2, 16, false, @[@1, @2]));
    assert(explicitStereo.Convert(source.data(), bytes, output) == bytes);
    assert(std::memcmp(output.data(), source.data(), bytes) == 0 && [explicitStereo.Info()[@"conversionBypassed"] boolValue]);
    assert(mp_pcm_converter_reset(explicitStereo.handle) == 0);
    std::puts("PCM16 stereo bypass: byte exact, original input unchanged, capacity/alignment rejected PASS");
}

void TestDiscrete(unsigned channels) {
    for (unsigned active = 0; active < channels; ++active) {
        Adapter adapter(Format(48000, channels));
        std::vector<float> source(Frames * channels, 0.f);
        for (unsigned f = 0; f < Frames; ++f) source[f * channels + active] = .2f;
        const auto saved = source;
        Output output;
        const int bytes = adapter.Convert(source.data(), (int)(source.size() * sizeof(float)), output);
        assert(bytes == Frames * 4 && source == saved);
        auto peaks = Peaks(output, bytes);
        const unsigned side = active % 2;
        const auto count = side == 0 ? (channels + 1) / 2 : channels / 2;
        const int expected = (int)std::lround(.2 * 32768. / count);
        assert(std::abs(peaks[side] - expected) <= 2 && peaks[1 - side] == 0);
        assert([adapter.Info()[@"downmixPolicy"] isEqual:@"DiscreteAverage"]);
        assert([adapter.Info()[@"bufferAllocations"] unsignedIntValue] == 2);
    }
    Adapter all(Format(48000, channels));
    std::vector<float> source(Frames * channels, .95f);
    Output output;
    auto peaks = Peaks(output, all.Convert(source.data(), (int)(source.size() * sizeof(float)), output));
    assert(std::abs(peaks[0] - 31130) <= 2 && std::abs(peaks[1] - 31130) <= 2);
    std::printf("%uch unknown layout: every channel contributes, explicit side averaging/headroom PASS\n", channels);
}

void TestKnownLayout() {
    // MPEG 5.1 A: L R C LFE Ls Rs. System downmix intentionally omits LFE.
    NSArray *labels = @[@1, @2, @3, @4, @5, @6];
    for (unsigned active = 0; active < 6; ++active) {
        Adapter adapter(Format(48000, 6, 32, true, labels));
        std::vector<float> source(Frames * 6, 0.f);
        for (unsigned f = 0; f < Frames; ++f) source[f * 6 + active] = .2f;
        Output output;
        auto peaks = Peaks(output, adapter.Convert(source.data(), (int)(source.size() * sizeof(float)), output));
        assert([adapter.Info()[@"downmixPolicy"] isEqual:@"SystemLayout"]);
        if (active == 3) assert(peaks[0] == 0 && peaks[1] == 0);
        else if (active == 2) assert(peaks[0] > 0 && peaks[0] == peaks[1]);
        else if (active == 0 || active == 4) assert(peaks[0] > 0 && peaks[1] == 0);
        else assert(peaks[1] > 0 && peaks[0] == 0);
    }
    // A permutation must follow actual labels rather than assuming index zero is L.
    Adapter reordered(Format(48000, 6, 32, true, @[@2, @1, @3, @4, @5, @6]));
    std::vector<float> source(Frames * 6, 0.f);
    for (unsigned f = 0; f < Frames; ++f) source[f * 6] = .2f;
    Output output;
    auto peaks = Peaks(output, reordered.Convert(source.data(), (int)(source.size() * sizeof(float)), output));
    assert(peaks[0] == 0 && peaks[1] > 0);
    Adapter reversedStereo(Format(48000, 2, 16, false, @[@2, @1]));
    std::vector<int16_t> stereoSource(Frames * 2, 0);
    for (unsigned f = 0; f < Frames; ++f) stereoSource[f * 2] = 6554;
    peaks = Peaks(output, reversedStereo.Convert(stereoSource.data(), (int)(stereoSource.size() * sizeof(int16_t)), output));
    assert(peaks[0] == 0 && peaks[1] > 0 && ![reversedStereo.Info()[@"conversionBypassed"] boolValue]);
    std::puts("Known 5.1 layout: actual labels, center/surround downmix, LFE omission PASS");
}

void TestIntegerPrecisions() {
    for (unsigned bits : {16u, 24u, 32u}) {
        Adapter adapter(Format(48000, 1, bits, false));
        const unsigned stride = bits / 8;
        std::vector<unsigned char> source(Frames * stride);
        const int32_t positive = bits == 16 ? 8192 : bits == 24 ? 2097152 : 536870912;
        for (unsigned f = 0; f < Frames; ++f) {
            int32_t value = f % 2 == 0 ? positive : -positive;
            for (unsigned b = 0; b < stride; ++b) source[f * stride + b] = (unsigned char)((uint32_t)value >> (8 * b));
        }
        const auto saved = source;
        Output output;
        const int bytes = adapter.Convert(source.data(), (int)source.size(), output);
        assert(bytes == Frames * 4 && source == saved);
        for (unsigned f = 0; f < Frames; ++f) {
            int16_t l, r;
            std::memcpy(&l, output.data() + f * 4, 2);
            std::memcpy(&r, output.data() + f * 4 + 2, 2);
            assert(l == r && std::abs((int)l - (f % 2 == 0 ? 8192 : -8192)) <= 1);
        }
        assert([adapter.Info()[@"downmixPolicy"] isEqual:@"MonoDuplicate"]);
        std::printf("Signed %ubit packed PCM: sign, precision, mono duplication, immutable source PASS\n", bits);
    }
}

void TestResampling(double rate) {
    Adapter adapter(Format(rate, 2));
    std::vector<float> source(Frames * 2);
    Output output;
    uint64_t total = 0, halfway = 0;
    double energy = 0;
    for (unsigned block = 0; block < 100; ++block) {
        for (unsigned f = 0; f < Frames; ++f) source[f * 2] = source[f * 2 + 1] =
            (float)(.3 * std::sin(2 * M_PI * 440 * ((uint64_t)block * Frames + f) / rate));
        const auto saved = source;
        const int bytes = adapter.Convert(source.data(), (int)(source.size() * sizeof(float)), output);
        assert(bytes >= 0 && bytes % 4 == 0 && bytes <= 65536 && source == saved);
        total += bytes / 4;
        for (int f = 0; f < bytes / 4; ++f) {
            int16_t l, r;
            std::memcpy(&l, output.data() + f * 4, 2);
            std::memcpy(&r, output.data() + f * 4 + 2, 2);
            assert(l == r && std::abs((int)l) <= 11000);
            energy += (double)l * l / (32768. * 32768.);
        }
        if (block == 49) halfway = total;
        assert([adapter.Info()[@"bufferAllocations"] unsignedIntValue] == 2);
    }
    const double expected = 100. * Frames * 48000. / rate;
    const double halfExpected = expected / 2;
    // A live converter may retain a bounded tail. Check steady-state conservation
    // separately, rather than calling end-of-stream for every capture block.
    assert(std::abs((double)total - expected) <= 4800);
    assert(std::abs((double)(total - halfway) - halfExpected) <= 4800);
    assert(energy / total > .03 && energy / total < .06);
    assert([adapter.Info()[@"convertedBlocks"] unsignedLongLongValue] == 100);
    std::printf("%.0f Hz Float32 -> 48kHz PCM16: maximum input block, signal/steady duration/buffer reuse PASS\n", rate);
}

void TestFailuresAndLifetime() {
    for (const char *json : {"null", "[]", "{}", "{invalid", "{\"channels\":33}"}) {
        void *handle = (void *)1;
        assert(mp_pcm_converter_create(json, &handle) < 0 && handle == nullptr);
    }
    for (const auto &format : {Format(48000, 0), Format(48000, 33), Format(4000, 2), Format(384000, 2),
         Format(48000, 2, 24, true), Format(48000, 2, 8, false)}) {
        void *handle = (void *)1;
        assert(mp_pcm_converter_create(format.c_str(), &handle) < 0 && handle == nullptr);
    }
    Adapter adapter(Format(48000, 2));
    std::vector<float> tooLarge((Frames + 1) * 2, .2f);
    Output output;
    assert(adapter.Convert(tooLarge.data(), (int)(tooLarge.size() * sizeof(float)), output) == -1);
    assert(adapter.Convert(tooLarge.data(), 3, output) == -1);
    assert([adapter.Info()[@"convertedBlocks"] unsignedLongLongValue] == 0);
    Adapter unknownLabels(Format(48000, 6, 32, true, @[@1, @2, @3, @4, @5, @4294967295]));
    Adapter duplicateLabels(Format(48000, 6, 32, true, @[@1, @1, @3, @4, @5, @6]));
    assert([unknownLabels.Info()[@"downmixPolicy"] isEqual:@"DiscreteAverage"]);
    assert([duplicateLabels.Info()[@"downmixPolicy"] isEqual:@"DiscreteAverage"]);
    for (unsigned i = 0; i < 64; ++i) { Adapter temporary(Format(48000, 8)); }
    char small[2];
    assert(mp_pcm_converter_info_json(adapter.handle, small, sizeof(small)) < 0);
    mp_pcm_converter_destroy(nullptr);
    std::puts("Invalid formats/capacity, non-consuming failures and repeated teardown PASS");
}

void TestDiscontinuityReset() {
    Adapter interrupted(Format(8000, 2)), control(Format(8000, 2));
    std::vector<float> source(Frames * 2);
    for (unsigned f = 0; f < Frames; ++f) source[f * 2] = source[f * 2 + 1] =
        (float)(.3 * std::sin(2 * M_PI * 440 * f / 8000.));
    Output output;
    for (int i = 0; i < 2; ++i) {
        assert(interrupted.Convert(source.data(), (int)(source.size() * sizeof(float)), output) > 0);
        assert(control.Convert(source.data(), (int)(source.size() * sizeof(float)), output) > 0);
    }
    auto before = [interrupted.Info()[@"convertedBlocks"] unsignedLongLongValue];
    assert(mp_pcm_converter_reset(interrupted.handle) == 0);
    assert([interrupted.Info()[@"convertedBlocks"] unsignedLongLongValue] == before);
    std::fill(source.begin(), source.end(), 0.f);
    for (int i = 0; i < 2; ++i) {
        auto peaks = Peaks(output, interrupted.Convert(source.data(), (int)(source.size() * sizeof(float)), output));
        assert(peaks[0] == 0 && peaks[1] == 0);
    }
    auto peaks = Peaks(output, control.Convert(source.data(), (int)(source.size() * sizeof(float)), output));
    assert(peaks[0] > 0 && peaks[1] > 0);
    assert(mp_pcm_converter_reset(nullptr) == -1);
    std::puts("Capture discontinuity: old resampler tail discarded; lifetime counters retained PASS");
}
}

int main() { @autoreleasepool {
    TestPassthrough();
    for (unsigned channels : {6u, 8u, 16u, 32u}) TestDiscrete(channels);
    TestKnownLayout();
    TestIntegerPrecisions();
    for (double rate : {8000., 32000., 44100., 48000., 96000., 192000.}) TestResampling(rate);
    TestFailuresAndLifetime();
    TestDiscontinuityReset();
} }
