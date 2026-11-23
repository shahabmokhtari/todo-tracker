package dev.onething.core

import java.util.UUID

/** A place a task can go in its details' Move…: on its own (a top-level task of the group), or under a task. */
data class MoveTarget(val parentId: UUID?, val title: String, val depth: Int)

/**
 * Move… in a task's details (the web app's Move dialog, app.js moveDialog; MoveTargets.swift): pick a group, then on
 * its own or under any open task of that group, never itself or one of its own subtasks.
 */
object MoveTargets {
    /** Where [item] can go in [groupId], in tree order: "On its own" first. [query] narrows the tasks (in titles). */
    fun options(board: TaskBoard, item: WorkItem, groupId: UUID, query: String = ""): List<MoveTarget> {
        val inside = item.selfAndDescendants.map { it.id }.toSet()
        val q = query.trim().lowercase()
        val result = mutableListOf(MoveTarget(null, "On its own", 0))
        fun walk(nodes: List<WorkItem>, depth: Int) {
            for (node in nodes) {
                if (node.id in inside) continue
                if (q.isEmpty() || node.title.lowercase().contains(q)) result.add(MoveTarget(node.id, node.title, if (q.isEmpty()) depth else 0))
                walk(node.children.filter { !it.isDone }, depth + 1)
            }
        }
        walk(board.items.filter { it.groupId == groupId && !it.isDone && it.archivedAt == null }, 0)
        return result
    }

    /** The move picking [parentId] in [groupId] makes (null: it's already there, nothing to do). */
    fun move(item: WorkItem, groupId: UUID, parentId: UUID?): BoardMove? {
        if (parentId != null) return if (parentId == item.parent?.id) null else BoardMove.Move(item.id, parentId)
        val sameGroup = groupId == item.groupId
        return if (item.parent != null || !sameGroup) BoardMove.Move(item.id, null, groupId) else null
    }
}
