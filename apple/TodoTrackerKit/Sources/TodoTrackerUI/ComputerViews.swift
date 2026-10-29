#if canImport(SwiftUI)
import SwiftUI
import TodoTrackerKit
#if os(iOS) && canImport(VisionKit)
import VisionKit
#endif

/// The calm line above the tasks while the computer can't be reached, or after it disconnected this phone.
struct RemoteBanner: View {
    @ObservedObject var model: BoardModel
    /// Opens Settings, where the phone connects again.
    var reconnect: () -> Void

    var body: some View {
        if let text = model.remoteBanner {
            HStack(alignment: .top, spacing: 10) {
                Image(systemName: model.remote == .revoked ? "iphone.slash" : model.remote == .connecting ? "arrow.triangle.2.circlepath" : "wifi.slash")
                    .foregroundStyle(.secondary)
                VStack(alignment: .leading, spacing: 6) {
                    Text(text).font(.footnote).fixedSize(horizontal: false, vertical: true)
                    if model.remote == .revoked {
                        HStack {
                            Button("Reconnect", action: reconnect).buttonStyle(.borderedProminent).controlSize(.small)
                            Button("OK") { model.disconnectComputer() }.controlSize(.small)
                        }
                    }
                }
                Spacer(minLength: 0)
            }
            .padding(12)
            .background(RoundedRectangle(cornerRadius: 14, style: .continuous).fill(.thinMaterial))
            .accessibilityElement(children: .contain)
            .accessibilityIdentifier("remote-banner")
        }
    }
}

/// Settings › Computer: connect this phone to a computer's tasks, or see which one it uses (and disconnect).
struct ComputerSection: View {
    @ObservedObject var model: BoardModel
    @State private var connecting = false
    @State private var confirmDisconnect = false

    var body: some View {
        Section {
            if model.isRemote, let name = model.remoteComputer {
                LabeledContent("Connected to", value: name)
                    .accessibilityIdentifier("settings-connected-to")
                LabeledContent("As", value: model.phoneName)
                if let text = model.remoteBanner { Text(text).font(.footnote).foregroundStyle(.secondary) }
                Button("Disconnect", role: .destructive) { confirmDisconnect = true }
                    .confirmationDialog("Disconnect from \(name)?", isPresented: $confirmDisconnect, titleVisibility: .visible) {
                        Button("Disconnect", role: .destructive) { model.disconnectComputer() }
                        Button("Cancel", role: .cancel) {}
                    } message: {
                        Text("This phone goes back to its own tasks, just as you left them. Nothing on \(name) changes; to remove this phone there too, open Settings › Phones on it.")
                    }
            } else {
                Button { connecting = true } label: { Label("Connect to a computer…", systemImage: "laptopcomputer.and.iphone") }
                    .accessibilityIdentifier("settings-connect")
            }
        } header: {
            Text("Computer")
        } footer: {
            Text(model.isRemote
                 ? "You’re using the tasks on \(model.remoteComputer ?? "your computer"), over your Wi-Fi or Tailscale. The tasks kept on this phone wait, untouched, until you disconnect."
                 : "Use the tasks on your computer from this phone, over your Wi-Fi or Tailscale. On the computer, open Settings › Phones and connect a phone.")
        }
        .sheet(isPresented: $connecting) { ConnectComputerView(model: model) }
    }
}

/// Scan the QR code from Settings › Phones on the computer, or paste its link.
struct ConnectComputerView: View {
    @ObservedObject var model: BoardModel
    @Environment(\.dismiss) private var dismiss
    @State private var pasted = ""
    @State private var started = false
    /// How the computer lists this phone (prefilled; on a real device the system only says "iPhone" or "iPad").
    @State private var name = ""

    var body: some View {
        NavigationStack {
            Form {
                Section {
                    TextField("This phone’s name", text: $name)
                        .autocorrectionDisabled()
                        .accessibilityIdentifier("connect-name")
                } header: {
                    Text("This phone’s name")
                } footer: {
                    Text("The computer lists this phone by it, and your changes from it say so (for example “Shahab’s iPad”).")
                }
                #if os(iOS) && canImport(VisionKit)
                if QRScanner.canScan {
                    Section {
                        QRScanner { found in
                            guard !model.isPairing, PairingLink.isPairing(found) else { return }
                            connect(found)
                        }
                        .frame(height: 280)
                        .listRowInsets(EdgeInsets())
                        .clipShape(RoundedRectangle(cornerRadius: 12, style: .continuous))
                    } header: {
                        Text("Scan the code")
                    } footer: {
                        Text("On your computer, open Todo Tracker › Settings › Phones and choose Connect a phone. Point the camera at the code it shows.")
                    }
                }
                #endif
                Section {
                    TextField("todotracker://pair?…", text: $pasted, axis: .vertical)
                        .lineLimit(1...4)
                        .autocorrectionDisabled()
                        #if os(iOS)
                        .textInputAutocapitalization(.never)
                        .keyboardType(.URL)
                        #endif
                        .accessibilityIdentifier("connect-link")
                    Button(model.isPairing ? "Connecting…" : "Connect") { connect(pasted) }
                        .disabled(model.isPairing || pasted.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
                } header: {
                    Text(canScan ? "Or paste the link" : "Paste the link")
                } footer: {
                    Text(canScan
                         ? "Under the code, the computer also shows its link to copy. A code works for 10 minutes, once."
                         : "On your computer, open Todo Tracker › Settings › Phones, choose Connect a phone and copy the link under the code. It works for 10 minutes, once.")
                }
                if let error = model.pairingError {
                    Section { Label(error, systemImage: "exclamationmark.triangle").foregroundStyle(.orange) }
                }
            }
            .navigationTitle("Connect to a computer")
            #if os(iOS)
            .navigationBarTitleDisplayMode(.inline)
            #endif
            .toolbar { ToolbarItem(placement: .cancellationAction) { Button("Cancel") { dismiss() } } }
            .onAppear {
                model.pairingError = nil
                if name.isEmpty { name = model.phoneName }
            }
            .onChange(of: model.isRemote) { _, connected in if connected && started { dismiss() } }
        }
    }

    private var canScan: Bool {
        #if os(iOS) && canImport(VisionKit)
        return QRScanner.canScan
        #else
        return false
        #endif
    }

    private func connect(_ link: String) {
        started = true
        model.pair(link: link, phoneName: name)
    }
}

#if os(iOS) && canImport(VisionKit)
/// The camera, reading QR codes (VisionKit's scanner; not on the simulator or very old phones).
struct QRScanner: UIViewControllerRepresentable {
    var found: (String) -> Void

    @MainActor static var canScan: Bool { DataScannerViewController.isSupported && DataScannerViewController.isAvailable }

    func makeUIViewController(context: Context) -> DataScannerViewController {
        let scanner = DataScannerViewController(recognizedDataTypes: [.barcode(symbologies: [.qr])], qualityLevel: .balanced,
                                                isHighlightingEnabled: true)
        scanner.delegate = context.coordinator
        try? scanner.startScanning()
        return scanner
    }

    func updateUIViewController(_ controller: DataScannerViewController, context: Context) {
        context.coordinator.found = found
    }

    static func dismantleUIViewController(_ controller: DataScannerViewController, coordinator: Coordinator) {
        controller.stopScanning()
    }

    func makeCoordinator() -> Coordinator { Coordinator(found: found) }

    final class Coordinator: NSObject, DataScannerViewControllerDelegate {
        var found: (String) -> Void

        init(found: @escaping (String) -> Void) {
            self.found = found
        }

        func dataScanner(_ dataScanner: DataScannerViewController, didAdd addedItems: [RecognizedItem], allItems: [RecognizedItem]) {
            for item in addedItems {
                if case let .barcode(code) = item, let text = code.payloadStringValue { found(text) }
            }
        }
    }
}
#endif
#endif
