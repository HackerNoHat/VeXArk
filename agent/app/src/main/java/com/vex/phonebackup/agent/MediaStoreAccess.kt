package com.vex.phonebackup.agent

import android.Manifest
import android.content.Context
import android.content.pm.PackageManager
import android.net.Uri
import android.os.Build
import android.os.Environment
import android.provider.MediaStore
import androidx.core.content.ContextCompat
import org.json.JSONObject

object MediaStoreAccess {
    fun status(context: Context): JSONObject {
        val allFiles = Build.VERSION.SDK_INT < 30 || Environment.isExternalStorageManager()
        val images = allFiles || granted(
            context,
            if (Build.VERSION.SDK_INT >= 33) {
                Manifest.permission.READ_MEDIA_IMAGES
            } else {
                Manifest.permission.READ_EXTERNAL_STORAGE
            }
        )
        val videos = allFiles || granted(
            context,
            if (Build.VERSION.SDK_INT >= 33) {
                Manifest.permission.READ_MEDIA_VIDEO
            } else {
                Manifest.permission.READ_EXTERNAL_STORAGE
            }
        )
        return JSONObject()
            .put("images", images)
            .put("videos", videos)
            .put(
                "selected",
                Build.VERSION.SDK_INT >= 34 &&
                    granted(context, Manifest.permission.READ_MEDIA_VISUAL_USER_SELECTED) &&
                    !images &&
                    !videos
            )
            .put("allFiles", allFiles)
    }

    fun scan(context: Context, emit: (String) -> Unit): Boolean = runCatching {
        val access = status(context)
        require(access.optBoolean("images") || access.optBoolean("videos")) {
            "photo/video permission is not granted"
        }
        val projection = arrayOf(
            MediaStore.Files.FileColumns._ID,
            MediaStore.Files.FileColumns.DISPLAY_NAME,
            MediaStore.Files.FileColumns.RELATIVE_PATH,
            MediaStore.Files.FileColumns.SIZE,
            MediaStore.Files.FileColumns.DATE_MODIFIED,
            MediaStore.Files.FileColumns.MEDIA_TYPE,
            MediaStore.Files.FileColumns.MIME_TYPE
        )
        val allowedTypes = buildList {
            if (access.optBoolean("images")) add(MediaStore.Files.FileColumns.MEDIA_TYPE_IMAGE)
            if (access.optBoolean("videos")) add(MediaStore.Files.FileColumns.MEDIA_TYPE_VIDEO)
        }
        val placeholders = allowedTypes.joinToString(",") { "?" }
        val collection = MediaStore.Files.getContentUri(MediaStore.VOLUME_EXTERNAL)
        context.contentResolver.query(
            collection,
            projection,
            "${MediaStore.Files.FileColumns.MEDIA_TYPE} IN ($placeholders)",
            allowedTypes.map(Int::toString).toTypedArray(),
            "${MediaStore.Files.FileColumns.DATE_MODIFIED} ASC"
        )?.use { cursor ->
            val idIndex = cursor.getColumnIndexOrThrow(MediaStore.Files.FileColumns._ID)
            val nameIndex = cursor.getColumnIndexOrThrow(MediaStore.Files.FileColumns.DISPLAY_NAME)
            val pathIndex = cursor.getColumnIndexOrThrow(MediaStore.Files.FileColumns.RELATIVE_PATH)
            val sizeIndex = cursor.getColumnIndexOrThrow(MediaStore.Files.FileColumns.SIZE)
            val modifiedIndex = cursor.getColumnIndexOrThrow(MediaStore.Files.FileColumns.DATE_MODIFIED)
            val typeIndex = cursor.getColumnIndexOrThrow(MediaStore.Files.FileColumns.MEDIA_TYPE)
            val mimeIndex = cursor.getColumnIndexOrThrow(MediaStore.Files.FileColumns.MIME_TYPE)
            while (cursor.moveToNext()) {
                val id = cursor.getLong(idIndex)
                val displayName = safeName(cursor.getString(nameIndex), id)
                val relativeDirectory = cursor.getString(pathIndex)
                    .orEmpty()
                    .replace('\\', '/')
                    .trim('/')
                val relative = listOf(relativeDirectory, displayName)
                    .filter(String::isNotBlank)
                    .joinToString("/")
                val uri = Uri.withAppendedPath(collection, id.toString())
                val kind = if (
                    cursor.getInt(typeIndex) == MediaStore.Files.FileColumns.MEDIA_TYPE_VIDEO
                ) "video" else "image"
                emit(JSONObject()
                    .put("relativePath", relative)
                    .put("kind", "file")
                    .put("size", cursor.getLong(sizeIndex).coerceAtLeast(0))
                    .put(
                        "modifiedUnixNanos",
                        cursor.getLong(modifiedIndex).coerceAtLeast(0) * 1_000_000_000L
                    )
                    .put("mode", 0)
                    .put("uid", 0)
                    .put("gid", 0)
                    .put("selinuxLabel", JSONObject.NULL)
                    .put("linkTarget", uri.toString())
                    .put("contentHash", cursor.getString(mimeIndex))
                    .put("mediaKind", kind)
                    .toString())
            }
        }
    }.isSuccess

    fun read(context: Context, value: String, emit: (ByteArray) -> Unit): Boolean = runCatching {
        val uri = Uri.parse(value)
        require(uri.scheme == ContentResolverScheme && uri.authority == MediaAuthority)
        require(uri.pathSegments.firstOrNull() == MediaStore.VOLUME_EXTERNAL)
        val mime = context.contentResolver.getType(uri).orEmpty()
        require(mime.startsWith("image/") || mime.startsWith("video/")) {
            "content URI is not photo/video media"
        }
        context.contentResolver.openInputStream(uri)?.use { input ->
            val buffer = ByteArray(256 * 1024)
            while (true) {
                val count = input.read(buffer)
                if (count < 0) break
                emit(if (count == buffer.size) buffer.copyOf() else buffer.copyOf(count))
            }
        } ?: error("MediaStore item cannot be opened")
    }.isSuccess

    private fun safeName(value: String?, id: Long): String {
        val sanitized = value.orEmpty()
            .replace('/', '_')
            .replace('\\', '_')
            .trim()
        return sanitized.ifBlank { "media-$id" }
    }

    private fun granted(context: Context, permission: String): Boolean =
        ContextCompat.checkSelfPermission(context, permission) == PackageManager.PERMISSION_GRANTED

    private const val ContentResolverScheme = "content"
    private const val MediaAuthority = "media"
}
