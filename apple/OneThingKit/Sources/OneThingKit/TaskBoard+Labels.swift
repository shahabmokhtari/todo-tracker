import Foundation

/// Tags and labels (TaskBoard.Organization.cs): the same rules as OneThing.Core, checked by tests/fixtures/labels.json.
extension TaskBoard {
    public static let maxLabelLength = 40

    /// Accessible on light and dark backgrounds; a new label takes the next one nobody uses.
    public static let labelPalette = ["#7c3aed", "#0ea5e9", "#10b981", "#f59e0b", "#ef4444", "#ec4899", "#14b8a6", "#64748b"]

    /// The label called `name` (case doesn't matter), or nil.
    public func findLabel(_ name: String) -> LabelDefinition? {
        let clean = name.trimmingCharacters(in: .whitespaces)
        return labels.first { Ordinal.equalIgnoreCase($0.name, clean) }
    }

    /// The color a task shows with: its own, else its first label's (nil: none).
    public func accent(of item: WorkItem) -> String? {
        item.color ?? item.labels.lazy.compactMap { self.findLabel($0)?.color }.first
    }

    /// A new label (color nil: the next one nobody uses).
    @discardableResult
    public func defineLabel(_ name: String, color: String?, actor: Actor = .user, now: Date) throws -> LabelDefinition {
        let clean = try Self.validateLabelNames([name])[0]
        if let color { try Self.validateColor(color) }
        if findLabel(clean) != nil { throw BoardError.invalid("A label named \"\(clean)\" already exists.") }
        let label: LabelDefinition
        if let color {
            label = LabelDefinition(name: clean, color: color.lowercased())
            labels.append(label)
        } else {
            label = ensureLabel(clean)
        }
        log(now, TaskBoard.boardScopeId, "labelChanged", "Added label \"\(clean)\"", actor)
        return label
    }

    /// Renames or recolors a label: every task with it gets the new name.
    public func updateLabel(_ name: String, newName: String, color: String, actor: Actor = .user, now: Date) throws {
        guard let label = findLabel(name) else { throw BoardError.notFound("Label \"\(name)\" was not found.") }
        let clean = try Self.validateLabelNames([newName])[0]
        try Self.validateColor(color)
        if let other = findLabel(clean), other !== label { throw BoardError.invalid("A label named \"\(clean)\" already exists.") }
        let old = label.name
        for item in allItems {
            if let i = item.labels.firstIndex(where: { Ordinal.equalIgnoreCase($0, old) }) { item.labels[i] = clean }
        }
        label.name = clean
        label.color = color.lowercased()
        log(now, TaskBoard.boardScopeId, "labelChanged", old == clean ? "Updated label \"\(clean)\"" : "Renamed label \"\(old)\" to \"\(clean)\"", actor)
    }

    /// Removes a label: it comes off every task (the tasks stay as they are otherwise).
    public func deleteLabel(_ name: String, actor: Actor = .user, now: Date) throws {
        guard let label = findLabel(name) else { throw BoardError.notFound("Label \"\(name)\" was not found.") }
        for item in allItems { item.labels.removeAll { Ordinal.equalIgnoreCase($0, label.name) } }
        labels.removeAll { $0 === label }
        log(now, TaskBoard.boardScopeId, "labelChanged", "Deleted label \"\(label.name)\"", actor)
    }

    /// A task's labels, as picked: each one that isn't on the board yet is made (with the next color).
    public func setLabels(_ id: UUID, _ names: [String], actor: Actor = .user, now: Date) throws {
        let item = try get(id)
        let resolved = try Self.validateLabelNames(names).map { ensureLabel($0).name }
        item.labels = resolved
        log(now, id, "updated", resolved.isEmpty ? "Cleared labels on \"\(item.title)\"" : "Labels on \"\(item.title)\": \(resolved.joined(separator: ", "))", actor)
    }

    /// A task's tags (cleaned, each once).
    public func setTags(_ id: UUID, _ tags: [String], actor: Actor = .user, now: Date) throws {
        let item = try get(id)
        let clean = try Self.normalizeTags(tags)
        item.tags = clean
        log(now, id, "updated", clean.isEmpty ? "Cleared tags on \"\(item.title)\"" : "Tags on \"\(item.title)\": \(clean.map { "#\($0)" }.joined(separator: " "))", actor)
    }

    /// The longest icon (in UTF-16 units, as .NET counts): one emoji, even a family or a flag, or a couple of letters.
    public static let maxIconLength = 16

    /// An emoji shown before the title (nil or blank: none).
    public func setIcon(_ id: UUID, _ icon: String?, actor: Actor = .user, now: Date) throws {
        let item = try get(id)
        let clean = icon?.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
        if !clean.isEmpty {
            let bad = clean.utf16.count > Self.maxIconLength
                || clean.unicodeScalars.contains { CharacterSet.controlCharacters.contains($0) || $0 == "\u{2028}" || $0 == "\u{2029}" }
            if bad { throw BoardError.invalid("An icon is an emoji or a few characters (at most \(Self.maxIconLength)).") }
        }
        item.icon = clean.isEmpty ? nil : clean
        log(now, id, "updated", "Updated \"\(item.title)\"", actor)
    }

    /// The task's own color, #rrggbb (nil or blank: none, it shows its first label's).
    public func setColor(_ id: UUID, _ color: String?, actor: Actor = .user, now: Date) throws {
        let item = try get(id)
        let clean = color?.trimmingCharacters(in: .whitespaces) ?? ""
        if !clean.isEmpty { try Self.validateColor(clean) }
        item.color = clean.isEmpty ? nil : clean.lowercased()
        log(now, id, "updated", "Updated \"\(item.title)\"", actor)
    }

    /// The label (ignoring case), or a new one with the next palette color.
    @discardableResult
    func ensureLabel(_ name: String) -> LabelDefinition {
        if let existing = findLabel(name) { return existing }
        let used = Set(labels.map { $0.color.lowercased() })
        let color = Self.labelPalette.first { !used.contains($0) } ?? Self.labelPalette[labels.count % Self.labelPalette.count]
        let label = LabelDefinition(name: name, color: color)
        labels.append(label)
        return label
    }

    /// Names as given: trimmed, each once (ignoring case); 1 to 40 characters without commas, brackets or quotes.
    static func validateLabelNames(_ names: [String]) throws -> [String] {
        var result: [String] = []
        for raw in names {
            let name = raw.trimmingCharacters(in: .whitespaces)
            let bad = name.isEmpty || name.utf16.count > maxLabelLength
                || name.unicodeScalars.contains { CharacterSet.controlCharacters.contains($0) || ",[]\"".unicodeScalars.contains($0) }
            if bad { throw BoardError.invalid("\"\(raw)\" isn't a valid label. Use up to \(maxLabelLength) characters without commas, brackets, or quotes.") }
            if !result.contains(where: { Ordinal.equalIgnoreCase($0, name) }) { result.append(name) }
        }
        guard result.count <= maxTags else { throw BoardError.invalid("At most \(maxTags) labels per task.") }
        return result
    }

    static func validateColor(_ color: String) throws {
        let hex = color.dropFirst()
        guard color.hasPrefix("#"), hex.count == 6, hex.allSatisfy({ $0.isHexDigit }) else {
            throw BoardError.invalid("Colors must be #rrggbb.")
        }
    }
}
