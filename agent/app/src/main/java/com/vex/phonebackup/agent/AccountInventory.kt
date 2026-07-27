package com.vex.phonebackup.agent

import android.accounts.Account
import android.accounts.AccountManager
import android.content.ContentResolver
import android.content.Context
import android.content.SyncAdapterType
import org.json.JSONArray
import org.json.JSONObject

object AccountInventory {
    fun export(context: Context): JSONObject {
        val adapters: Map<String, List<SyncAdapterType>> = runCatching {
            ContentResolver.getSyncAdapterTypes().groupBy { it.accountType }
        }.getOrElse { emptyMap() }
        val accounts: List<Account> = runCatching {
            AccountManager.get(context).accounts.sortedWith(
                compareBy<Account>({ it.type }, { it.name })
            )
        }.getOrElse { emptyList() }
        return JSONObject()
            .put("credentialsIncluded", false)
            .put("note", "Passwords, OAuth tokens and AccountManager secrets are never available.")
            .put("accounts", JSONArray().apply {
                accounts.forEach { account ->
                    put(JSONObject()
                        .put("name", account.name)
                        .put("type", account.type)
                        .put("isGoogle", account.type == "com.google")
                        .put(
                            "authorities",
                            JSONArray(
                                adapters[account.type]
                                    .orEmpty()
                                    .map { it.authority }
                                    .distinct()
                                    .sorted()
                            )
                        ))
                }
            })
    }
}
