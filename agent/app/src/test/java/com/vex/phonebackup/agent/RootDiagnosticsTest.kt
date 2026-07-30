package com.vex.phonebackup.agent

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test
import java.nio.file.Files

class RootDiagnosticsTest {
    @Test
    fun `root trace serializes granted shell details`() {
        val diagnostics = RootDiagnostics(
            attemptId = "attempt-1",
            timestampUtc = "2026-07-30T12:00:00Z",
            requestGrant = true,
            appUid = 10341,
            appGrantState = "granted",
            cachedShellPresent = true,
            cachedShellAlive = true,
            cachedShellRoot = false,
            cachedShellClosed = true,
            shellExitCode = 0,
            shellSuccess = true,
            stdout = "uid=0(root)",
            stderr = "",
            selinux = "Enforcing",
            exceptionType = null,
            exceptionMessage = null,
            durationMs = 42,
            steps = listOf("cached_shell", "root_shell")
        )
        val probe = RootProbe(
            available = true,
            granted = true,
            provider = "KernelSU",
            detail = "uid=0(root)",
            diagnostics = diagnostics
        )

        assertTrue(probe.granted)
        assertEquals("KernelSU", probe.provider)
        assertEquals(0, probe.diagnostics?.shellExitCode)
        assertEquals("uid=0(root)", probe.diagnostics?.stdout)
    }

    @Test
    fun `root trace keeps denied reason and stderr`() {
        val diagnostics = RootDiagnostics(
            attemptId = "attempt-2",
            timestampUtc = "2026-07-30T12:00:00Z",
            requestGrant = true,
            appUid = 10342,
            appGrantState = "denied",
            cachedShellPresent = true,
            cachedShellAlive = true,
            cachedShellRoot = false,
            cachedShellClosed = true,
            shellExitCode = 1,
            shellSuccess = false,
            stdout = "",
            stderr = "permission denied",
            selinux = "Enforcing",
            exceptionType = null,
            exceptionMessage = null,
            durationMs = 15,
            steps = listOf("result:denied")
        )
        val probe = RootProbe(
            available = true,
            granted = false,
            provider = "KernelSU/Magisk/APatch",
            detail = "permission denied",
            diagnostics = diagnostics
        )

        assertTrue(!probe.granted)
        assertEquals("permission denied", probe.detail)
        assertEquals("permission denied", probe.diagnostics?.stderr)
    }

    @Test
    fun `root outcomes distinguish granted denied exception and timeout`() {
        val granted = classifyRootProbe(
            true,
            "uid=0(root) gid=0(root) context=u:r:su:s0",
            "",
            "granted",
            true,
            null,
            null
        )
        val denied = classifyRootProbe(
            false,
            "",
            "permission denied",
            "denied",
            true,
            null,
            null
        )
        val exception = classifyRootProbe(
            false,
            "",
            "",
            "undetermined",
            true,
            "java.lang.IllegalStateException",
            "shell failed"
        )
        val timeout = classifyRootProbe(
            false,
            "",
            "",
            "undetermined",
            true,
            "java.util.concurrent.TimeoutException",
            "90 seconds"
        )

        assertTrue(granted.granted)
        assertEquals("permission denied", denied.detail)
        assertEquals("IllegalStateException: shell failed", exception.detail)
        assertEquals("Root request timed out: 90 seconds", timeout.detail)
    }

    @Test
    fun `root trace serializes protocol fields`() {
        val trace = RootDiagnostics(
            attemptId = "attempt-json",
            timestampUtc = "2026-07-30T12:00:00Z",
            requestGrant = true,
            appUid = 10341,
            appGrantState = "denied",
            cachedShellPresent = true,
            cachedShellAlive = true,
            cachedShellRoot = false,
            cachedShellClosed = true,
            shellExitCode = 1,
            shellSuccess = false,
            stdout = "",
            stderr = "permission denied",
            selinux = "Enforcing",
            exceptionType = null,
            exceptionMessage = null,
            durationMs = 15,
            steps = listOf("result:denied")
        )

        val json = trace.toJson()

        assertEquals("attempt-json", json.getString("attemptId"))
        assertFalse(json.getBoolean("shellSuccess"))
        assertEquals("permission denied", json.getString("stderr"))
        assertEquals("result:denied", json.getJSONArray("steps").getString(0))
    }

    @Test
    fun `diagnostic rotation keeps configured file count`() {
        val folder = Files.createTempDirectory("vexark-agent-log-test").toFile()
        try {
            folder.resolve("agent.jsonl").writeText("current")
            folder.resolve("agent.jsonl.1").writeText("one")
            folder.resolve("agent.jsonl.2").writeText("two")

            rotateDiagnosticFiles(folder, 3)

            assertFalse(folder.resolve("agent.jsonl").exists())
            assertEquals("current", folder.resolve("agent.jsonl.1").readText())
            assertEquals("one", folder.resolve("agent.jsonl.2").readText())
            assertFalse(folder.resolve("agent.jsonl.3").exists())
        } finally {
            folder.deleteRecursively()
        }
    }
}
