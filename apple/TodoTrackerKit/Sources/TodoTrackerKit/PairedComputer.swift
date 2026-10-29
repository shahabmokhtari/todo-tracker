import Foundation
#if canImport(Security)
import Security
#endif

/// A secret kept safe on the device (the Keychain in the app, memory in tests).
public protocol SecretStore: AnyObject {
    func read(_ key: String) -> String?
    @discardableResult func write(_ key: String, _ value: String) -> Bool
    func remove(_ key: String)
}

/// Secrets in memory only (tests, and previews).
public final class InMemorySecretStore: SecretStore {
    private var values: [String: String] = [:]

    public init() {}

    public func read(_ key: String) -> String? { values[key] }

    @discardableResult public func write(_ key: String, _ value: String) -> Bool {
        values[key] = value
        return true
    }

    public func remove(_ key: String) { values[key] = nil }
}

#if canImport(Security)
/// Secrets in the Keychain: this device only, readable once the phone was unlocked after starting (so the app can
/// reconnect in the background), never in backups to another device.
public final class KeychainSecretStore: SecretStore {
    private let service: String

    public init(service: String = "com.todotracker.phone") {
        self.service = service
    }

    private func query(_ key: String) -> [String: Any] {
        [kSecClass as String: kSecClassGenericPassword, kSecAttrService as String: service, kSecAttrAccount as String: key]
    }

    public func read(_ key: String) -> String? {
        var q = query(key)
        q[kSecReturnData as String] = true
        q[kSecMatchLimit as String] = kSecMatchLimitOne
        var result: AnyObject?
        guard SecItemCopyMatching(q as CFDictionary, &result) == errSecSuccess, let data = result as? Data else { return nil }
        return String(data: data, encoding: .utf8)
    }

    /// Kept on this device only: never in a backup restored onto another device (a token there would be a second
    /// device posing as this one), readable after the first unlock (background syncs).
    public static let accessibility = kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly

    @discardableResult public func write(_ key: String, _ value: String) -> Bool {
        let data = Data(value.utf8)
        // An update sets how it's kept too (an item from an older version is moved to this device only).
        let update = SecItemUpdate(query(key) as CFDictionary, [kSecValueData as String: data, kSecAttrAccessible as String: Self.accessibility] as CFDictionary)
        if update == errSecSuccess { return true }
        var add = query(key)
        add[kSecValueData as String] = data
        add[kSecAttrAccessible as String] = Self.accessibility
        return SecItemAdd(add as CFDictionary, nil) == errSecSuccess
    }

    public func remove(_ key: String) {
        SecItemDelete(query(key) as CFDictionary)
    }
}
#endif

/// The computer this phone is connected to: its addresses, certificate and name (settings), and the phone's token
/// (a secret). Nil when the phone uses the tasks kept on it.
public struct PairedComputer: Equatable, Sendable {
    public var hosts: [String]
    public var fingerprint: String
    public var computer: String
    /// The address that answered last (tried first next time).
    public var lastHost: String?
    public var token: String

    public init(hosts: [String], fingerprint: String, computer: String, lastHost: String? = nil, token: String) {
        self.hosts = hosts
        self.fingerprint = fingerprint
        self.computer = computer
        self.lastHost = lastHost
        self.token = token
    }
}

/// Keeps the pairing: the token in the secret store, the rest in UserDefaults.
public final class PairingStore {
    private let defaults: UserDefaults
    private let secrets: SecretStore
    static let tokenKey = "phone-token"
    static let settingsKey = "pairedComputer"
    static let lastReadKey = "pairedComputerLastRead"
    static let phoneNameKey = "phoneName"

    public init(defaults: UserDefaults = .standard, secrets: SecretStore) {
        self.defaults = defaults
        self.secrets = secrets
    }

    /// The pairing, when there is one whole (a token without settings, or settings without a token, isn't one).
    public func load() -> PairedComputer? {
        guard let token = secrets.read(Self.tokenKey), !token.isEmpty,
              let saved = defaults.dictionary(forKey: Self.settingsKey),
              let hosts = saved["hosts"] as? [String], !hosts.isEmpty,
              let fingerprint = saved["fingerprint"] as? String,
              let computer = saved["computer"] as? String else { return nil }
        return PairedComputer(hosts: hosts, fingerprint: fingerprint, computer: computer, lastHost: saved["lastHost"] as? String, token: token)
    }

    @discardableResult
    public func save(_ paired: PairedComputer) -> Bool {
        guard secrets.write(Self.tokenKey, paired.token) else { return false }
        var saved: [String: Any] = ["hosts": paired.hosts, "fingerprint": paired.fingerprint, "computer": paired.computer]
        if let lastHost = paired.lastHost { saved["lastHost"] = lastHost }
        defaults.set(saved, forKey: Self.settingsKey)
        return true
    }

    /// Forgets the computer (the token first, so nothing can use it afterwards).
    public func forget() {
        secrets.remove(Self.tokenKey)
        defaults.removeObject(forKey: Self.settingsKey)
        defaults.removeObject(forKey: Self.lastReadKey)
    }

    /// The name this phone gave itself when it connected ("Shahab's iPad"): offered again next time, so it's kept when
    /// the phone disconnects. Blank: none.
    public var phoneName: String? {
        get { defaults.string(forKey: Self.phoneNameKey).flatMap { $0.isEmpty ? nil : $0 } }
        set {
            let clean = newValue?.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
            if clean.isEmpty { defaults.removeObject(forKey: Self.phoneNameKey) } else { defaults.set(clean, forKey: Self.phoneNameKey) }
        }
    }

    /// When the board was last read from the computer (what the cached copy shows).
    public var lastRead: Date? {
        get { defaults.object(forKey: Self.lastReadKey) as? Date }
        set { defaults.set(newValue, forKey: Self.lastReadKey) }
    }
}
