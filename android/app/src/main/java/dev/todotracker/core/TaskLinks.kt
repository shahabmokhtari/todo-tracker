package dev.todotracker.core

import java.util.UUID

/** Deep links that open the app on a task: `todotracker://task/<id>` (TaskLinks in the C# core, same rules). */
object TaskLinks {
    const val SCHEME = "todotracker"

    fun forTask(id: UUID): String = "$SCHEME://task/$id"

    /** What a link opens. */
    sealed interface Target {
        data object App : Target
        data class Task(val id: UUID) : Target
    }

    /** The task (or just the app, for the bare scheme) a link opens; null when it isn't a link the app follows. */
    fun target(link: String?): Target? {
        val text = link?.trim() ?: return null
        if (!text.startsWith("$SCHEME:", ignoreCase = true)) return null
        var rest = text.substring(SCHEME.length + 1).trimStart('/')
        val end = rest.indexOfAny(charArrayOf('?', '#'))
        if (end >= 0) rest = rest.substring(0, end)
        val parts = rest.trimEnd('/').split('/')
        if (parts == listOf("") || parts == listOf("open")) return Target.App
        if (parts.size != 2 || !parts[0].equals("task", ignoreCase = true)) return null
        return uuid(parts[1])?.let { Target.Task(it) }
    }

    /** With or without dashes, any case (like .NET's Guid parsing of the forms we hand out). */
    private fun uuid(text: String): UUID? {
        val dashed = when {
            Regex("^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$").matches(text) -> text
            Regex("^[0-9a-fA-F]{32}$").matches(text) -> "${text.substring(0, 8)}-${text.substring(8, 12)}-${text.substring(12, 16)}-${text.substring(16, 20)}-${text.substring(20)}"
            else -> return null
        }
        return UUID.fromString(dashed)
    }
}
