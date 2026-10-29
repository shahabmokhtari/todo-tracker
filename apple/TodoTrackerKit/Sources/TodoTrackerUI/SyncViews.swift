#if canImport(SwiftUI)
import SwiftUI
import TodoTrackerKit
#if os(iOS)
import UIKit
import UniformTypeIdentifiers
#endif

/// Settings › Sync: the line that opens "Sync with your other devices" (iPhone and iPad).
struct SyncSection: View {
    @ObservedObject var model: BoardModel
    @ObservedObject var sync: SyncModel

    var body: some View {
        Section {
            NavigationLink {
                SyncView(model: model, sync: sync)
            } label: {
                LabeledContent("Sync with your other devices", value: sync.isOn ? (sync.settings?.kind == .gist ? "GitHub gist" : "iCloud Drive") : "Off")
            }
            .disabled(model.isRemote)
            .accessibilityIdentifier("settings-sync")
        } header: {
            Text("Sync")
        } footer: {
            Text(model.isRemote
                 ? "You’re using \(model.remoteComputer ?? "your computer")’s tasks. Disconnect from it to sync on your own instead."
                 : "Your computers and other phones, without a computer running: through iCloud Drive or a private GitHub gist.")
        }
    }
}

/// "Sync with your other devices": set up a place (iCloud Drive's TodoTrackerSync folder, or a gist), then see how it
/// goes: when it last synced, the other devices, problems and changes made on both.
struct SyncView: View {
    @ObservedObject var model: BoardModel
    @ObservedObject var sync: SyncModel
    @State private var library = SyncLibrary.defaultName
    @State private var token = ""
    @State private var picking = false
    @State private var confirmOff = false
    @State private var forgetting: SyncDevice?
    @Environment(\.openURL) private var openURL

    var body: some View {
        Form {
            if sync.isOn { status } else { setup }
        }
        .navigationTitle("Sync")
        #if os(iOS)
        .navigationBarTitleDisplayMode(.inline)
        .sheet(isPresented: $picking) {
            FolderPicker { url in
                picking = false
                if let url { sync.choose(folder: url, library: library) }
            }
            .ignoresSafeArea()
        }
        #endif
        .onAppear { library = sync.settings?.library ?? library }
        .alert("There’s no TodoTrackerSync folder here", isPresented: Binding(get: { sync.pendingFolder != nil }, set: { if !$0 { sync.pendingFolder = nil } })) {
            Button("Create it here") { sync.createPendingFolder(library: library) }
            Button("Cancel", role: .cancel) { sync.pendingFolder = nil }
        } message: {
            Text("Your computers meet in iCloud Drive › Apps › TodoTrackerSync. If they sync through iCloud Drive already, pick that folder instead. Otherwise, make it here: \(sync.pendingFolder.map { "\($0.url.lastPathComponent) › \($0.subpath.replacingOccurrences(of: "/", with: " › "))" } ?? "").")
        }
    }

    // MARK: Not set up

    @ViewBuilder private var setup: some View {
        Section {
            Text(model.modeDescription).font(.footnote)
        }
        Section {
            TextField("Library", text: $library)
                .accessibilityIdentifier("sync-library")
                #if os(iOS)
                .textInputAutocapitalization(.words)
                #endif
        } header: {
            Text("Library")
        } footer: {
            Text("Devices with the same library name sync together. Your computers use the name of their tasks folder (Todo Tracker unless you changed it).")
        }
        Section {
            Button { picking = true } label: { Label("Choose the folder in iCloud Drive…", systemImage: "icloud") }
                .accessibilityIdentifier("sync-icloud")
        } header: {
            Text("iCloud Drive")
        } footer: {
            Text("Pick iCloud Drive › Apps › TodoTrackerSync, the folder your Mac and Windows computers use. If it isn’t there yet, pick iCloud Drive and choose Create it here.")
        }
        Section {
            SecureField("GitHub token", text: $token)
                .accessibilityIdentifier("sync-token")
                #if os(iOS)
                .textInputAutocapitalization(.never)
                #endif
                .autocorrectionDisabled()
            Button("Make a token on GitHub…") { openURL(SyncModel.tokenPage) }
            Button("Sync through a gist") {
                sync.useGist(token: token, library: library)
                token = ""
            }
            .disabled(token.trimmingCharacters(in: .whitespaces).isEmpty)
            .accessibilityIdentifier("sync-gist")
        } header: {
            Text("A private GitHub gist")
        } footer: {
            Text("Make a token that can only use gists (the gist scope), copy it and paste it here. It stays in this \(model.phone?.deviceKind ?? "device")’s Keychain. On your computers, turn on GitHub gist sync too.")
        }
        if let problem = sync.problem {
            Section { Text(problem).foregroundStyle(.red).font(.footnote) }
        }
    }

    // MARK: Syncing

    @ViewBuilder private var status: some View {
        Section {
            LabeledContent("Syncing through", value: sync.placeName ?? "")
            LabeledContent("Last synced", value: sync.status.lastSync.map { Self.relative($0.date) } ?? "Not yet")
                .accessibilityIdentifier("sync-last")
            Button {
                sync.syncNow()
            } label: {
                HStack {
                    Text("Sync now")
                    if sync.isSyncing {
                        Spacer()
                        ProgressView()
                    }
                }
            }
            .disabled(sync.isSyncing)
            .accessibilityIdentifier("sync-now")
        } footer: {
            Text("It syncs when you open the app, a few seconds after a change, every minute while it’s open, and now and then in the background.")
        }

        if let problem = sync.problem ?? sync.status.lastError ?? sync.waiting {
            Section("Problems") {
                Text(problem).font(.footnote).accessibilityIdentifier("sync-problem")
            }
        }

        Section {
            if sync.status.devices.isEmpty {
                Text("None yet").foregroundStyle(.secondary)
            }
            ForEach(sync.status.devices, id: \.device) { device in
                HStack {
                    VStack(alignment: .leading) {
                        Text(device.name)
                        Text("Synced \(Self.relative(device.at.date))").font(.caption).foregroundStyle(.secondary)
                    }
                    Spacer()
                    Button("Forget") { forgetting = device }.buttonStyle(.borderless)
                }
            }
        } header: {
            Text("Other devices")
        } footer: {
            Text("Forget a device you no longer use: its changes aren’t merged any more.")
        }
        .confirmationDialog("Forget \(forgetting?.name ?? "")?", isPresented: Binding(get: { forgetting != nil }, set: { if !$0 { forgetting = nil } }), titleVisibility: .visible) {
            Button("Forget", role: .destructive) {
                if let device = forgetting { sync.forget(device.device) }
                forgetting = nil
            }
        } message: {
            Text("Its tasks that are already here stay. If it syncs again, it comes back.")
        }

        if !sync.status.conflicts.isEmpty {
            Section {
                ForEach(sync.status.conflicts, id: \.key) { conflict in
                    VStack(alignment: .leading) {
                        Text(Self.title(conflict.path))
                        Text("Changed on both: here and on \(conflict.peerName)").font(.caption).foregroundStyle(.secondary)
                    }
                }
            } header: {
                Text("Changed on both")
            } footer: {
                Text("Both versions are kept in the task. Open it on a computer to choose one.")
            }
        }

        Section {
            TextField("Library", text: $library)
                .onSubmit { sync.setLibrary(library) }
                #if os(iOS)
                .textInputAutocapitalization(.words)
                #endif
        } header: {
            Text("Library")
        } footer: {
            Text("Devices with the same library name sync together. Change it to join another set of tasks (yours here stay).")
        }

        Section {
            Button("Turn sync off", role: .destructive) { confirmOff = true }
                .accessibilityIdentifier("sync-off")
                .confirmationDialog("Turn sync off?", isPresented: $confirmOff, titleVisibility: .visible) {
                    Button("Turn sync off", role: .destructive) { sync.turnOff() }
                } message: {
                    Text("Your tasks stay on this \(model.phone?.deviceKind ?? "device") as they are. Your other devices keep theirs.")
                }
        }
    }

    private static func relative(_ date: Date) -> String {
        if Date().timeIntervalSince(date) < 60 { return "Just now" }
        let formatter = RelativeDateTimeFormatter()
        formatter.unitsStyle = .full
        return formatter.localizedString(for: date, relativeTo: Date())
    }

    /// "Work/Plan the trip.md" → "Plan the trip".
    private static func title(_ path: String) -> String {
        let name = (path as NSString).lastPathComponent
        return (name as NSString).deletingPathExtension
    }
}

#if os(iOS)
/// The Files app's folder picker (iCloud Drive, On My iPhone, other providers): the folder comes back security-scoped.
struct FolderPicker: UIViewControllerRepresentable {
    var picked: (URL?) -> Void

    func makeUIViewController(context: Context) -> UIDocumentPickerViewController {
        let picker = UIDocumentPickerViewController(forOpeningContentTypes: [.folder])
        picker.allowsMultipleSelection = false
        picker.delegate = context.coordinator
        return picker
    }

    func updateUIViewController(_ controller: UIDocumentPickerViewController, context: Context) {}

    func makeCoordinator() -> Coordinator { Coordinator(picked) }

    final class Coordinator: NSObject, UIDocumentPickerDelegate {
        let picked: (URL?) -> Void
        init(_ picked: @escaping (URL?) -> Void) { self.picked = picked }

        func documentPicker(_ controller: UIDocumentPickerViewController, didPickDocumentsAt urls: [URL]) { picked(urls.first) }

        func documentPickerWasCancelled(_ controller: UIDocumentPickerViewController) { picked(nil) }
    }
}
#endif
#endif
