package com.vex.phonebackup.agent

import android.content.Context
import android.system.Os
import android.util.Base64
import com.topjohnwu.superuser.CallbackList
import com.topjohnwu.superuser.Shell
import org.json.JSONObject
import java.io.File
import java.io.FileOutputStream
import java.io.OutputStream

object RootHelper {
    private const val ASSET = "helper/arm64-v8a/phonebackup-helper"

    fun probe(context: Context): JSONObject? = withExtracted(context) { helper ->
        val result = Shell.cmd("${quote(helper.absolutePath)} probe").exec()
        if (!result.isSuccess) return@withExtracted null
        result.out.firstOrNull()?.let(::JSONObject)
    }

    fun scan(
        context: Context,
        root: String,
        includeCaches: Boolean,
        fullHash: Boolean,
        emit: (String) -> Unit
    ): Boolean = withExtracted(context) { helper ->
        val output = object : CallbackList<String>() {
            override fun onAddElement(value: String) = emit(value)
        }
        val errors = mutableListOf<String>()
        val command = buildString {
            append(quote(helper.absolutePath))
            append(" scan --root ").append(quote(root))
            if (includeCaches) append(" --include-caches")
            if (fullHash) append(" --full-hash")
        }
        Shell.cmd(command).to(output, errors).exec().isSuccess
    }

    fun read(
        context: Context,
        root: String,
        relative: String,
        emit: (ByteArray) -> Unit
    ): Boolean = withExtracted(context) { helper ->
        val output = object : CallbackList<String>() {
            override fun onAddElement(value: String) {
                if (value.isNotEmpty()) emit(Base64.decode(value, Base64.DEFAULT))
            }
        }
        val errors = mutableListOf<String>()
        val command = "${quote(helper.absolutePath)} read-base64 " +
            "--root ${quote(root)} --relative ${quote(relative)}"
        Shell.cmd(command).to(output, errors).exec().isSuccess
    }

    fun restore(
        context: Context,
        root: String,
        relative: String,
        mode: Int,
        uid: Int,
        gid: Int,
        modifiedUnixNanos: Long,
        selinuxLabel: String?,
        consume: (OutputStream) -> Unit
    ): Boolean = withExtracted(context) { helper ->
        val directory = File(context.noBackupFilesDir, "root-pipes").apply { mkdirs() }
        val fifo = File(directory, "restore-${System.nanoTime()}.fifo")
        runCatching {
            Os.mkfifo(fifo.absolutePath, 0b110000000)
            val command = buildString {
                append(quote(helper.absolutePath))
                append(" restore --root ").append(quote(root))
                append(" --relative ").append(quote(relative))
                append(" --mode ").append(mode)
                append(" --uid ").append(uid)
                append(" --gid ").append(gid)
                append(" --modified-unix-nanos ").append(modifiedUnixNanos)
                if (!selinuxLabel.isNullOrBlank()) {
                    append(" --selinux-label ").append(quote(selinuxLabel))
                }
                append(" < ").append(quote(fifo.absolutePath))
            }
            val future = Shell.cmd(command).enqueue()
            FileOutputStream(fifo).buffered(256 * 1024).use(consume)
            future.get().isSuccess
        }.getOrDefault(false).also {
            fifo.delete()
        }
    }

    fun restoreDirectory(
        context: Context,
        root: String,
        relative: String,
        mode: Int,
        uid: Int,
        gid: Int,
        modifiedUnixNanos: Long,
        selinuxLabel: String?
    ): Boolean = withExtracted(context) { helper ->
        val command = buildString {
            append(quote(helper.absolutePath))
            append(" restore-directory --root ").append(quote(root))
            append(" --relative ").append(quote(relative))
            append(" --mode ").append(mode)
            append(" --uid ").append(uid)
            append(" --gid ").append(gid)
            append(" --modified-unix-nanos ").append(modifiedUnixNanos)
            if (!selinuxLabel.isNullOrBlank()) {
                append(" --selinux-label ").append(quote(selinuxLabel))
            }
        }
        Shell.cmd(command).exec().isSuccess
    }

    @Synchronized
    fun <T> withExtracted(context: Context, block: (File) -> T): T {
        val directory = File(context.noBackupFilesDir, "root-helper").apply { mkdirs() }
        val target = File(directory, "phonebackup-helper-${BuildConfig.VERSION_CODE}")
        if (!target.isFile) {
            val temporary = File(directory, "${target.name}.${System.nanoTime()}.tmp")
            try {
                context.assets.open(ASSET).use { input ->
                    temporary.outputStream().use { output -> input.copyTo(output, 256 * 1024) }
                }
                Os.chmod(temporary.absolutePath, 0b111000000)
                if (!temporary.renameTo(target))
                    throw IllegalStateException("Cannot publish root-helper")
            } finally {
                temporary.delete()
            }
        }
        return block(target)
    }

    @Synchronized
    fun cleanup(context: Context) {
        File(context.noBackupFilesDir, "root-helper").listFiles()?.forEach(File::delete)
        File(context.noBackupFilesDir, "root-pipes").listFiles()?.forEach(File::delete)
    }

    private fun quote(value: String): String = "'${value.replace("'", "'\\''")}'"
}
