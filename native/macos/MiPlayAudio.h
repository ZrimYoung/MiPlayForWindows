#pragma once
#include <stdint.h>
#ifdef __cplusplus
extern "C" {
#endif
int mp_default_output(char *uid, int capacity);
int mp_start(const char *uid, void **handle);
// Nonblocking worker-thread read. Returns PCM bytes, zero if empty, or a negative error.
int mp_read(void *handle, unsigned char *pcm, int capacity, uint64_t *end_time_ns);
void mp_stop(void *handle);
void mp_wait_nanos(uint64_t nanos);
uint64_t mp_time_nanos(void);
void *mp_watch_sleep(void (*callback)(void));
void mp_unwatch_sleep(void *observer);
#ifdef __cplusplus
}
#endif
