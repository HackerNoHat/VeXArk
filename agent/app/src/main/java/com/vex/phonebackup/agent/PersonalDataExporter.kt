package com.vex.phonebackup.agent

import android.Manifest
import android.content.Context
import android.content.pm.PackageManager
import android.provider.CallLog
import android.provider.ContactsContract
import android.provider.Telephony
import android.provider.BaseColumns
import androidx.core.content.ContextCompat
import org.json.JSONObject
import java.io.ByteArrayOutputStream

object PersonalDataExporter {
    fun status(context: Context): JSONObject = JSONObject()
        .put("contacts", granted(context, Manifest.permission.READ_CONTACTS))
        .put("messages", granted(context, Manifest.permission.READ_SMS))
        .put("calls", granted(context, Manifest.permission.READ_CALL_LOG))

    fun export(context: Context, kind: String, emit: (ByteArray) -> Unit): Boolean =
        when (kind) {
            "contacts" -> exportContacts(context, emit)
            "messages" -> exportMessages(context, emit)
            "calls" -> exportCalls(context, emit)
            else -> false
        }

    private fun exportContacts(context: Context, emit: (ByteArray) -> Unit): Boolean = runCatching {
        require(granted(context, Manifest.permission.READ_CONTACTS))
        context.contentResolver.query(
            ContactsContract.Contacts.CONTENT_URI,
            arrayOf(ContactsContract.Contacts.LOOKUP_KEY),
            null,
            null,
            ContactsContract.Contacts._ID
        )?.use { cursor ->
            val lookup = cursor.getColumnIndexOrThrow(ContactsContract.Contacts.LOOKUP_KEY)
            while (cursor.moveToNext()) {
                val uri = android.net.Uri.withAppendedPath(
                    ContactsContract.Contacts.CONTENT_VCARD_URI,
                    cursor.getString(lookup)
                )
                context.contentResolver.openAssetFileDescriptor(uri, "r")?.use { descriptor ->
                    descriptor.createInputStream().use { input ->
                        val output = ByteArrayOutputStream()
                        input.copyTo(output, 64 * 1024)
                        emit(output.toByteArray())
                        emit("\r\n".toByteArray())
                    }
                }
            }
        }
    }.isSuccess

    private fun exportMessages(context: Context, emit: (ByteArray) -> Unit): Boolean = runCatching {
        require(granted(context, Manifest.permission.READ_SMS))
        val projection = arrayOf(
            BaseColumns._ID,
            Telephony.TextBasedSmsColumns.THREAD_ID,
            Telephony.TextBasedSmsColumns.ADDRESS,
            Telephony.TextBasedSmsColumns.BODY,
            Telephony.TextBasedSmsColumns.DATE,
            Telephony.TextBasedSmsColumns.TYPE,
            Telephony.TextBasedSmsColumns.READ
        )
        context.contentResolver.query(
            Telephony.Sms.CONTENT_URI,
            projection,
            null,
            null,
            "${Telephony.TextBasedSmsColumns.DATE} ASC"
        )?.use { cursor ->
            while (cursor.moveToNext()) {
                val item = JSONObject().put("recordType", "sms")
                projection.forEachIndexed { index, name ->
                    item.put(name, if (cursor.isNull(index)) JSONObject.NULL else cursor.getString(index))
                }
                emit((item.toString() + "\n").toByteArray())
            }
        }
        exportMms(context, emit)
    }.isSuccess

    private fun exportMms(context: Context, emit: (ByteArray) -> Unit) {
        val projection = arrayOf(
            Telephony.Mms._ID,
            Telephony.Mms.THREAD_ID,
            Telephony.Mms.DATE,
            Telephony.Mms.MESSAGE_BOX,
            Telephony.Mms.READ,
            Telephony.Mms.SUBJECT,
            Telephony.Mms.CONTENT_TYPE
        )
        context.contentResolver.query(
            Telephony.Mms.CONTENT_URI,
            projection,
            null,
            null,
            "${Telephony.Mms.DATE} ASC"
        )?.use { cursor ->
            while (cursor.moveToNext()) {
                val id = cursor.getString(0)
                val item = JSONObject().put("recordType", "mms")
                projection.forEachIndexed { index, name ->
                    item.put(name, if (cursor.isNull(index)) JSONObject.NULL else cursor.getString(index))
                }
                item.put("addresses", mmsAddresses(context, id))
                item.put("parts", mmsTextParts(context, id))
                emit((item.toString() + "\n").toByteArray())
            }
        }
    }

    private fun mmsAddresses(context: Context, messageId: String): org.json.JSONArray {
        val result = org.json.JSONArray()
        val projection = arrayOf("address", "type", "charset")
        context.contentResolver.query(
            android.net.Uri.parse("content://mms/$messageId/addr"),
            projection,
            null,
            null,
            null
        )?.use { cursor ->
            while (cursor.moveToNext()) {
                result.put(JSONObject()
                    .put("address", cursor.getString(0))
                    .put("type", cursor.getString(1))
                    .put("charset", cursor.getString(2)))
            }
        }
        return result
    }

    private fun mmsTextParts(context: Context, messageId: String): org.json.JSONArray {
        val result = org.json.JSONArray()
        val projection = arrayOf(
            Telephony.Mms.Part._ID,
            Telephony.Mms.Part.CONTENT_TYPE,
            Telephony.Mms.Part.NAME,
            Telephony.Mms.Part.FILENAME,
            Telephony.Mms.Part.TEXT
        )
        context.contentResolver.query(
            android.net.Uri.parse("content://mms/$messageId/part"),
            projection,
            null,
            null,
            null
        )?.use { cursor ->
            while (cursor.moveToNext()) {
                val contentType = cursor.getString(1).orEmpty()
                val part = JSONObject()
                    .put("id", cursor.getString(0))
                    .put("contentType", contentType)
                    .put("name", cursor.getString(2))
                    .put("filename", cursor.getString(3))
                if (contentType == "text/plain" || contentType == "application/smil")
                    part.put("text", cursor.getString(4))
                else
                    part.put("binaryOmitted", true)
                result.put(part)
            }
        }
        return result
    }

    private fun exportCalls(context: Context, emit: (ByteArray) -> Unit): Boolean = runCatching {
        require(granted(context, Manifest.permission.READ_CALL_LOG))
        val projection = arrayOf(
            CallLog.Calls._ID,
            CallLog.Calls.NUMBER,
            CallLog.Calls.CACHED_NAME,
            CallLog.Calls.DATE,
            CallLog.Calls.DURATION,
            CallLog.Calls.TYPE,
            CallLog.Calls.NEW
        )
        context.contentResolver.query(
            CallLog.Calls.CONTENT_URI,
            projection,
            null,
            null,
            "${CallLog.Calls.DATE} ASC"
        )?.use { cursor ->
            while (cursor.moveToNext()) {
                val item = JSONObject()
                projection.forEachIndexed { index, name ->
                    item.put(name, if (cursor.isNull(index)) JSONObject.NULL else cursor.getString(index))
                }
                emit((item.toString() + "\n").toByteArray())
            }
        }
    }.isSuccess

    private fun granted(context: Context, permission: String): Boolean =
        ContextCompat.checkSelfPermission(context, permission) == PackageManager.PERMISSION_GRANTED
}
