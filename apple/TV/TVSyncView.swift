import SwiftUI
import CoreImage
import CoreImage.CIFilterBuiltins
import OneThingKit
import OneThingTV

/// Sync on the TV: the same private GitHub gist your phones and computers sync through. Set up with the remote: scan the
/// code on your phone to make a token, then type or paste it here (a nearby iPhone offers its keyboard).
struct TVSyncView: View {
    @ObservedObject var model: BoardModel
    let isDemo: Bool

    var body: some View {
        if let sync = model.sync {
            TVSyncContent(sync: sync)
        } else if isDemo {
            TVSyncSetup(note: "This is a made-up board for screenshots: nothing here is saved or synced.") { _, _ in }
        } else {
            VStack(alignment: .leading, spacing: 24) {
                Text("Sync").font(.system(size: 64, weight: .bold, design: .rounded))
                Text(model.storeFailure ?? "Sync isn’t available right now. Restart the app to try again.")
                    .font(.system(size: 32))
                    .foregroundStyle(.white.opacity(0.8))
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
        }
    }
}

private struct TVSyncContent: View {
    @ObservedObject var sync: SyncModel

    var body: some View {
        if sync.isOn {
            TVSyncStatus(sync: sync)
        } else {
            TVSyncSetup(note: sync.problem) { token, library in sync.useGist(token: token, library: library) }
        }
    }
}

/// Not set up yet: the steps, a code to scan for the token page, and the token and library fields.
struct TVSyncSetup: View {
    let note: String?
    let connect: (_ token: String, _ library: String) -> Void

    @State private var token = ""
    @State private var library = SyncLibrary.defaultName
    @State private var problem: String?

    var body: some View {
        HStack(alignment: .top, spacing: 80) {
            VStack(alignment: .leading, spacing: 30) {
                Text("Sync with your other devices")
                    .font(.system(size: 60, weight: .bold, design: .rounded))
                Text("Your Apple TV shows the tasks your phones and computers sync through a private GitHub gist. It keeps only a copy, so nothing is lost if tvOS clears it.")
                    .font(.system(size: 28))
                    .foregroundStyle(.white.opacity(0.75))
                    .fixedSize(horizontal: false, vertical: true)
                ForEach(Array(TVPairing.steps.enumerated()), id: \.offset) { index, step in
                    HStack(alignment: .top, spacing: 20) {
                        Text("\(index + 1)")
                            .font(.system(size: 26, weight: .bold))
                            .frame(width: 48, height: 48)
                            .background(Circle().fill(TVBrand.gradient))
                        Text(step).font(.system(size: 28)).fixedSize(horizontal: false, vertical: true)
                    }
                }
                SecureField("GitHub token", text: $token)
                    .textContentType(.password)
                HStack(spacing: 24) {
                    Text("Library").font(.system(size: 28, weight: .semibold)).foregroundStyle(.white.opacity(0.6))
                    TextField("Library", text: $library)
                }
                HStack(spacing: 30) {
                    Button("Connect") {
                        switch TVPairing.check(token: token) {
                        case .ok(let clean):
                            problem = nil
                            connect(clean, library)
                            token = ""
                        case .problem(let why):
                            problem = why
                        }
                    }
                    .font(.system(size: 30, weight: .semibold))
                    if let message = problem ?? note {
                        Text(message).font(.system(size: 26)).foregroundStyle(TVBrand.warning).fixedSize(horizontal: false, vertical: true)
                    }
                }
            }
            .frame(maxWidth: .infinity, alignment: .leading)

            VStack(spacing: 24) {
                TVQRCode(text: SyncBackends.gistTokenPage.absoluteString)
                    .frame(width: 360, height: 360)
                    .padding(28)
                    .background(RoundedRectangle(cornerRadius: 32).fill(.white))
                Text("Scan with your phone to make a token")
                    .font(.system(size: 26, weight: .semibold))
                Text("github.com › Settings › Tokens, with only “gist” ticked")
                    .font(.system(size: 22))
                    .foregroundStyle(.white.opacity(0.6))
                    .multilineTextAlignment(.center)
            }
            .frame(width: 460)
        }
    }
}

/// Set up: where it syncs, when it last did, the other devices, and Sync now / Turn off.
struct TVSyncStatus: View {
    @ObservedObject var sync: SyncModel
    @State private var turningOff = false

    var body: some View {
        HStack(alignment: .top, spacing: 80) {
            VStack(alignment: .leading, spacing: 28) {
                Text("Syncing").font(.system(size: 60, weight: .bold, design: .rounded))
                line("Through", sync.placeName ?? "A private GitHub gist")
                line("Library", sync.settings?.library ?? SyncLibrary.defaultName)
                line("Last synced", sync.status.lastSync.map { RelativeTime.format($0.date, now: Date()) } ?? (sync.isSyncing ? "Syncing now…" : "Not yet"))
                if let problem = sync.problem ?? sync.status.lastError ?? sync.waiting {
                    Text(problem).font(.system(size: 26)).foregroundStyle(TVBrand.warning).fixedSize(horizontal: false, vertical: true)
                }
                Text("It syncs when you open the app, a few seconds after a change and every minute while it’s open.")
                    .font(.system(size: 24))
                    .foregroundStyle(.white.opacity(0.55))
                    .fixedSize(horizontal: false, vertical: true)
                HStack(spacing: 30) {
                    Button(sync.isSyncing ? "Syncing…" : "Sync now") { sync.syncNow() }
                        .disabled(sync.isSyncing)
                    Button("Turn off sync") { turningOff = true }
                }
                .font(.system(size: 30, weight: .semibold))
            }
            .frame(maxWidth: .infinity, alignment: .leading)

            VStack(alignment: .leading, spacing: 20) {
                Text("OTHER DEVICES").font(.system(size: 22, weight: .bold)).tracking(3).foregroundStyle(.white.opacity(0.5))
                if sync.status.devices.isEmpty {
                    Text("None yet").font(.system(size: 28)).foregroundStyle(.white.opacity(0.5))
                }
                ForEach(sync.status.devices, id: \.device) { device in
                    VStack(alignment: .leading, spacing: 4) {
                        Text(device.name).font(.system(size: 30, weight: .medium))
                        Text("Synced \(RelativeTime.format(device.at.date, now: Date()))").font(.system(size: 22)).foregroundStyle(.white.opacity(0.5))
                    }
                }
            }
            .frame(width: 560, alignment: .leading)
        }
        .alert("Turn off sync on this Apple TV?", isPresented: $turningOff) {
            Button("Turn off", role: .destructive) { sync.turnOff() }
            Button("Cancel", role: .cancel) {}
        } message: {
            Text("Your tasks stay on your other devices. The token is forgotten here.")
        }
    }

    private func line(_ label: String, _ value: String) -> some View {
        HStack(alignment: .firstTextBaseline, spacing: 20) {
            Text(label).foregroundStyle(.white.opacity(0.55)).frame(width: 240, alignment: .leading)
            Text(value)
        }
        .font(.system(size: 30))
    }
}

/// A QR code, drawn crisp at any size.
struct TVQRCode: View {
    let text: String

    var body: some View {
        if let image = Self.image(text) {
            Image(decorative: image, scale: 1).interpolation(.none).resizable().scaledToFit()
        } else {
            Text(text).font(.system(size: 18)).foregroundStyle(.black)
        }
    }

    static func image(_ text: String) -> CGImage? {
        let filter = CIFilter.qrCodeGenerator()
        filter.message = Data(text.utf8)
        filter.correctionLevel = "M"
        guard let output = filter.outputImage else { return nil }
        return CIContext().createCGImage(output, from: output.extent)
    }
}
