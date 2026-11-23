package com.suitius.dlnacast.audio

import android.content.pm.ApplicationInfo
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class CaptureSourceCatalogTest {
    @Test
    fun suggestsPublishedMediaCategoriesAndKnownUncategorizedMediaApps() {
        assertTrue(CaptureSourceCatalog.isSuggestedMediaApp("org.example.audio", ApplicationInfo.CATEGORY_AUDIO))
        assertTrue(CaptureSourceCatalog.isSuggestedMediaApp("org.example.video", ApplicationInfo.CATEGORY_VIDEO))
        assertTrue(CaptureSourceCatalog.isSuggestedMediaApp("com.tencent.qqmusic", ApplicationInfo.CATEGORY_UNDEFINED))
        assertFalse(CaptureSourceCatalog.isSuggestedMediaApp("org.example.calendar", ApplicationInfo.CATEGORY_PRODUCTIVITY))
        assertFalse(CaptureSourceCatalog.isSuggestedMediaApp("com.tencent.qqmusic.extra", ApplicationInfo.CATEGORY_UNDEFINED))
    }
}
