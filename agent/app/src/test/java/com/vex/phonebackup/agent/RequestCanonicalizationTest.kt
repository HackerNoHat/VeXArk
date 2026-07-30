package com.vex.phonebackup.agent

import org.json.JSONObject
import org.junit.Assert.assertEquals
import org.junit.Test

class RequestCanonicalizationTest {
    @Test
    fun `canonical payload preserves unicode and uses platform independent escaping`() {
        val payload = JSONObject()
            .put("root", "/data/user/0/пример")
            .put("relative", "a/b\\c\"d\n\u0001😀")

        assertEquals(
            "{\"relative\":\"a/b\\\\c\\\"d\\n\\u0001😀\",\"root\":\"/data/user/0/пример\"}",
            canonicalJsonForRequest(payload)
        )
    }
}
