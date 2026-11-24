#pragma once
#ifdef __cplusplus
extern "C" {
#endif

// All calls belong to one worker. Destroy only after that worker has stopped.
// format_json describes packed, little-endian, interleaved PCM (1..32 channels,
// 8..192 kHz, signed 16/24/32 bit or Float32). Optional Core Audio channel labels
// describe each channel in its actual order; absent/unknown labels stay discrete.
// Output is 48 kHz PCM16 stereo. Source samples are never modified; the caller
// keeps ownership, and its input pointer is never retained after a call.
// Returns zero on creation, a positive output byte count on conversion, or zero
// while a resampler awaits more input. Negative values report invalid arguments
// (-1), invalid format (-2), setup failure (-3), allocation failure (-5), or
// conversion failure (-6). A failed create always sets *handle to null.
int mp_pcm_converter_create(const char *format_json, void **handle);

// At most 2048 input frames per call; output capacity must be at least 65536.
// Invalid capacity/alignment is rejected before consuming any source samples.
int mp_pcm_convert(void *handle, const unsigned char *pcm, int byte_count,
                   unsigned char *output, int capacity);

// Returns UTF-8 JSON byte count or negative error. Contains counters/policy only.
int mp_pcm_converter_info_json(void *handle, char *json, int capacity);
// Discard resampler history on a capture discontinuity; preserve lifetime counters.
int mp_pcm_converter_reset(void *handle);
void mp_pcm_converter_destroy(void *handle);

#ifdef __cplusplus
}
#endif
