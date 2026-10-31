#if canImport(SwiftUI)
import SwiftUI
import TodoTrackerKit

/// Done (the web's done.js): what got finished, day by day, with the time it took; Reopen or Archive each. The Archive
/// tab shows what was put away, to bring back.
struct DoneView: View {
    @ObservedObject var model: BoardModel
    @Environment(\.shellState) private var shell
    @State private var tab = Tab.done
    @State private var filter = ""

    enum Tab: Hashable { case done, archive }

    private var selection: Binding<UUID?> {
        Binding(get: { shell?.selectedTask }, set: { shell?.selectedTask = $0 })
    }

    var body: some View {
        let all = tab == .done ? model.finishedRoots : model.archivedRoots
        let list = filter.isEmpty ? all : all.filter { $0.title.localizedCaseInsensitiveContains(filter) }
        let spent = list.reduce(0) { $0 + $1.timeSpent(model.now) }
        List(selection: selection) {
            Section {
                Picker("Show", selection: $tab) {
                    Text("Done (\(model.finishedRoots.count))").tag(Tab.done)
                    Text("Archive (\(model.archivedRoots.count))").tag(Tab.archive)
                }
                .pickerStyle(.segmented)
                .labelsHidden()
                TextField("Find a finished task", text: $filter)
                    .autocorrectionDisabled()
                if !model.searchText.isEmpty { SearchFilterBar(model: model) }
                if !list.isEmpty {
                    Text("\(list.count) task\(list.count == 1 ? "" : "s")\(spent >= 60 ? " · \(Presentation.duration(spent)) spent" : "")")
                        .font(.caption).foregroundStyle(.secondary)
                }
            }
            if list.isEmpty {
                ContentUnavailableView(filter.isEmpty ? (tab == .done ? "Nothing finished yet" : "Nothing archived") : "Nothing matches",
                                       systemImage: tab == .done ? "checkmark.circle" : "archivebox",
                                       description: Text(filter.isEmpty ? (tab == .done ? "It will show up here, day by day." : "Archive finished tasks to keep Done short.") : ""))
            }
            ForEach(Self.byDay(list, now: model.now), id: \.label) { day in
                Section {
                    ForEach(day.items) { item in row(item).tag(item.id) }
                } header: {
                    Text("\(day.label) · \(day.items.count)")
                }
            }
        }
        .onKeyPress(.space) {
            guard let id = selection.wrappedValue, model.board.find(id) != nil else { return .ignored }
            model.reopen(id)
            return .handled
        }
        .onKeyPress(.return) {
            guard let id = selection.wrappedValue else { return .ignored }
            shell?.open(id)
            return .handled
        }
    }

    private func row(_ item: WorkItem) -> some View {
        HStack(spacing: 10) {
            Image(systemName: tab == .done ? "checkmark.circle.fill" : "archivebox.fill")
                .foregroundStyle(tab == .done ? Color.green : .secondary)
            VStack(alignment: .leading, spacing: 2) {
                Text(Presentation.titleWithIcon(item)).lineLimit(2)
                HStack(spacing: 6) {
                    Circle().fill(Color(hex: model.groupColor(item))).frame(width: 7, height: 7)
                    Text(model.groups.first { $0.id == item.groupId }?.name ?? "")
                    if !item.children.isEmpty {
                        let steps = item.selfAndDescendants.dropFirst()
                        Text("· \(steps.filter(\.isDone).count)/\(steps.count) steps")
                    }
                    let spent = item.timeSpent(model.now)
                    if spent >= 60 { Text("· ⏱ \(Presentation.duration(spent))") }
                    if let done = item.completedAt { Text("· \(done.formatted(date: .omitted, time: .shortened))") }
                }
                .font(.caption).foregroundStyle(.secondary).lineLimit(1)
            }
            Spacer(minLength: 8)
            if tab == .done {
                Button { model.reopen(item.id) } label: { Label("Reopen", systemImage: "arrow.uturn.backward") }
                    .accessibilityLabel("Reopen \(item.title)")
                Button { model.archive(item.id) } label: { Label("Archive", systemImage: "archivebox") }
                    .accessibilityLabel("Archive \(item.title)")
            } else {
                Button { model.unarchive(item.id) } label: { Label("Unarchive", systemImage: "tray.and.arrow.up") }
                    .accessibilityLabel("Unarchive \(item.title)")
            }
        }
        .buttonStyle(.borderless)
        .labelStyle(AdaptiveLabelStyle())
        .contentShape(Rectangle())
        .onTapGesture(count: 2) { shell?.open(item.id) }
        .contextMenu { TaskContextMenu(model: model, id: item.id) }
    }

    /// Finished tasks by the day they were finished: Today, Yesterday, then the date.
    static func byDay(_ items: [WorkItem], now: Date, calendar: Calendar = .current) -> [(label: String, items: [WorkItem])] {
        var days: [(label: String, items: [WorkItem])] = []
        for item in items {
            let at = item.completedAt ?? item.archivedAt ?? now
            let label: String
            if calendar.isDate(at, inSameDayAs: now) {
                label = "Today"
            } else if let yesterday = calendar.date(byAdding: .day, value: -1, to: now), calendar.isDate(at, inSameDayAs: yesterday) {
                label = "Yesterday"
            } else {
                label = at.formatted(.dateTime.weekday(.wide).day().month(.wide))
            }
            if days.last?.label == label { days[days.count - 1].items.append(item) } else { days.append((label, [item])) }
        }
        return days
    }
}

/// Words beside the icon where there's room (iPad, Mac); the icon alone on a narrow screen.
struct AdaptiveLabelStyle: LabelStyle {
    @Environment(\.horizontalSizeClass) private var sizeClass

    func makeBody(configuration: Configuration) -> some View {
        if sizeClass == .compact {
            configuration.icon
        } else {
            Label { configuration.title } icon: { configuration.icon }
        }
    }
}
#endif
