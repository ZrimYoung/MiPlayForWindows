#import "adapter/globals.h"
#import "adapter/now_playing.h"
#include <stdio.h>
#include <math.h>

static bool targetIsCurrent(void) {
    NSString *expected = NSProcessInfo.processInfo.environment[@"MIPLAY_MEDIA_TARGET"];
    if (!expected.length || !g_mediaRemote.getNowPlayingClient) return false;
    dispatch_semaphore_t ready = dispatch_semaphore_create(0);
    __block NSString *actual = nil;
    g_mediaRemote.getNowPlayingClient(dispatch_get_global_queue(QOS_CLASS_USER_INITIATED, 0), ^(id client) {
        MRClient *source = client;
        if ([source respondsToSelector:@selector(parentApplicationBundleIdentifier)]) actual = [source parentApplicationBundleIdentifier];
        if (!actual.length && [source respondsToSelector:@selector(bundleIdentifier)]) actual = [source bundleIdentifier];
        dispatch_semaphore_signal(ready);
    });
    if (dispatch_semaphore_wait(ready, dispatch_time(DISPATCH_TIME_NOW, NSEC_PER_SEC)) != 0) return false;
    return [actual isEqualToString:expected];
}

// Separate from the read-only observer; called only by an explicit UI action.
static void sendTransportCommand(MRCommand command) {
    @autoreleasepool {
        bool accepted = targetIsCurrent() && g_mediaRemote.sendCommand && g_mediaRemote.sendCommand(command, nil);
        if (accepted) waitForCommandCompletion();
        printf("%d\n", accepted ? 1 : 0);
        fflush(stdout);
    }
}
void miplay_media_toggle(void) { sendTransportCommand(kMRTogglePlayPause); }
void miplay_media_previous(void) { sendTransportCommand(kMRPreviousTrack); }
void miplay_media_next(void) { sendTransportCommand(kMRNextTrack); }
void miplay_media_target_check(void) {
    @autoreleasepool { printf("%d\n", targetIsCurrent() ? 1 : 0); fflush(stdout); }
}
void miplay_media_seek(void) {
    @autoreleasepool {
        const char *raw = getenv("MIPLAY_MEDIA_POSITION");
        char *end = NULL;
        double position = raw ? strtod(raw, &end) : -1;
        bool accepted = raw && end != raw && *end == '\0' && isfinite(position) &&
            position >= 0 && position <= 31536000 && g_mediaRemote.setElapsedTime && targetIsCurrent();
        if (accepted) {
            g_mediaRemote.setElapsedTime(position);
            waitForCommandCompletion();
        }
        printf("%d\n", accepted ? 1 : 0); fflush(stdout);
    }
}
void miplay_media_check(void) {
    // Symbol availability only: no playback or routing change.
    printf("%d\n", g_mediaRemote.sendCommand && g_mediaRemote.setElapsedTime && g_mediaRemote.getNowPlayingClient ? 1 : 0);
    fflush(stdout);
}
