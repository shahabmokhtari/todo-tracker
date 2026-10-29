package dev.todotracker.core.sync

data class TextMergeResult(val text: String, val conflicted: Boolean)

/** What to do where both sides changed the same lines differently. */
enum class ConflictPolicy {
    /** Keep both sides' lines (the first side's first): nothing typed is lost. */
    BOTH,

    /** Keep only the first side's lines there (everything else still merges). */
    FIRST,

    /** Keep only the second side's lines there. */
    SECOND,
}

/**
 * Line-based three-way merge (diff3), as TodoTracker.Core.Sync.TextMerge: changes to different lines combine; where
 * both sides changed the same lines differently both versions' lines are kept (the first side's first), so the result
 * is the same on every device as long as callers order the sides the same way.
 */
object TextMerge {
    /** [lineKeys] gives each line its identity (or null), e.g. a step's block id: keyed lines merge one by one. */
    fun merge(base: String, first: String, second: String, lineKeys: ((List<String>) -> List<String?>)? = null, policy: ConflictPolicy = ConflictPolicy.BOTH): TextMergeResult {
        val o = split(base)
        val a = split(first)
        val b = split(second)
        val ko = lineKeys?.invoke(o)
        val ka = lineKeys?.invoke(a)
        val kb = lineKeys?.invoke(b)
        val ma = match(o, a)
        val mb = match(o, b)
        val output = mutableListOf<String>()
        var conflicted = false
        var io = 0
        var ia = 0
        var ib = 0
        while (true) {
            while (io < o.size && ma[io] == ia && mb[io] == ib) {
                output.add(o[io])
                io++
                ia++
                ib++
            }
            if (io >= o.size && ia >= a.size && ib >= b.size) break

            // The next base line both sides kept ends this unstable chunk.
            var next = io
            while (next < o.size && (ma[next] < 0 || mb[next] < 0)) next++
            val ea = if (next < o.size) ma[next] else a.size
            val eb = if (next < o.size) mb[next] else b.size
            val co = o.subList(io, next)
            val ca = a.subList(ia, ea)
            val cb = b.subList(ib, eb)
            val keyed = if (ko != null && co != ca && co != cb && ca != cb) mergeByKey(co, ca, cb, ko.subList(io, next), ka!!.subList(ia, ea), kb!!.subList(ib, eb), policy) else null
            when {
                co == ca -> output.addAll(cb)
                co == cb || ca == cb -> output.addAll(ca)
                keyed != null -> {
                    output.addAll(keyed.first)
                    conflicted = conflicted or keyed.second
                }
                ko != null && co.isEmpty() && distinctKeyed(ka!!.subList(ia, ea), kb!!.subList(ib, eb)) -> {
                    // Both added lines that each carry their own identity (time logged on two devices): both stay.
                    output.addAll(ca)
                    output.addAll(cb)
                }
                else -> {
                    conflicted = true
                    when (policy) {
                        ConflictPolicy.FIRST -> output.addAll(ca)
                        ConflictPolicy.SECOND -> output.addAll(cb)
                        ConflictPolicy.BOTH -> {
                            output.addAll(ca)
                            output.addAll(cb.filter { it !in ca })
                        }
                    }
                }
            }
            io = next
            ia = ea
            ib = eb
        }
        return TextMergeResult(join(output), conflicted)
    }

    /** Keyed lines merge one by one when the clashing stretch has the same keys in the same order on all three sides. */
    private fun mergeByKey(o: List<String>, a: List<String>, b: List<String>, ko: List<String?>, ka: List<String?>, kb: List<String?>, policy: ConflictPolicy): Pair<List<String>, Boolean>? {
        if (o.size != a.size || o.size != b.size || o.isEmpty()) return null
        val lines = mutableListOf<String>()
        var conflicted = false
        for (i in o.indices) {
            val key = ko[i]
            if (key == null || ka[i] != key || kb[i] != key) return null
            if (a[i] == o[i] || a[i] == b[i]) {
                lines.add(if (b[i] == o[i]) a[i] else b[i])
            } else if (b[i] == o[i]) {
                lines.add(a[i])
            } else {
                // The same step changed on both sides: one version (a step can't be two lines).
                conflicted = true
                lines.add(if (policy == ConflictPolicy.SECOND) b[i] else a[i])
            }
        }
        return lines to conflicted
    }

    private fun distinctKeyed(a: List<String?>, b: List<String?>): Boolean =
        a.isNotEmpty() && b.isNotEmpty() && a.all { it != null } && b.all { it != null } && a.intersect(b.toSet()).isEmpty()

    /** Lines with their line endings (the last may have none). */
    fun split(text: String): List<String> {
        val lines = mutableListOf<String>()
        var start = 0
        for (i in text.indices) {
            if (text[i] == '\n') {
                lines.add(text.substring(start, i + 1))
                start = i + 1
            }
        }
        if (start < text.length) lines.add(text.substring(start))
        return lines
    }

    private fun join(lines: List<String>): String {
        val sb = StringBuilder()
        for (line in lines) {
            // A last line without its newline that ends up in the middle gets one.
            if (sb.isNotEmpty() && sb[sb.length - 1] != '\n') sb.append('\n')
            sb.append(line)
        }
        return sb.toString()
    }

    /** For each base line, the index of the line it matches in [other] (longest common subsequence), or -1. */
    private fun match(o: List<String>, other: List<String>): IntArray {
        val map = IntArray(o.size) { -1 }
        var prefix = 0
        while (prefix < o.size && prefix < other.size && o[prefix] == other[prefix]) {
            map[prefix] = prefix
            prefix++
        }
        var suffix = 0
        while (suffix < o.size - prefix && suffix < other.size - prefix && o[o.size - 1 - suffix] == other[other.size - 1 - suffix]) {
            map[o.size - 1 - suffix] = other.size - 1 - suffix
            suffix++
        }
        val n = o.size - prefix - suffix
        val m = other.size - prefix - suffix
        if (n == 0 || m == 0) return map
        val lcs = Array(n + 1) { IntArray(m + 1) }
        for (i in n - 1 downTo 0) {
            for (j in m - 1 downTo 0) {
                lcs[i][j] = if (o[prefix + i] == other[prefix + j]) lcs[i + 1][j + 1] + 1 else maxOf(lcs[i + 1][j], lcs[i][j + 1])
            }
        }
        var i = 0
        var j = 0
        while (i < n && j < m) {
            if (o[prefix + i] == other[prefix + j]) {
                map[prefix + i] = prefix + j
                i++
                j++
            } else if (lcs[i + 1][j] >= lcs[i][j + 1]) {
                i++
            } else {
                j++
            }
        }
        return map
    }
}
