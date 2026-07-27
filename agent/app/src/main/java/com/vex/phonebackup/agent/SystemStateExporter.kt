package com.vex.phonebackup.agent

import android.app.role.RoleManager
import android.content.Context
import android.content.Intent
import android.net.Uri
import android.provider.Settings
import com.topjohnwu.superuser.Shell
import org.json.JSONArray
import org.json.JSONObject

object SystemStateExporter {
    private val systemKeys = setOf(
        Settings.System.ACCELEROMETER_ROTATION,
        Settings.System.HAPTIC_FEEDBACK_ENABLED,
        Settings.System.SOUND_EFFECTS_ENABLED,
        Settings.System.TIME_12_24
    )
    private val secureKeys = setOf(
        Settings.Secure.DEFAULT_INPUT_METHOD,
        Settings.Secure.ENABLED_INPUT_METHODS,
        Settings.Secure.ENABLED_ACCESSIBILITY_SERVICES,
        Settings.Secure.ACCESSIBILITY_ENABLED
    )
    private val globalKeys = setOf(
        Settings.Global.ANIMATOR_DURATION_SCALE,
        Settings.Global.TRANSITION_ANIMATION_SCALE,
        Settings.Global.WINDOW_ANIMATION_SCALE
    )
    private val roles = listOf(
        RoleManager.ROLE_BROWSER,
        RoleManager.ROLE_DIALER,
        RoleManager.ROLE_SMS,
        RoleManager.ROLE_HOME,
        RoleManager.ROLE_ASSISTANT
    )
    private val packagePattern = Regex("^[A-Za-z0-9_]+(?:\\.[A-Za-z0-9_]+)+$")

    fun export(context: Context): JSONObject {
        val resolver = context.contentResolver
        val roleManager = context.getSystemService(RoleManager::class.java)
        return JSONObject()
            .put("system", readSettings(systemKeys) { Settings.System.getString(resolver, it) })
            .put("secure", readSettings(secureKeys) { Settings.Secure.getString(resolver, it) })
            .put("global", readSettings(globalKeys) { Settings.Global.getString(resolver, it) })
            .put("roles", JSONObject().apply {
                roles.forEach { role ->
                    if (runCatching { roleManager.isRoleAvailable(role) }.getOrDefault(false)) {
                        val holder = runCatching {
                            defaultPackageForRole(context, role)
                        }.getOrNull()
                        put(role, JSONArray(
                            if (holder == null) emptyList<String>() else listOf(holder)
                        ))
                    }
                }
            })
    }

    fun restore(context: Context, state: JSONObject): JSONObject {
        val failures = JSONArray()
        restoreSettings("system", systemKeys, state.optJSONObject("system"), failures)
        restoreSettings("secure", secureKeys, state.optJSONObject("secure"), failures)
        restoreSettings("global", globalKeys, state.optJSONObject("global"), failures)
        val roleManager = context.getSystemService(RoleManager::class.java)
        val rolesState = state.optJSONObject("roles") ?: JSONObject()
        roles.filter(roleManager::isRoleAvailable).forEach { role ->
            val holders = rolesState.optJSONArray(role) ?: return@forEach
            val packageName = holders.optString(0)
            if (!packagePattern.matches(packageName) ||
                runCatching { context.packageManager.getPackageInfo(packageName, 0) }.isFailure
            ) {
                failures.put("role:$role")
                return@forEach
            }
            if (!Shell.cmd(
                    "cmd role add-role-holder --user 0 ${quote(role)} ${quote(packageName)}"
                ).exec().isSuccess
            ) failures.put("role:$role")
        }
        return JSONObject().put("ok", true).put("failures", failures)
    }

    private fun readSettings(
        keys: Set<String>,
        reader: (String) -> String?
    ): JSONObject = JSONObject().apply {
        keys.sorted().forEach { key ->
            put(key, runCatching { reader(key) }.getOrNull() ?: JSONObject.NULL)
        }
    }

    private fun defaultPackageForRole(context: Context, role: String): String? {
        val intent = when (role) {
            RoleManager.ROLE_BROWSER ->
                Intent(Intent.ACTION_VIEW, Uri.parse("https://example.invalid/"))
            RoleManager.ROLE_DIALER ->
                Intent(Intent.ACTION_DIAL, Uri.parse("tel:"))
            RoleManager.ROLE_SMS ->
                Intent(Intent.ACTION_SENDTO, Uri.parse("smsto:"))
            RoleManager.ROLE_HOME ->
                Intent(Intent.ACTION_MAIN).addCategory(Intent.CATEGORY_HOME)
            RoleManager.ROLE_ASSISTANT ->
                Intent(Intent.ACTION_ASSIST)
            else -> return null
        }
        return context.packageManager.resolveActivity(
            intent,
            android.content.pm.PackageManager.MATCH_DEFAULT_ONLY
        )?.activityInfo?.packageName
    }

    private fun restoreSettings(
        namespace: String,
        allowlist: Set<String>,
        values: JSONObject?,
        failures: JSONArray
    ) {
        if (values == null) return
        allowlist.forEach { key ->
            if (!values.has(key) || values.isNull(key)) return@forEach
            val value = values.optString(key)
            if (value.length > 16_384 || value.any { it == '\u0000' }) {
                failures.put("$namespace:$key")
                return@forEach
            }
            if (!Shell.cmd(
                    "settings put $namespace ${quote(key)} ${quote(value)}"
                ).exec().isSuccess
            ) failures.put("$namespace:$key")
        }
    }

    private fun quote(value: String): String = "'${value.replace("'", "'\\''")}'"
}
