package dev.todotracker.core

import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.JsonPrimitive

/**
 * The search box (TaskQuery in C#, same syntax): words match the title or details; `#tag` (nested tags included),
 * `label:name`, `group:name`, `is:open`/`is:done`, `is:archived` (archived tasks are found only then) and the board
 * column (`is:inbox`, `is:next`, `is:doing`) narrow it down. Tags and labels are inherited by subtasks. Quote
 * multi-word values: `label:"deep work"`. All terms must match, in any case.
 */
class TaskSearch private constructor(
    private val words: List<String>,
    private val tags: List<String>,
    private val labels: List<String>,
    private val groups: List<String>,
    private val done: Boolean?,
    private val archived: Boolean,
    private val stage: String?,
) {
    val isEmpty: Boolean get() = words.isEmpty() && tags.isEmpty() && labels.isEmpty() && groups.isEmpty() && done == null && !archived && stage == null

    /** Matching tasks in tree order (parents before their subtasks). */
    fun apply(board: TaskBoard): List<WorkItem> = board.allItems.filter { matches(it, board) }

    fun matches(item: WorkItem, board: TaskBoard): Boolean {
        if (done != null && item.isDone != done) return false
        if (item.isArchived != archived) return false
        if (stage != null && (stageOf(item.root) != stage || item.root.isDone)) return false
        val lineage = listOf(item) + item.ancestors
        return words.all { item.title.contains(it, ignoreCase = true) || item.details?.contains(it, ignoreCase = true) == true } &&
            tags.all { t -> lineage.any { i -> strings(i, "tags").any { it.equals(t, ignoreCase = true) || it.startsWith("$t/", ignoreCase = true) } } } &&
            labels.all { l -> lineage.any { i -> strings(i, "labels").any { it.equals(l, ignoreCase = true) } } } &&
            groups.all { g -> board.groups.any { it.id == item.groupId && (it.name.equals(g, ignoreCase = true) || it.id.toString() == g) } }
    }

    companion object {
        private val STAGES = setOf("inbox", "next", "doing")

        fun parse(text: String?): TaskSearch {
            val words = mutableListOf<String>()
            val tags = mutableListOf<String>()
            val labels = mutableListOf<String>()
            val groups = mutableListOf<String>()
            var done: Boolean? = null
            var archived = false
            var stage: String? = null
            for (token in tokenize(text ?: "")) {
                val state = value(token, "is:")?.lowercase()
                when {
                    token.startsWith("#") && token.length > 1 -> tags.add(token.substring(1))
                    value(token, "label:") != null -> labels.add(value(token, "label:")!!)
                    value(token, "group:") != null -> groups.add(value(token, "group:")!!)
                    state == "open" || state == "done" -> done = state == "done"
                    state == "archived" -> archived = true
                    (state ?: value(token, "stage:")?.lowercase()) in STAGES -> stage = state ?: value(token, "stage:")!!.lowercase()
                    else -> words.add(token)
                }
            }
            return TaskSearch(words, tags, labels, groups, done, archived, stage)
        }

        /** The board column other apps keep (a task that doesn't say is in Next). */
        private fun stageOf(item: WorkItem): String = (item.extra?.get("stage") as? JsonPrimitive)?.content?.lowercase() ?: "next"

        /** Tags or labels other apps keep on a task (this one keeps them as they were). */
        private fun strings(item: WorkItem, name: String): List<String> =
            (item.extra?.get(name) as? JsonArray)?.mapNotNull { (it as? JsonPrimitive)?.takeIf { p -> p.isString }?.content } ?: emptyList()

        private fun value(token: String, prefix: String): String? =
            if (token.length > prefix.length && token.startsWith(prefix, ignoreCase = true)) token.substring(prefix.length) else null

        /** Splits on spaces; double quotes (straight or curly) group words and are dropped. */
        private fun tokenize(text: String): List<String> {
            val result = mutableListOf<String>()
            val current = StringBuilder()
            var quoted = false
            for (c in text) {
                when {
                    c == '"' || c == '“' || c == '”' -> quoted = !quoted
                    c.isWhitespace() && !quoted -> if (current.isNotEmpty()) {
                        result.add(current.toString())
                        current.clear()
                    }
                    else -> current.append(c)
                }
            }
            if (current.isNotEmpty()) result.add(current.toString())
            return result
        }
    }
}
