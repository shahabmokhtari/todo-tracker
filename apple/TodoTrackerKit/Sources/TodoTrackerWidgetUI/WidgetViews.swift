#if canImport(SwiftUI)
import SwiftUI
import TodoTrackerKit

/// The sizes the widgets come in (WidgetKit's families, without WidgetKit, so the views render in tests too).
public enum WidgetSize: Sendable {
    case small, medium, large
    /// Lock screen (iPhone, iPad).
    case rectangular, inline
}

/// What a widget shows at one moment: nil snapshot means the app hasn't written one yet.
public struct WidgetContent: Sendable {
    public let view: WidgetView
    public let hasSnapshot: Bool

    public init(view: WidgetView, hasSnapshot: Bool) {
        self.view = view
        self.hasSnapshot = hasSnapshot
    }

    /// What the shared snapshot says for `date`, leaving out tasks just marked done on a widget.
    public init(snapshot: WidgetSnapshot?, at date: Date, hidden: Set<UUID> = []) {
        self.init(view: snapshot?.view(at: date, hiding: hidden) ?? .empty, hasSnapshot: snapshot != nil)
    }
}

/// The words the widgets say (the same on every platform: Android's widget says them too).
public enum WidgetWords {
    public static let nowName = "Now"
    public static let nowDescription = "The one thing to do now."
    public static let todayName = "Today"
    public static let todayDescription = "Your list for today, and what comes back later."
    public static let nothing = "Nothing to do right now. Enjoy it."
    public static let openApp = "Open Onething once to see your tasks here."
    public static let done = "Done"

    public static func more(_ count: Int) -> String { "\(count) more today" }
}

enum WidgetTheme {
    static let accent = Color(red: 0x63 / 255, green: 0x66 / 255, blue: 0xF1 / 255)

    static func color(_ hex: String?) -> Color {
        guard let hex, let value = UInt64(hex.replacingOccurrences(of: "#", with: ""), radix: 16) else { return accent }
        return Color(red: Double((value >> 16) & 0xFF) / 255, green: Double((value >> 8) & 0xFF) / 255, blue: Double(value & 0xFF) / 255)
    }
}

/// The task's link (todotracker://task/<id>): tapping it opens the app on that task.
public func widgetLink(_ item: WidgetItem) -> URL { URL(string: TaskLinks.forTask(item.id))! }

extension EnvironmentValues {
    /// Off when the widget is drawn outside WidgetKit (a picture in a test): links are drawn as what they show.
    @Entry public var widgetLinks = true
}

/// Opens the app on the task when tapped (in a widget).
struct TaskLink<Content: View>: View {
    let item: WidgetItem
    @ViewBuilder let content: Content
    @Environment(\.widgetLinks) private var links

    var body: some View {
        if links { Link(destination: widgetLink(item)) { content } } else { content }
    }
}

struct GroupLabel: View {
    let item: WidgetItem

    var body: some View {
        if let group = item.group {
            HStack(spacing: 4) {
                Circle().fill(WidgetTheme.color(item.color)).frame(width: 7, height: 7)
                Text(group).lineLimit(1)
            }
            .font(.caption2.weight(.medium))
            .foregroundStyle(.secondary)
        }
    }
}

/// The look of the Done button (the widget wraps it in a button that marks the task done).
public struct DoneLabel: View {
    var compact = false

    public init(compact: Bool = false) { self.compact = compact }

    public var body: some View {
        Label(WidgetWords.done, systemImage: "checkmark")
            .labelStyle(.titleAndIcon)
            .font((compact ? Font.caption2 : .caption).weight(.semibold))
            .lineLimit(1)
            .padding(.horizontal, compact ? 8 : 10)
            .padding(.vertical, compact ? 3 : 5)
            .foregroundStyle(.white)
            .background(Capsule().fill(WidgetTheme.accent))
    }
}

/// "Open" in place of Done: a task with open subtasks is finished in the app.
struct OpenLabel: View {
    let item: WidgetItem

    var body: some View {
        TaskLink(item: item) {
            Text("Open")
                .font(.caption.weight(.semibold))
                .padding(.horizontal, 10)
                .padding(.vertical, 5)
                .foregroundStyle(WidgetTheme.accent)
                .background(Capsule().fill(WidgetTheme.accent.opacity(0.14)))
        }
    }
}

struct EmptyMessage: View {
    let content: WidgetContent

    var body: some View {
        VStack(alignment: .leading, spacing: 4) {
            Image(systemName: content.hasSnapshot ? "sparkles" : "checklist")
                .font(.title3)
                .foregroundStyle(WidgetTheme.accent)
            Text(content.hasSnapshot ? WidgetWords.nothing : WidgetWords.openApp)
                .font(.subheadline.weight(.medium))
                .fixedSize(horizontal: false, vertical: true)
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .leading)
    }
}

/// The Now widget: the one thing to do now (and its group), Done, and (medium) the next few and how many more today.
public struct NowWidgetView<Done: View>: View {
    let content: WidgetContent
    let size: WidgetSize
    let done: (WidgetItem) -> Done

    public init(content: WidgetContent, size: WidgetSize, @ViewBuilder done: @escaping (WidgetItem) -> Done) {
        self.content = content
        self.size = size
        self.done = done
    }

    public var body: some View {
        switch size {
        case .inline:
            Text(content.view.focus?.title ?? (content.hasSnapshot ? "Nothing to do now" : "Onething"))
        case .rectangular:
            VStack(alignment: .leading, spacing: 1) {
                Text(WidgetWords.nowName.uppercased()).font(.caption2.weight(.semibold))
                Text(content.view.focus?.title ?? WidgetWords.nothing).font(.headline).lineLimit(2)
                if let group = content.view.focus?.group { Text(group).font(.caption2).foregroundStyle(.secondary).lineLimit(1) }
            }
            .frame(maxWidth: .infinity, alignment: .leading)
        case .medium, .large:
            HStack(alignment: .top, spacing: 14) {
                focusColumn(compactDone: false)
                if content.view.focus != nil {
                    Divider()
                    nextColumn
                }
            }
        case .small:
            focusColumn(compactDone: true)
        }
    }

    @ViewBuilder private func focusColumn(compactDone: Bool) -> some View {
        if let focus = content.view.focus {
            VStack(alignment: .leading, spacing: 6) {
                Text(WidgetWords.nowName.uppercased())
                    .font(.caption2.weight(.bold))
                    .foregroundStyle(WidgetTheme.accent)
                Text(focus.title)
                    .font(.headline)
                    .lineLimit(size == .small ? 3 : 4)
                    .minimumScaleFactor(0.85)
                GroupLabel(item: focus)
                Spacer(minLength: 0)
                HStack(spacing: 6) {
                    if focus.openSubtasks { OpenLabel(item: focus) } else { done(focus) }
                    if size == .small, content.view.more(after: 1) > 0 {
                        Text("+\(content.view.more(after: 1))").font(.caption2.weight(.semibold)).foregroundStyle(.secondary)
                    }
                }
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
        } else {
            EmptyMessage(content: content)
        }
    }

    private var nextColumn: some View {
        VStack(alignment: .leading, spacing: 6) {
            Text("NEXT").font(.caption2.weight(.bold)).foregroundStyle(.secondary)
            ForEach(content.view.next) { item in
                TaskLink(item: item) {
                    HStack(spacing: 6) {
                        Circle().fill(WidgetTheme.color(item.color)).frame(width: 6, height: 6)
                        Text(item.title).font(.caption).lineLimit(1)
                    }
                }
            }
            if content.view.next.isEmpty { Text("That's all for now.").font(.caption).foregroundStyle(.secondary) }
            Spacer(minLength: 0)
            if content.view.more > 0 {
                Text(WidgetWords.more(content.view.more)).font(.caption2).foregroundStyle(.secondary)
            }
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
    }
}

/// The Today widget: the Do now list (the first one highlighted), each with Done, then what comes back later today.
public struct TodayWidgetView<Done: View>: View {
    let content: WidgetContent
    let rows: Int
    let done: (WidgetItem) -> Done

    /// - Parameter rows: how many tasks fit (large: 6).
    public init(content: WidgetContent, rows: Int = 6, @ViewBuilder done: @escaping (WidgetItem) -> Done) {
        self.content = content
        self.rows = rows
        self.done = done
    }

    public var body: some View {
        let view = content.view
        let today = Array(view.today.prefix(rows))
        let later = Array(view.later.prefix(max(0, rows - today.count)))
        let more = view.more(after: today.count) - later.count
        VStack(alignment: .leading, spacing: 8) {
            HStack {
                Text(WidgetWords.todayName).font(.headline)
                Spacer()
                if view.nowCount > 0 { Text("\(view.nowCount) to do").font(.caption).foregroundStyle(.secondary) }
            }
            if view.focus == nil && view.later.isEmpty {
                EmptyMessage(content: content)
            } else {
                ForEach(Array(today.enumerated()), id: \.element.id) { index, item in
                    row(item, first: index == 0)
                }
                if today.isEmpty { Text(WidgetWords.nothing).font(.subheadline).foregroundStyle(.secondary) }
                if !later.isEmpty {
                    Text("LATER TODAY").font(.caption2.weight(.bold)).foregroundStyle(.secondary).padding(.top, 2)
                    ForEach(later, id: \.item.id) { entry in
                        TaskLink(item: entry.item) {
                            HStack(spacing: 6) {
                                Text(entry.at, style: .time).font(.caption.monospacedDigit()).foregroundStyle(.secondary)
                                Text(entry.item.title).font(.caption).lineLimit(1)
                            }
                        }
                    }
                }
                Spacer(minLength: 0)
                if more > 0 { Text(WidgetWords.more(more)).font(.caption2).foregroundStyle(.secondary) }
            }
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
    }

    private func row(_ item: WidgetItem, first: Bool) -> some View {
        HStack(spacing: 8) {
            TaskLink(item: item) {
                VStack(alignment: .leading, spacing: 1) {
                    Text(item.title).font(first ? .subheadline.weight(.semibold) : .subheadline).lineLimit(1)
                    GroupLabel(item: item)
                }
                .frame(maxWidth: .infinity, alignment: .leading)
            }
            if item.openSubtasks { OpenLabel(item: item) } else { done(item) }
        }
        .padding(.vertical, first ? 6 : 2)
        .padding(.horizontal, first ? 8 : 0)
        .background(RoundedRectangle(cornerRadius: 10, style: .continuous).fill(first ? WidgetTheme.accent.opacity(0.12) : Color.clear))
    }
}
#endif
