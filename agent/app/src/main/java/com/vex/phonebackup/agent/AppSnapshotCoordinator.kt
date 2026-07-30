package com.vex.phonebackup.agent

import android.content.Context
import android.content.pm.ApplicationInfo
import com.topjohnwu.superuser.Shell
import org.json.JSONArray
import org.json.JSONObject
import java.util.concurrent.ConcurrentHashMap

object AppSnapshotCoordinator {
    private val packagePattern = Regex("^[A-Za-z0-9_]+(?:\\.[A-Za-z0-9_]+)+$")
    private val permissionPattern = Regex("^[A-Za-z0-9_.]+$")
    private val originalStoppedState = ConcurrentHashMap<String, Boolean>()

    fun begin(context: Context, packageName: String): Boolean {
        if (!packagePattern.matches(packageName)) return false
        val application = runCatching {
            context.packageManager.getApplicationInfo(packageName, 0)
        }.getOrNull() ?: return false
        val wasStopped = application.flags and ApplicationInfo.FLAG_STOPPED != 0
        val result = Shell.cmd("am force-stop --user 0 $packageName").exec()
        if (result.isSuccess) originalStoppedState[packageName] = wasStopped
        return result.isSuccess
    }

    fun end(packageName: String): Boolean {
        if (!packagePattern.matches(packageName)) return false
        val wasStopped = originalStoppedState.remove(packageName) ?: return false
        if (wasStopped) return true

        val commands = listOf(
            "cmd package unstop --user 0 $packageName",
            "cmd package set-stopped-state --user 0 false $packageName"
        )
        for ((index, command) in commands.withIndex()) {
            val result = Shell.cmd(command).exec()
            if (result.isSuccess) {
                AgentDiagnostics.log(
                    level = "info",
                    component = "snapshot",
                    eventCode = "package_unstopped",
                    message = "Package stopped-state restored",
                    result = "ok",
                    fields = mapOf(
                        "packageName" to packageName,
                        "strategy" to if (index == 0) "package_unstop" else "legacy_set_stopped_state",
                        "exitCode" to result.code
                    )
                )
                return true
            }
            AgentDiagnostics.log(
                level = "warn",
                component = "snapshot",
                eventCode = "package_unstop_failed",
                message = "Package stopped-state restore command failed",
                result = "failed",
                fields = mapOf(
                    "packageName" to packageName,
                    "strategy" to if (index == 0) "package_unstop" else "legacy_set_stopped_state",
                    "exitCode" to result.code,
                    "stdout" to result.out.joinToString("\n").trim(),
                    "stderr" to result.err.joinToString("\n").trim()
                )
            )
        }
        return false
    }

    fun prepareRestore(packageName: String): Boolean {
        if (!packagePattern.matches(packageName)) return false
        val stopped = Shell.cmd("am force-stop --user 0 $packageName").exec()
        if (!stopped.isSuccess) return false
        return Shell.cmd("pm clear --user 0 $packageName").exec().isSuccess
    }

    fun finishRestore(packageName: String): Boolean {
        if (!packagePattern.matches(packageName)) return false
        return Shell.cmd("cmd package set-stopped-state --user 0 false $packageName").exec().isSuccess
    }

    fun applyPolicy(
        packageName: String,
        enabled: Boolean,
        batteryExempt: Boolean,
        permissions: JSONArray
    ): JSONObject {
        if (!packagePattern.matches(packageName))
            return JSONObject().put("ok", false).put("error", "invalid_package")
        val failures = JSONArray()
        for (index in 0 until permissions.length()) {
            val item = permissions.optJSONObject(index) ?: continue
            val name = item.optString("name")
            if (!permissionPattern.matches(name)) {
                failures.put(name)
                continue
            }
            val action = if (item.optBoolean("granted")) "grant" else "revoke"
            if (!Shell.cmd("pm $action --user 0 $packageName $name").exec().isSuccess)
                failures.put(name)
        }
        val enabledAction = if (enabled) "enable" else "disable-user"
        if (!Shell.cmd("pm $enabledAction --user 0 $packageName").exec().isSuccess)
            failures.put("enabled-state")
        val whitelistAction = if (batteryExempt) "+$packageName" else "-$packageName"
        if (!Shell.cmd("dumpsys deviceidle whitelist $whitelistAction").exec().isSuccess)
            failures.put("battery-whitelist")
        return JSONObject()
            .put("ok", true)
            .put("failures", failures)
    }
}
