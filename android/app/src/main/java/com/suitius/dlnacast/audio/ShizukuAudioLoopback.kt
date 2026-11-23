package com.suitius.dlnacast.audio

import android.content.AttributionSource
import android.content.Context
import android.content.ContextWrapper
import android.content.pm.PackageManager
import android.media.AudioAttributes
import android.media.AudioFormat
import android.media.AudioManager
import android.media.AudioRecord
import android.os.Build
import android.os.Process
import android.os.UserHandle
import java.lang.reflect.InvocationTargetException
import java.lang.reflect.Method

internal const val ALL_MEDIA_UID = -1

/** The platform's AudioPolicy APIs run only in the user-authorized shell service. */
internal class ShizukuAudioLoopback private constructor(
    val record: AudioRecord,
    private val audioManager: AudioManager,
    private val policy: Any,
    private val unregister: Method,
) : AutoCloseable {
    override fun close() {
        try {
            record.release()
        } finally {
            unregister.invoke(audioManager, policy)
        }
    }

    companion object {
        fun open(appContext: Context, user: UserHandle, matchingUid: Int): ShizukuAudioLoopback {
            val uid = Process.myUid()
            check(uid == Process.SHELL_UID) { "需要通过 ADB 启动 Shizuku" }
            val shellPackage = "com.android.shell"
            val base = appContext.createPackageContext(shellPackage, 0)
            check(base.packageName == shellPackage && shellPackage in appContext.packageManager.getPackagesForUid(uid).orEmpty()) {
                "系统无法确认 Shizuku 的音频调用身份"
            }
            // A package context created from a system context can inherit its op-package name.
            val sourceBuilder = AttributionSource.Builder(uid).setPackageName(shellPackage)
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.UPSIDE_DOWN_CAKE) sourceBuilder.setPid(Process.myPid())
            val source = sourceBuilder.build()
            val shell = object : ContextWrapper(base) {
                override fun getOpPackageName(): String = shellPackage
                override fun getAttributionSource(): AttributionSource = source
            }
            check(shell.checkSelfPermission("android.permission.MODIFY_AUDIO_ROUTING") == PackageManager.PERMISSION_GRANTED) {
                "本机没有向 Shizuku 开放系统音频路由权限"
            }
            return try {
                register(shell, user, matchingUid)
            } catch (error: InvocationTargetException) {
                throw IllegalStateException("系统无法建立音频回环：${error.targetException.message.orEmpty()}", error.targetException)
            } catch (error: ReflectiveOperationException) {
                throw IllegalStateException("此系统的音频回环接口与 Shizuku 模式不兼容", error)
            }
        }

        private fun register(shell: Context, user: UserHandle, matchingUid: Int): ShizukuAudioLoopback {
            val ruleClass = Class.forName("android.media.audiopolicy.AudioMixingRule")
            val ruleBuilderClass = Class.forName("android.media.audiopolicy.AudioMixingRule\$Builder")
            val ruleBuilder = ruleBuilderClass.getConstructor().newInstance()
            ruleBuilderClass.getMethod("setTargetMixRole", Int::class.javaPrimitiveType)
                .invoke(ruleBuilder, ruleClass.getField("MIX_ROLE_PLAYERS").getInt(null))
            val addRule = ruleBuilderClass.getMethod("addMixRule", Int::class.javaPrimitiveType, Any::class.java)
            val usageRule = ruleClass.getField("RULE_MATCH_ATTRIBUTE_USAGE").getInt(null)
            for (usage in listOf(AudioAttributes.USAGE_MEDIA, AudioAttributes.USAGE_GAME, AudioAttributes.USAGE_UNKNOWN)) {
                addRule.invoke(ruleBuilder, usageRule, AudioAttributes.Builder().setUsage(usage).build())
            }
            val userId = UserHandle::class.java.getMethod("getIdentifier").invoke(user) as Int
            addRule.invoke(ruleBuilder, ruleClass.getField("RULE_MATCH_USERID").getInt(null), userId)
            if (matchingUid != ALL_MEDIA_UID) {
                addRule.invoke(ruleBuilder, ruleClass.getField("RULE_MATCH_UID").getInt(null), matchingUid)
            }
            val rule = ruleBuilderClass.getMethod("build").invoke(ruleBuilder)

            val mixClass = Class.forName("android.media.audiopolicy.AudioMix")
            val mixBuilderClass = Class.forName("android.media.audiopolicy.AudioMix\$Builder")
            val mixBuilder = mixBuilderClass.getConstructor(ruleClass).newInstance(rule)
            val format = AudioFormat.Builder().setSampleRate(PcmFrameBuffer.SAMPLE_RATE)
                .setChannelMask(AudioFormat.CHANNEL_OUT_STEREO)
                .setEncoding(AudioFormat.ENCODING_PCM_16BIT).build()
            mixBuilderClass.getMethod("setFormat", AudioFormat::class.java).invoke(mixBuilder, format)
            // Preserve local playback and the platform's capture-policy checks.
            mixBuilderClass.getMethod("setRouteFlags", Int::class.javaPrimitiveType)
                .invoke(mixBuilder, mixClass.getField("ROUTE_FLAG_LOOP_BACK_RENDER").getInt(null))
            val mix = mixBuilderClass.getMethod("build").invoke(mixBuilder)

            val manager = checkNotNull(shell.getSystemService(AudioManager::class.java))
            val policyClass = Class.forName("android.media.audiopolicy.AudioPolicy")
            val policyBuilderClass = Class.forName("android.media.audiopolicy.AudioPolicy\$Builder")
            val policyBuilder = policyBuilderClass.getConstructor(Context::class.java).newInstance(shell)
            policyBuilderClass.getMethod("addMix", mixClass).invoke(policyBuilder, mix)
            val policy = checkNotNull(policyBuilderClass.getMethod("build").invoke(policyBuilder))
            val unregister = AudioManager::class.java.getMethod("unregisterAudioPolicy", policyClass)
            val result = AudioManager::class.java.getMethod("registerAudioPolicy", policyClass).invoke(manager, policy)
            check(result == 0) { "系统拒绝注册音频回环（$result）" }
            try {
                val record = createRecorder(policyClass, policy, mixClass, mix, shell)
                checkNotNull(record) { "系统未返回可用的播放音频采集器" }
                if (record.state != AudioRecord.STATE_INITIALIZED || record.sampleRate != PcmFrameBuffer.SAMPLE_RATE ||
                    record.channelCount != PcmFrameBuffer.CHANNEL_COUNT || record.audioFormat != AudioFormat.ENCODING_PCM_16BIT
                ) {
                    record.release()
                    error("系统无法初始化 48 kHz 双声道音频回环")
                }
                return ShizukuAudioLoopback(record, manager, policy, unregister)
            } catch (error: Exception) {
                runCatching { unregister.invoke(manager, policy) }
                throw error
            }
        }

        private fun createRecorder(
            policyClass: Class<*>,
            policy: Any,
            mixClass: Class<*>,
            mix: Any?,
            shell: Context,
        ): AudioRecord? {
            // Newer builds accept an attributed builder for the same registered mix.
            val withBuilder = try {
                policyClass.getMethod("createAudioRecordSink", mixClass, AudioRecord.Builder::class.java)
            } catch (_: NoSuchMethodException) {
                return policyClass.getMethod("createAudioRecordSink", mixClass).invoke(policy, mix) as? AudioRecord
            }
            return withBuilder.invoke(policy, mix, AudioRecord.Builder().setContext(shell)) as? AudioRecord
        }
    }
}
