import SwiftUI
import OneThingKit
import OneThingWidgetUI
import WidgetKit

/// The home-screen widgets (and, on iPhone and iPad, the Lock Screen): Now and Today. They show the snapshot the app
/// writes to the App Group (`WidgetSnapshot`), one timeline entry for each moment the lists change in the next day, so
/// they stay right with the app closed.
@main
struct OneThingWidgets: WidgetBundle {
    var body: some Widget {
        NowWidget()
        TodayWidget()
    }
}

struct WidgetEntry: TimelineEntry {
    let date: Date
    let content: WidgetContent
}

struct SnapshotProvider: TimelineProvider {
    func placeholder(in context: Context) -> WidgetEntry { WidgetEntry(date: Date(), content: Self.sample) }

    func getSnapshot(in context: Context, completion: @escaping (WidgetEntry) -> Void) {
        let entry = entries(from: Date()).first ?? placeholder(in: context)
        // In the widget gallery before the app wrote anything: a made-up task, to show what it looks like.
        completion(context.isPreview && !entry.content.hasSnapshot ? placeholder(in: context) : entry)
    }

    func getTimeline(in context: Context, completion: @escaping (Timeline<WidgetEntry>) -> Void) {
        let now = Date()
        let entries = entries(from: now)
        // After the last change the app worked out, ask again (the app writes a new snapshot as the day moves on).
        let next = max(entries.last?.date ?? now, now).addingTimeInterval(3600)
        completion(Timeline(entries: entries, policy: .after(next)))
    }

    private func entries(from now: Date) -> [WidgetEntry] {
        let shelf = WidgetShelf.shared
        let snapshot = shelf?.snapshot()
        let hidden = shelf?.hidden() ?? []
        let dates = [now] + (snapshot?.frames.map(\.at).filter { $0 > now } ?? [])
        return dates.map { WidgetEntry(date: $0, content: WidgetContent(snapshot: snapshot, at: $0, hidden: hidden)) }
    }

    static let sample: WidgetContent = {
        let focus = WidgetItem(id: UUID(), title: "Reply to the security review", group: "Work", color: "#3b82f6", openSubtasks: false)
        let next = ["Prep 1:1 agenda", "Book the dentist", "Water the plants"].map {
            WidgetItem(id: UUID(), title: $0, group: "Personal", color: "#22c55e", openSubtasks: false)
        }
        return WidgetContent(view: WidgetView(focus: focus, next: next, more: 2, today: [focus] + next, later: [], nowCount: 6, laterCount: 0), hasSnapshot: true)
    }()
}

/// Done: marks the task done through the app (MarkDoneIntent).
private func doneButton(_ item: WidgetItem, compact: Bool) -> some View {
    Button(intent: MarkDoneIntent(task: item.id)) { DoneLabel(compact: compact) }
        .buttonStyle(.plain)
}

struct NowWidget: Widget {
    var body: some WidgetConfiguration {
        StaticConfiguration(kind: "Now", provider: SnapshotProvider()) { entry in
            NowEntryView(entry: entry)
        }
        .configurationDisplayName(WidgetWords.nowName)
        .description(WidgetWords.nowDescription)
        .supportedFamilies(Self.families)
    }

    #if os(iOS)
    static let families: [WidgetFamily] = [.systemSmall, .systemMedium, .accessoryRectangular, .accessoryInline]
    #else
    static let families: [WidgetFamily] = [.systemSmall, .systemMedium]
    #endif
}

struct NowEntryView: View {
    let entry: WidgetEntry
    @Environment(\.widgetFamily) private var family

    private var size: WidgetSize {
        switch family {
        case .systemSmall: return .small
        #if os(iOS)
        case .accessoryRectangular: return .rectangular
        case .accessoryInline: return .inline
        #endif
        default: return .medium
        }
    }

    var body: some View {
        NowWidgetView(content: entry.content, size: size) { doneButton($0, compact: family == .systemSmall) }
            // Tapping the widget opens the app on the task (onething://task/<id>).
            .widgetURL(entry.content.view.focus.map(widgetLink))
            .containerBackground(for: .widget) {
                if size == .rectangular || size == .inline { Color.clear } else { Rectangle().fill(.background) }
            }
    }
}

struct TodayWidget: Widget {
    var body: some WidgetConfiguration {
        StaticConfiguration(kind: "Today", provider: SnapshotProvider()) { entry in
            TodayWidgetView(content: entry.content) { doneButton($0, compact: true) }
                .widgetURL(entry.content.view.focus.map(widgetLink))
                .containerBackground(.background, for: .widget)
        }
        .configurationDisplayName(WidgetWords.todayName)
        .description(WidgetWords.todayDescription)
        .supportedFamilies([.systemLarge])
    }
}
