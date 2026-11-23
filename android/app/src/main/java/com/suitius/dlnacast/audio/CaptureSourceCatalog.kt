package com.suitius.dlnacast.audio

import android.content.Context
import android.content.Intent
import android.content.pm.ApplicationInfo

data class CaptureSource(
    val label: String,
    val packageName: String?,
    val uid: Int?,
    val isSuggestedMedia: Boolean = false,
) {
    val isSystemWide: Boolean get() = uid == null
}

object CaptureSourceCatalog {
    val systemWide = CaptureSource("全部可捕获音频", null, null)

    // A fallback for popular media apps that leave android:appCategory unset.
    private val knownMediaPackages = setOf(
        "com.tencent.qqmusic",
        "com.netease.cloudmusic",
        "com.kugou.android",
        "com.kuwo.player",
        "com.apple.android.music",
        "com.spotify.music",
        "com.google.android.youtube",
        "com.google.android.apps.youtube.music",
        "tv.danmaku.bili",
        "com.bilibili.app.in",
        "com.miui.player",
        "com.miui.videoplayer",
        "com.ximalaya.ting.android",
        "org.videolan.vlc",
    )

    // Relevance only: playback usage and capture policy can differ per stream.
    internal fun isSuggestedMediaApp(packageName: String, category: Int): Boolean =
        category == ApplicationInfo.CATEGORY_AUDIO || category == ApplicationInfo.CATEGORY_VIDEO ||
            packageName in knownMediaPackages

    @Suppress("DEPRECATION") // The int-flags overload also supports Android 10.
    fun load(context: Context): List<CaptureSource> {
        val packageManager = context.packageManager
        // Match the manifest's scoped package visibility for the searchable app list.
        val launcherIntent = Intent(Intent.ACTION_MAIN).addCategory(Intent.CATEGORY_LAUNCHER)
        val applications = packageManager.queryIntentActivities(launcherIntent, 0)
            .map { it.activityInfo.applicationInfo }
            .distinctBy { it.packageName }
            .filterNot { it.packageName == context.packageName }
            .map { info ->
                CaptureSource(
                    label = packageManager.getApplicationLabel(info).toString(),
                    packageName = info.packageName,
                    uid = info.uid,
                    isSuggestedMedia = isSuggestedMediaApp(info.packageName, info.category),
                )
            }
            .sortedBy { it.label.lowercase() }
        return listOf(systemWide) + applications
    }
}
