package dev.todotracker.core.vault

import java.io.StringReader
import org.yaml.snakeyaml.LoaderOptions
import org.yaml.snakeyaml.composer.Composer
import org.yaml.snakeyaml.error.YAMLException
import org.yaml.snakeyaml.nodes.MappingNode
import org.yaml.snakeyaml.nodes.Node
import org.yaml.snakeyaml.nodes.ScalarNode
import org.yaml.snakeyaml.nodes.SequenceNode
import org.yaml.snakeyaml.parser.ParserImpl
import org.yaml.snakeyaml.reader.StreamReader
import org.yaml.snakeyaml.resolver.Resolver

/**
 * YAML frontmatter (TodoTracker.Core.Vault.Frontmatter): values are read as YAML nodes (never typed: `due: 2026-01-20`
 * is text), while each top-level key's original text is kept so keys the app doesn't own are written back exactly.
 */
internal class Frontmatter private constructor(private val values: MappingNode?, val blocks: List<Pair<String?, String>>) {
    companion object {
        val EMPTY = Frontmatter(null, emptyList())

        /** Splits `---` frontmatter off [text] (LF only): the frontmatter, the body, and whether there was one. */
        fun split(text: String): Triple<Frontmatter, String, Boolean> {
            if (!text.startsWith("---\n")) return Triple(EMPTY, text, false)
            var end = -1
            var position = 4
            while (position <= text.length) {
                val next = text.indexOf('\n', position)
                val line = if (next < 0) text.substring(position) else text.substring(position, next)
                val trimmed = Net.trimEnd(line)
                if (trimmed == "---" || trimmed == "...") {
                    end = position
                    position = if (next < 0) text.length else next + 1
                    break
                }
                if (next < 0) break
                position = next + 1
            }
            // An opening --- without a closing one is just a horizontal rule in the body.
            if (end < 0) return Triple(EMPTY, text, false)
            return Triple(parse(text.substring(4, end)), text.substring(position), true)
        }

        fun parse(yaml: String): Frontmatter {
            val values: MappingNode? = try {
                val options = LoaderOptions()
                val composer = Composer(ParserImpl(StreamReader(StringReader(yaml)), options), Resolver(), options)
                val root = if (composer.checkNode()) composer.node else null
                when {
                    root == null -> null
                    root is ScalarNode && root.value.isEmpty() -> null
                    root is MappingNode -> root.also(::checkDuplicates)
                    else -> throw VaultFormatException("The frontmatter must be a list of `key: value` properties.")
                }
            } catch (e: YAMLException) {
                throw VaultFormatException("The frontmatter isn't valid YAML: ${e.message}", e)
            }

            val blocks = mutableListOf<Pair<String?, String>>()
            var key: String? = null
            val raw = StringBuilder()
            for (line in yaml.split('\n')) {
                if (line.isEmpty() && raw.isEmpty() && blocks.isEmpty() && key == null) continue
                // Comments start their own block so they stay where the user put them when a key around them is rewritten.
                val comment = line.startsWith('#')
                val k = topLevelKey(line)
                if (k != null || (comment && key != null)) {
                    if (raw.isNotEmpty() || key != null) blocks.add(key to raw.toString())
                    key = if (comment) null else k
                    raw.setLength(0)
                }
                raw.append(line).append('\n')
            }
            if (raw.isNotEmpty() || key != null) {
                val last = raw.toString()
                // The yaml ends with "\n" before the closing ---, which split('\n') turns into a trailing empty line.
                blocks.add(key to if (last.endsWith("\n\n")) last.dropLast(1) else last)
            }
            return Frontmatter(values, blocks)
        }

        /** YamlDotNet refuses a mapping that has a key twice (at any depth). */
        private fun checkDuplicates(node: Node) {
            when (node) {
                is MappingNode -> {
                    val seen = HashSet<String>()
                    for (t in node.value) {
                        val k = t.keyNode
                        if (k is ScalarNode && !seen.add(k.value)) throw VaultFormatException("The frontmatter isn't valid YAML: the key \"${k.value}\" is there twice.")
                        checkDuplicates(t.valueNode)
                    }
                }
                is SequenceNode -> node.value.forEach(::checkDuplicates)
                else -> {}
            }
        }

        private fun topLevelKey(line: String): String? {
            if (line.isEmpty() || Net.isWhite(line[0]) || line[0] == '#' || line[0] == '-') return null
            val colon = line.indexOf(':')
            return if (colon > 0 && (colon == line.length - 1 || Net.isWhite(line[colon + 1]))) Net.trim(line.substring(0, colon)).trim('"', '\'') else null
        }

        fun get(map: MappingNode, key: String): String? =
            (map.value.firstOrNull { (it.keyNode as? ScalarNode)?.value?.let { k -> Net.equalsCi(k, key) } == true }?.valueNode as? ScalarNode)?.value
    }

    private fun node(key: String): Node? =
        values?.value?.firstOrNull { (it.keyNode as? ScalarNode)?.value?.let { k -> Net.equalsCi(k, key) } == true }?.valueNode

    fun scalar(key: String): String? = (node(key) as? ScalarNode)?.value

    fun list(key: String): List<String> = when (val n = node(key)) {
        is SequenceNode -> n.value.filterIsInstance<ScalarNode>().map { it.value }.filter { it.isNotEmpty() }
        is ScalarNode -> if (n.value.isEmpty()) emptyList() else n.value.split(',').map(Net::trim).filter { it.isNotEmpty() }
        else -> emptyList()
    }

    fun maps(key: String): List<MappingNode> = (node(key) as? SequenceNode)?.value?.filterIsInstance<MappingNode>() ?: emptyList()

    fun has(key: String): Boolean = node(key) != null
}
