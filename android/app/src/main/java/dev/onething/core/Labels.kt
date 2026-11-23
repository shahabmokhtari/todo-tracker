package dev.onething.core

import java.time.Instant
import java.util.UUID

// Tags and labels (TaskBoard.Organization.cs): the same rules as OneThing.Core and the Apple core, checked by
// tests/fixtures/labels.json.

const val MAX_LABEL_LENGTH = 40

private val COLOR = Regex("^#[0-9a-fA-F]{6}$")

/** Names as given: trimmed, each once (ignoring case); 1 to 40 characters without commas, brackets or quotes. */
internal fun validateLabelNames(names: List<String>): List<String> {
    val result = mutableListOf<String>()
    for (raw in names) {
        val name = raw.trim()
        val bad = name.isEmpty() || name.length > MAX_LABEL_LENGTH || name.any { it.isISOControl() || it in ",[]\"" }
        if (bad) throw BoardException("\"$raw\" isn't a valid label. Use up to $MAX_LABEL_LENGTH characters without commas, brackets, or quotes.")
        if (result.none { it.equals(name, ignoreCase = true) }) result.add(name)
    }
    if (result.size > TaskBoard.MAX_TAGS) throw BoardException("At most ${TaskBoard.MAX_TAGS} labels per task.")
    return result
}

private fun validateColor(color: String) {
    if (!COLOR.matches(color)) throw BoardException("Colors must be #rrggbb.")
}

/** A new label ([color] null: the next one nobody uses). */
fun TaskBoard.defineLabel(name: String, color: String?, actor: Actor = Actor.USER, now: Instant): LabelDefinition {
    val clean = validateLabelNames(listOf(name)).single()
    color?.let(::validateColor)
    if (findLabel(clean) != null) throw BoardException("A label named \"$clean\" already exists.")
    val label = if (color == null) ensureLabel(clean) else LabelDefinition(clean, color.lowercase()).also { labels.add(it) }
    log(now, TaskBoard.BOARD_SCOPE_ID, "labelChanged", "Added label \"$clean\"", actor)
    return label
}

/** Renames or recolors a label: every task with it gets the new name. */
fun TaskBoard.updateLabel(name: String, newName: String, color: String, actor: Actor = Actor.USER, now: Instant) {
    val label = findLabel(name) ?: throw BoardException("Label \"$name\" was not found.")
    val clean = validateLabelNames(listOf(newName)).single()
    validateColor(color)
    val other = findLabel(clean)
    if (other != null && other !== label) throw BoardException("A label named \"$clean\" already exists.")
    val old = label.name
    for (item in allItems) {
        val i = item.labels.indexOfFirst { it.equals(old, ignoreCase = true) }
        if (i >= 0) item.labels[i] = clean
    }
    label.name = clean
    label.color = color.lowercase()
    log(now, TaskBoard.BOARD_SCOPE_ID, "labelChanged", if (old == clean) "Updated label \"$clean\"" else "Renamed label \"$old\" to \"$clean\"", actor)
}

/** Removes a label: it comes off every task (the tasks stay as they are otherwise). */
fun TaskBoard.deleteLabel(name: String, actor: Actor = Actor.USER, now: Instant) {
    val label = findLabel(name) ?: throw BoardException("Label \"$name\" was not found.")
    for (item in allItems) item.labels.removeAll { it.equals(label.name, ignoreCase = true) }
    labels.remove(label)
    log(now, TaskBoard.BOARD_SCOPE_ID, "labelChanged", "Deleted label \"${label.name}\"", actor)
}

/** A task's labels, as picked: each one that isn't on the board yet is made (with the next color). */
fun TaskBoard.setLabels(id: UUID, names: List<String>, actor: Actor = Actor.USER, now: Instant) {
    val item = get(id)
    val resolved = validateLabelNames(names).map { ensureLabel(it).name }
    item.labels.clear()
    item.labels.addAll(resolved)
    log(now, id, "updated", if (resolved.isEmpty()) "Cleared labels on \"${item.title}\"" else "Labels on \"${item.title}\": ${resolved.joinToString(", ")}", actor)
}

/** A task's tags (cleaned, each once). */
fun TaskBoard.setTags(id: UUID, tags: List<String>, actor: Actor = Actor.USER, now: Instant) {
    val item = get(id)
    val clean = TaskBoard.normalizeTags(tags)
    item.tags.clear()
    item.tags.addAll(clean)
    log(now, id, "updated", if (clean.isEmpty()) "Cleared tags on \"${item.title}\"" else "Tags on \"${item.title}\": ${clean.joinToString(" ") { "#$it" }}", actor)
}

/** The longest icon (in UTF-16 units, as .NET counts): one emoji, even a family or a flag, or a couple of letters. */
const val MAX_ICON_LENGTH = 16

/** An emoji shown before the title (null or blank: none). */
fun TaskBoard.setIcon(id: UUID, icon: String?, actor: Actor = Actor.USER, now: Instant) {
    val item = get(id)
    val clean = icon?.trim().orEmpty()
    if (clean.isNotEmpty() && (clean.length > MAX_ICON_LENGTH || clean.any { it.isISOControl() || it == '\u2028' || it == '\u2029' })) {
        throw BoardException("An icon is an emoji or a few characters (at most $MAX_ICON_LENGTH).")
    }
    item.icon = clean.ifEmpty { null }
    log(now, id, "updated", "Updated \"${item.title}\"", actor)
}

/** The task's own color, #rrggbb (null or blank: none, it shows its first label's). */
fun TaskBoard.setColor(id: UUID, color: String?, actor: Actor = Actor.USER, now: Instant) {
    val item = get(id)
    val clean = color?.trim().orEmpty()
    if (clean.isNotEmpty()) validateColor(clean)
    item.color = clean.ifEmpty { null }?.lowercase()
    log(now, id, "updated", "Updated \"${item.title}\"", actor)
}
