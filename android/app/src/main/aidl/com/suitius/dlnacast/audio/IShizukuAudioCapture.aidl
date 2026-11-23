package com.suitius.dlnacast.audio;

import android.os.Bundle;
import android.os.ParcelFileDescriptor;

interface IShizukuAudioCapture {
    ParcelFileDescriptor startCapture(int matchingUid, IBinder client) = 1;
    void stopCapture() = 2;
    Bundle readTrackMetadata() = 3;
    boolean setSpeakerMuted(boolean muted) = 4;
    boolean isSpeakerMuted() = 5;
    void destroy() = 16777114;
}
