package com.vex.phonebackup.agent

import android.content.Context
import android.content.pm.ApplicationInfo
import android.content.pm.PackageInfo
import android.content.pm.PackageManager
import android.os.Build
import android.os.PowerManager
import android.os.Environment
import android.os.StatFs
import org.json.JSONArray
import org.json.JSONObject
import java.io.File
import java.io.FileInputStream
import java.security.MessageDigest

object AgentInventory {
    fun device(context: Context): JSONObject {
        val stat = StatFs(Environment.getDataDirectory().absolutePath)
        val root = RootCapabilities.probe(false)
        return JSONObject()
            .put("stableId", getSerialSafely())
            .put("model", Build.MODEL)
            .put("device", Build.DEVICE)
            .put("androidVersion", Build.VERSION.RELEASE)
            .put("sdk", Build.VERSION.SDK_INT)
            .put("fingerprint", Build.FINGERPRINT)
            .put("abi", Build.SUPPORTED_ABIS.firstOrNull().orEmpty())
            .put("selinux", readSelinux())
            .put("rootAvailable", root.available)
            .put("rootGranted", root.granted)
            .put("rootProvider", root.provider)
            .put("dataTotalBytes", stat.totalBytes)
            .put("dataAvailableBytes", stat.availableBytes)
            .put("agentPackage", context.packageName)
    }

    fun packages(
        context: Context,
        includeSystemApps: Boolean,
        requestedPackages: Set<String>? = null
    ): JSONArray {
        val manager = context.packageManager
        val flags = PackageManager.PackageInfoFlags.of(
            (PackageManager.GET_PERMISSIONS or PackageManager.GET_SIGNING_CERTIFICATES).toLong()
        )
        val packages = if (Build.VERSION.SDK_INT >= 33) {
            manager.getInstalledPackages(flags)
        } else {
            @Suppress("DEPRECATION")
            manager.getInstalledPackages(PackageManager.GET_PERMISSIONS or PackageManager.GET_SIGNING_CERTIFICATES)
        }
        return JSONArray().apply {
            packages.asSequence()
                .filter { requestedPackages == null || it.packageName in requestedPackages }
                .filter {
                    includeSystemApps ||
                        it.applicationInfo?.flags?.and(ApplicationInfo.FLAG_SYSTEM) == 0
                }
                .sortedBy { it.packageName }
                .forEach { put(packageJson(context, manager, it)) }
        }
    }

    private fun packageJson(
        context: Context,
        manager: PackageManager,
        info: PackageInfo
    ): JSONObject {
        val app = info.applicationInfo
        val sourceDir = app?.sourceDir.orEmpty()
        val splits = app?.splitSourceDirs?.toList().orEmpty()
        val signatures = if (Build.VERSION.SDK_INT >= 28) {
            info.signingInfo?.apkContentsSigners?.toList().orEmpty()
        } else {
            @Suppress("DEPRECATION")
            info.signatures?.toList().orEmpty()
        }
        val digest = MessageDigest.getInstance("SHA-256")
        val certificate = signatures.firstOrNull()?.toByteArray()?.let(digest::digest)
            ?.joinToString("") { "%02x".format(it) }.orEmpty()
        val apkArtifacts = JSONArray().apply {
            (listOf(sourceDir) + splits).filter(String::isNotBlank).forEach { put(apkJson(it)) }
        }
        val dataPaths = JSONArray().apply {
            app?.dataDir?.let(::put)
            app?.deviceProtectedDataDir?.let(::put)
        }
        val runtimePermissions = JSONArray().apply {
            info.requestedPermissions.orEmpty().sorted().forEach { permission ->
                put(JSONObject()
                    .put("name", permission)
                    .put(
                        "granted",
                        manager.checkPermission(permission, info.packageName) ==
                            PackageManager.PERMISSION_GRANTED
                    )
                    .put("flags", 0))
            }
        }
        val batteryExempt = (context.getSystemService(Context.POWER_SERVICE) as PowerManager)
            .isIgnoringBatteryOptimizations(info.packageName)
        return JSONObject()
            .put("packageName", info.packageName)
            .put("label", app?.loadLabel(manager)?.toString().orEmpty())
            .put("versionCode", if (Build.VERSION.SDK_INT >= 28) info.longVersionCode else {
                @Suppress("DEPRECATION") info.versionCode.toLong()
            })
            .put("versionName", info.versionName.orEmpty())
            .put("signingCertificateSha256", certificate)
            .put("installer", runCatching {
                if (Build.VERSION.SDK_INT >= 30) manager.getInstallSourceInfo(info.packageName).installingPackageName
                else @Suppress("DEPRECATION") manager.getInstallerPackageName(info.packageName)
            }.getOrNull())
            .put("uid", app?.uid ?: -1)
            .put("isSystem", app?.flags?.and(ApplicationInfo.FLAG_SYSTEM) != 0)
            .put("isEnabled", app?.enabled == true)
            .put("apkArtifacts", apkArtifacts)
            .put("dataPaths", dataPaths)
            .put("runtimePermissions", runtimePermissions)
            .put("batteryOptimizationExempt", batteryExempt)
    }

    private fun apkJson(path: String): JSONObject {
        val file = File(path)
        val digest = MessageDigest.getInstance("SHA-256")
        runCatching {
            FileInputStream(file).use { input ->
                val buffer = ByteArray(256 * 1024)
                while (true) {
                    val count = input.read(buffer)
                    if (count < 0) break
                    digest.update(buffer, 0, count)
                }
            }
        }
        return JSONObject()
            .put("path", path)
            .put("fileName", file.name)
            .put("size", file.length())
            .put("modifiedUnixNanos", file.lastModified() * 1_000_000L)
            .put("sha256", digest.digest().joinToString("") { "%02x".format(it) })
    }

    private fun readSelinux(): String = runCatching {
        ProcessBuilder("getenforce").start().inputStream.bufferedReader().readText().trim()
    }.getOrDefault("unknown")

    private fun String?.orEmpty() = this ?: ""
    private fun getSerialSafely(): String =
        runCatching { if (Build.VERSION.SDK_INT >= 26) Build.getSerial() else Build.SERIAL }
            .getOrDefault(Build.DEVICE)
}
