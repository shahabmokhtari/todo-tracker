import SwiftUI

/// Light, dark, or as the system is: the same choice as the Windows and web apps offer.
public enum AppTheme: String, CaseIterable, Identifiable, Sendable {
    case system, light, dark

    /// Where the choice is kept (UserDefaults, read with @AppStorage).
    public static let storageKey = "theme"

    public var id: String { rawValue }

    public var label: String {
        switch self {
        case .system: return "System"
        case .light: return "Light"
        case .dark: return "Dark"
        }
    }

    /// nil: follow the system.
    public var colorScheme: ColorScheme? {
        switch self {
        case .system: return nil
        case .light: return .light
        case .dark: return .dark
        }
    }

    /// A stored value; anything unknown (or nothing yet) follows the system.
    public static func named(_ raw: String?) -> AppTheme {
        raw.flatMap(AppTheme.init(rawValue:)) ?? .system
    }
}

/// The appearance button in the header: System, Light or Dark.
public struct ThemeMenu: View {
    @AppStorage(AppTheme.storageKey) private var theme = AppTheme.system.rawValue
    private let onChoose: ((String) -> Void)?

    /// `onChoose`: also tell the others (the server, so every window follows).
    public init(onChoose: ((String) -> Void)? = nil) {
        self.onChoose = onChoose
    }

    public var body: some View {
        Menu {
            // Only a pick made here is passed on (not the theme changing because another window changed it).
            Picker("Appearance", selection: Binding(get: { theme }, set: { chosen in
                theme = chosen
                onChoose?(chosen)
            })) {
                ForEach(AppTheme.allCases) { Text($0.label).tag($0.rawValue) }
            }
            .pickerStyle(.inline)
        } label: {
            Image(systemName: "circle.lefthalf.filled")
        }
        .menuIndicator(.hidden)
        .fixedSize()
        .accessibilityLabel("Appearance")
        .help("Light, dark, or as the system is")
    }
}

extension View {
    /// Applies the stored appearance choice (put it on each window's root view).
    public func followsAppTheme(_ raw: String) -> some View {
        preferredColorScheme(AppTheme.named(raw).colorScheme)
    }
}
