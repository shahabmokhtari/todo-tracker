import Foundation
#if canImport(Security)
import Security
#endif

/// A secret kept safe on the device (the Keychain in the app, memory in tests): a sync backend's token.
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
/// Secrets in the Keychain: this device only, readable once the phone was unlocked after starting (so the app can sync
/// in the background), never in backups to another device.
public final class KeychainSecretStore: SecretStore {
    private let service: String

    /// The service name stays what it was, so tokens saved by earlier versions are still found.
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
