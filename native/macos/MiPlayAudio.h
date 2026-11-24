#pragma once
#include <stdint.h>
#ifdef __cplusplus
extern "C" {
#endif
int mp_default_output(char *uid, int capacity);
// Enumeration is read-only. Returns UTF-8 JSON byte count, or a negative error.
int mp_outputs_json(char *json, int capacity);
int mp_processes_json(char *json, int capacity);
int mp_start(const char *uid, int mute, void **handle);
int mp_start_process(const char *uid, int pid, int include_children, int mute, void **handle);
// Nonblocking worker-thread read. Returns PCM bytes, zero if empty, or a negative error.
int mp_read(void *handle, unsigned char *pcm, int capacity, uint64_t *end_time_ns);
// Raw reads preserve rate, sample precision and channel order; planar sources
// are packed interleaved. Capacity errors do not consume the queued block.
int mp_raw_format_json(void *handle, char *json, int capacity);
int mp_read_raw(void *handle, unsigned char *pcm, int capacity, uint64_t *end_time_ns, int *discontinuity);
// Bounded worker-thread wait (at most 20 ms). Stop must wait for the reader
// before freeing its handle. Never blocks the real-time IOProc.
int mp_wait_capture(void *handle, int timeout_ms);
// Worker-thread snapshot; never call concurrently with mp_read or mp_stop.
// Contains source/delivered formats and counters only, never captured samples.
int mp_capture_info_json(void *handle, char *json, int capacity);
void mp_stop(void *handle);
void mp_wait_nanos(uint64_t nanos);
uint64_t mp_time_nanos(void);
void *mp_watch_sleep(void (*callback)(void));
void mp_unwatch_sleep(void *observer);
#ifdef __cplusplus
}
#endif
