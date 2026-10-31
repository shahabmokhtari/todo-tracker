#if canImport(SwiftUI)
import SwiftUI
import TodoTrackerKit

/// The Board (the web's board.js): the group's top-level tasks as cards in a small, fixed flow, Inbox → Next → Doing →
/// Done. Drag a card to another column or place (or Option-arrows on the selected one); Doing is kept to three on
/// purpose. Starting a timer moves a card to Doing; Done finishes it, and out of Done opens it again.
struct BoardView: View {
    @ObservedObject var model: BoardModel
    @Environment(\.shellState) private var shell
    @FocusState private var boardFocused: Bool
    /// The first column on screen when they don't all fit (the column bar and a picked card's column scroll to it).
    @State private var shownColumn: BoardColumn? = .inbox
    /// How many columns fit side by side.
    @State private var visibleCount = BoardColumn.allCases.count

    var body: some View {
        let columns = model.boardColumns
        GeometryReader { geo in
            // As many columns side by side as fit at a comfortable width; the rest scroll sideways.
            let visible = max(1, min(BoardColumn.allCases.count, Int(geo.size.width / 250)))
            VStack(alignment: .leading, spacing: 8) {
                SearchFilterBar(model: model).padding(.horizontal, 16)
                Text("Drag cards along, or select one and use Option and the arrow keys. Starting a timer moves a card to Doing.")
                    .font(.caption).foregroundStyle(.secondary)
                    .padding(.horizontal, 16)
                if visible < BoardColumn.allCases.count {
                    // Not all columns fit: each is a tap away (and a swipe sideways).
                    columnBar(columns, visible: visible).padding(.horizontal, 16)
                }
                ScrollView(.horizontal) {
                    HStack(alignment: .top, spacing: 12) {
                        ForEach(BoardColumn.allCases) { column in
                            BoardColumnView(model: model, column: column, cards: columns[column] ?? [], selected: shell?.selectedTask)
                                .containerRelativeFrame(.horizontal, count: visible, spacing: 12)
                                .id(column)
                        }
                    }
                    .scrollTargetLayout()
                }
                .scrollPosition(id: $shownColumn, anchor: .leading)
                // The margins count in the columns' width, so the last one ends where the screen does.
                .contentMargins(.horizontal, 16, for: .scrollContent)
                .scrollTargetBehavior(.viewAligned)
                .scrollIndicators(visible < BoardColumn.allCases.count ? .automatic : .hidden)
            }
            .padding(.top, 8)
        }
        .focusable()
        .focused($boardFocused)
        .focusEffectDisabled()
        .onKeyPress(phases: .down) { press in key(press, columns: columns) }
        .onAppear { boardFocused = true }
        // A card picked with the keyboard (or opened elsewhere) in a column off screen: the board scrolls to it.
        .onChange(of: shell?.selectedTask) { _, id in
            guard let id, let card = model.board.find(id), card.parent == nil else { return }
            let all = BoardColumn.allCases
            let column = BoardColumns.column(of: card)
            if let first = shownColumn.flatMap({ all.firstIndex(of: $0) }), let want = all.firstIndex(of: column),
               want >= first && want < first + visibleCount { return }
            withAnimation(.snappy) { shownColumn = all[min(all.firstIndex(of: column) ?? 0, all.count - visibleCount)] }
        }
        .background(Color.secondary.opacity(0.04))
    }

    /// The columns by name and count; the ones on screen are highlighted, a tap brings one into view.
    private func columnBar(_ columns: [BoardColumn: [WorkItem]], visible: Int) -> some View {
        let all = BoardColumn.allCases
        let first = shownColumn.flatMap { all.firstIndex(of: $0) } ?? 0
        return HStack(spacing: 6) {
            ForEach(Array(all.enumerated()), id: \.element) { index, column in
                let onScreen = index >= first && index < first + visible
                Button {
                    withAnimation(.snappy) { shownColumn = all[min(index, all.count - visible)] }
                } label: {
                    Text("\(column.name) \(columns[column]?.count ?? 0)")
                        .font(.subheadline.weight(.semibold))
                        .lineLimit(1)
                        .padding(.horizontal, 10).padding(.vertical, 6)
                        .foregroundStyle(onScreen ? Theme.accent : .secondary)
                        .background(Capsule().fill(onScreen ? Theme.accent.opacity(0.14) : Color.secondary.opacity(0.1)))
                }
                .buttonStyle(.plain)
                .accessibilityLabel("Show \(column.name)")
                .accessibilityAddTraits(onScreen ? .isSelected : [])
            }
            Spacer(minLength: 0)
        }
        .onAppear { visibleCount = visible }
        .onChange(of: visible) { _, now in visibleCount = now }
    }

    /// Arrows pick a card; Option-arrows move it; Space or ⌘↩ finishes it; Return opens it.
    private func key(_ press: KeyPress, columns: [BoardColumn: [WorkItem]]) -> KeyPress.Result {
        guard let shell else { return .ignored }
        let cards = BoardColumn.allCases.map { columns[$0] ?? [] }
        let direction: BoardColumns.Direction?
        switch press.key {
        case .leftArrow: direction = .left
        case .rightArrow: direction = .right
        case .upArrow: direction = .up
        case .downArrow: direction = .down
        default: direction = nil
        }
        guard let selected = shell.selectedTask, let card = model.board.find(selected), card.parent == nil else {
            // Nothing picked yet: an arrow picks the first card.
            if direction != nil, let first = cards.first(where: { !$0.isEmpty })?.first {
                shell.selectedTask = first.id
                return .handled
            }
            return .ignored
        }
        if let direction {
            if press.modifiers.contains(.option) {
                model.moveCard(card.id, direction)
                return .handled
            }
            let col = BoardColumn.allCases.firstIndex(of: BoardColumns.column(of: card)) ?? 0
            let row = cards[col].firstIndex { $0.id == card.id } ?? 0
            switch direction {
            case .up: if row > 0 { shell.selectedTask = cards[col][row - 1].id }
            case .down: if row + 1 < cards[col].count { shell.selectedTask = cards[col][row + 1].id }
            case .left, .right:
                var to = col
                repeat { to += direction == .left ? -1 : 1 } while cards.indices.contains(to) && cards[to].isEmpty
                if cards.indices.contains(to) { shell.selectedTask = cards[to][min(row, cards[to].count - 1)].id }
            }
            return .handled
        }
        if press.key == .space || (press.key == .return && press.modifiers.contains(.command)) {
            model.toggleDone(card.id)
            return .handled
        }
        if press.key == .return {
            shell.open(card.id)
            return .handled
        }
        return .ignored
    }
}

/// One column: its name and count (Doing as 2/3, with a word when it holds more), its cards, and a box to add one.
struct BoardColumnView: View {
    @ObservedObject var model: BoardModel
    let column: BoardColumn
    let cards: [WorkItem]
    let selected: UUID?
    @State private var newTitle = ""
    @State private var targeted = false
    @State private var dropBefore: UUID?

    var body: some View {
        let fill = column == .doing ? BoardColumns.fill(cards.count) : .ok
        VStack(alignment: .leading, spacing: 8) {
            HStack(spacing: 6) {
                Image(systemName: symbol).font(.subheadline.weight(.semibold)).foregroundStyle(.secondary)
                Text(column.name).font(.headline)
                Spacer()
                Text(column == .doing ? "\(cards.count)/\(BoardColumns.wipLimit)" : "\(cards.count)")
                    .font(.caption.weight(.bold)).monospacedDigit()
                    .padding(.horizontal, 8).padding(.vertical, 2)
                    .foregroundStyle(fill == .over ? Color(hex: "#ef4444") : fill == .full ? Theme.warnText : .secondary)
                    .background(Capsule().fill(fill == .over ? Color(hex: "#ef4444").opacity(0.14) : Color.secondary.opacity(0.12)))
                    .help(column == .doing ? "Keep Doing to \(BoardColumns.wipLimit): finishing beats starting" : column.hint)
            }
            .padding(.horizontal, 4)
            .accessibilityElement(children: .combine)
            .accessibilityAddTraits(.isHeader)
            if fill == .over {
                Text(BoardColumns.overLimitMessage).font(.caption).foregroundStyle(Color(hex: "#ef4444"))
                    .padding(.horizontal, 4)
            }
            ScrollView {
                LazyVStack(spacing: 8) {
                    if cards.isEmpty {
                        Text(column.emptyText).font(.subheadline).foregroundStyle(.tertiary)
                            .frame(maxWidth: .infinity, minHeight: 60)
                    }
                    ForEach(cards) { card in
                        BoardCard(model: model, card: card, column: column, selected: selected == card.id)
                            .overlay(alignment: .top) {
                                if dropBefore == card.id { Capsule().fill(Theme.accent).frame(height: 3).offset(y: -5) }
                            }
                            // Dropped on a card: it goes before that one.
                            .dropDestination(for: String.self) { texts, _ in
                                let ids = TaskDrag.ids(texts)
                                ids.forEach { model.moveCard($0, to: column, before: column == .done ? nil : card.id) }
                                return !ids.isEmpty
                            } isTargeted: { over in
                                if over { dropBefore = card.id } else if dropBefore == card.id { dropBefore = nil }
                            }
                    }
                }
                .padding(.vertical, 2)
            }
            if column != .done {
                TextField("Add to \(column.name)…", text: $newTitle)
                    .textFieldStyle(.roundedBorder)
                    .onSubmit {
                        model.addTask(newTitle, stage: column.stage)
                        newTitle = ""
                    }
                    .accessibilityLabel("Add a task to \(column.name)")
            }
        }
        .padding(10)
        .frame(maxHeight: .infinity, alignment: .top)
        .background(RoundedRectangle(cornerRadius: 16, style: .continuous).fill(.background.secondary))
        .overlay(RoundedRectangle(cornerRadius: 16, style: .continuous)
            .strokeBorder(targeted ? Theme.accent : fill == .over ? Color(hex: "#ef4444").opacity(0.5) : Color.secondary.opacity(0.15), lineWidth: targeted ? 2 : 1))
        // Dropped on the column (not on a card): its place in the shared order, or last within the column.
        .dropDestination(for: String.self) { texts, _ in
            let ids = TaskDrag.ids(texts)
            ids.forEach { model.moveCard($0, to: column) }
            return !ids.isEmpty
        } isTargeted: { targeted = $0 }
        .padding(.bottom, 12)
    }

    private var symbol: String {
        switch column {
        case .inbox: return "tray"
        case .next: return "list.bullet"
        case .doing: return "play.fill"
        case .done: return "checkmark"
        }
    }
}

/// A card: its title, group color, priority, labels, tags, deadline, progress and time; a timer, done (or archive) and
/// a Move menu; a click opens it.
struct BoardCard: View {
    @ObservedObject var model: BoardModel
    let card: WorkItem
    let column: BoardColumn
    let selected: Bool
    @Environment(\.shellState) private var shell

    var body: some View {
        let timing = model.runningTimer?.item.root.id == card.id
        VStack(alignment: .leading, spacing: 6) {
            HStack(alignment: .top, spacing: 8) {
                PriorityDot(priority: card.priority).padding(.top, 1)
                Text(Presentation.titleWithIcon(card))
                    .font(.subheadline.weight(.semibold))
                    .strikethrough(card.isDone)
                    .foregroundStyle(card.isDone ? .secondary : .primary)
                    .multilineTextAlignment(.leading)
                    .frame(maxWidth: .infinity, alignment: .leading)
                if card.repeatRule != nil { Image(systemName: "repeat").font(.caption).foregroundStyle(.secondary).accessibilityLabel("Repeats") }
            }
            if !card.labels.isEmpty || !card.tags.isEmpty {
                FlowLayout(spacing: 4) {
                    ForEach(card.labels, id: \.self) { LabelChip(name: $0, color: model.labelColor($0)) }
                    ForEach(card.tags.prefix(3), id: \.self) { TagChip(tag: $0) }
                }
            }
            let facts = CardFacts.chips(card, now: model.now)
            if !facts.isEmpty {
                FlowLayout(spacing: 4) { ForEach(facts, id: \.self) { ChipView(chip: $0) } }
            }
            HStack(spacing: 14) {
                if !card.isDone {
                    Button { model.toggleTimer(card.id) } label: { Image(systemName: timing ? "stop.circle.fill" : "play.circle") }
                        .foregroundStyle(timing ? Color(hex: "#ef4444") : .secondary)
                        .accessibilityLabel(timing ? "Stop timing \(card.title)" : "Start timing \(card.title)")
                        .help(timing ? "Stop the timer" : "Start the timer")
                }
                if column == .done {
                    Button { model.archive(card.id) } label: { Image(systemName: "archivebox") }
                        .accessibilityLabel("Archive \(card.title)").help("Archive")
                } else {
                    Button { model.complete(card.id) } label: { Image(systemName: "checkmark.circle") }
                        .accessibilityLabel("Mark \(card.title) done").help("Mark done")
                }
                Menu {
                    ForEach(BoardColumn.allCases.filter { $0 != column }) { to in
                        Button("To \(to.name)") { model.moveCard(card.id, to: to) }
                    }
                    if column != .done {
                        Divider()
                        Button("Up") { model.moveCard(card.id, .up) }
                        Button("Down") { model.moveCard(card.id, .down) }
                    }
                } label: { Image(systemName: "arrow.left.arrow.right") }
                    .menuStyle(.button)
                    .menuIndicator(.hidden)
                    .fixedSize()
                    .accessibilityLabel("Move \(card.title)")
                    .help("Move to another column")
                Spacer(minLength: 0)
            }
            .buttonStyle(.borderless)
            .font(.callout)
            .foregroundStyle(.secondary)
        }
        .padding(10)
        .padding(.leading, 4)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(RoundedRectangle(cornerRadius: 12, style: .continuous).fill(.background))
        .overlay(alignment: .leading) {
            // The group's color down the side.
            UnevenRoundedRectangle(topLeadingRadius: 12, bottomLeadingRadius: 12, style: .continuous)
                .fill(Color(hex: model.groupColor(card))).frame(width: 4)
        }
        .overlay(RoundedRectangle(cornerRadius: 12, style: .continuous)
            .strokeBorder(selected ? Theme.accent : timing ? Color(hex: "#ef4444").opacity(0.6) : Color.secondary.opacity(0.18), lineWidth: selected ? 2 : 1))
        .opacity(card.isDone ? 0.75 : 1)
        .contentShape(RoundedRectangle(cornerRadius: 12, style: .continuous))
        .pointerHover()
        .onTapGesture {
            shell?.selectedTask = card.id
            shell?.open(card.id)
        }
        .contextMenu { TaskContextMenu(model: model, id: card.id) }
        .draggable(TaskDrag.payload(card.id)) {
            Text(card.title).font(.subheadline.weight(.semibold)).padding(8)
                .background(RoundedRectangle(cornerRadius: 10).fill(.background))
        }
        .accessibilityElement(children: .contain)
        .accessibilityLabel("\(card.title)\(card.isDone ? ", done" : "")")
        .accessibilityAddTraits(selected ? .isSelected : [])
    }
}
#endif
