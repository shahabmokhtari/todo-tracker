import SwiftUI
import OneThingTV

/// The Apple TV's look: dark and calm, the brand gradient (indigo → violet → pink) where the eye should go, and type
/// big enough to read from the sofa.
enum TVBrand {
    static let indigo = Color(tvHex: "#4f46e5")!
    static let violet = Color(tvHex: "#7c3aed")!
    static let pink = Color(tvHex: "#db2777")!
    static let night = Color(tvHex: "#0b0a1f")!
    static let deep = Color(tvHex: "#1e1b4b")!
    static let warning = Color(tvHex: "#fbbf24")!
    static let danger = Color(tvHex: "#fb7185")!

    static let gradient = LinearGradient(colors: [indigo, violet, pink], startPoint: .topLeading, endPoint: .bottomTrailing)
    /// A card's quiet fill on the dark background.
    static let card = Color.white.opacity(0.08)
    static let hairline = Color.white.opacity(0.12)
}

extension Color {
    /// A `#rrggbb` color (a group's), nil when it isn't one.
    init?(tvHex hex: String?) {
        guard var text = hex?.trimmingCharacters(in: .whitespaces), text.hasPrefix("#") else { return nil }
        text.removeFirst()
        guard text.count == 6, let value = UInt32(text, radix: 16) else { return nil }
        self.init(red: Double((value >> 16) & 0xff) / 255, green: Double((value >> 8) & 0xff) / 255, blue: Double(value & 0xff) / 255)
    }
}

/// The background of every screen: the night color with two soft brand glows, edge to edge (behind the safe area).
struct TVBackdrop: View {
    var body: some View {
        ZStack {
            TVBrand.night
            RadialGradient(colors: [TVBrand.indigo.opacity(0.45), .clear], center: .topLeading, startRadius: 0, endRadius: 1100)
            RadialGradient(colors: [TVBrand.pink.opacity(0.22), .clear], center: .bottomTrailing, startRadius: 0, endRadius: 1000)
        }
        .ignoresSafeArea()
    }
}

/// A short colored label ("Due in 3h", "Overdue · 2h ago").
struct TVChip: View {
    let text: String
    var systemImage: String?
    var tint: Color = .white

    var body: some View {
        HStack(spacing: 10) {
            if let systemImage { Image(systemName: systemImage) }
            Text(text)
        }
        .font(.system(size: 28, weight: .semibold))
        .foregroundStyle(tint)
        .padding(.horizontal, 22)
        .padding(.vertical, 10)
        .background(Capsule().fill(tint.opacity(0.16)))
    }
}

/// Why something didn't work ("Couldn't save…"), along the bottom for a few seconds. Put it on a screen with
/// `.tvToast($message)`.
struct TVToast: ViewModifier {
    @Binding var message: String?

    func body(content: Content) -> some View {
        content.overlay(alignment: .bottom) {
            if let message {
                Label(message, systemImage: "exclamationmark.triangle.fill")
                    .font(.system(size: 30, weight: .semibold))
                    .foregroundStyle(.white)
                    .multilineTextAlignment(.leading)
                    .padding(.horizontal, 36)
                    .padding(.vertical, 22)
                    .background(RoundedRectangle(cornerRadius: 24).fill(TVBrand.danger.opacity(0.9)))
                    .frame(maxWidth: 1400)
                    .padding(.bottom, 40)
                    .transition(.move(edge: .bottom).combined(with: .opacity))
                    .allowsHitTesting(false)
                    .task(id: message) {
                        try? await Task.sleep(nanoseconds: 6_000_000_000)
                        withAnimation(.easeIn(duration: 0.4)) { self.message = nil }
                    }
            }
        }
        .animation(.spring(duration: 0.4), value: message)
    }
}

extension View {
    func tvToast(_ message: Binding<String?>) -> some View { modifier(TVToast(message: message)) }
}

extension TVWhen {
    var tint: Color { urgent ? TVBrand.danger : TVBrand.warning }
    var symbol: String { urgent ? "exclamationmark.circle.fill" : "calendar" }
}

/// A task on a card: its group's color down the side, its title, when it's due and how far along it is.
struct TVCardView: View {
    let card: TVCard
    var width: CGFloat? = 400
    /// Nil: as tall as its content (at least 160).
    var height: CGFloat?
    var titleSize: CGFloat = 34

    var body: some View {
        HStack(spacing: 0) {
            Rectangle().fill(Color(tvHex: card.groupColor) ?? TVBrand.violet).frame(width: 10)
            VStack(alignment: .leading, spacing: 14) {
                // The group, and for a step the task it belongs to.
                let caption = [card.group?.uppercased(), card.parentTitle].compactMap { $0 }.joined(separator: "  ·  ")
                if !caption.isEmpty {
                    Text(caption).font(.system(size: 20, weight: .bold)).tracking(1.5).foregroundStyle(.white.opacity(0.55)).lineLimit(1)
                }
                Text(card.title)
                    .font(.system(size: titleSize, weight: .semibold))
                    .foregroundStyle(card.isDone ? .white.opacity(0.6) : .white)
                    .strikethrough(card.isDone, color: .white.opacity(0.4))
                    .lineLimit(2)
                    .multilineTextAlignment(.leading)
                    .frame(maxWidth: .infinity, alignment: .leading)
                Spacer(minLength: 0)
                HStack(spacing: 16) {
                    if let when = card.when {
                        Label(when.text, systemImage: when.symbol).foregroundStyle(when.tint)
                    }
                    if let progress = card.progress {
                        Label(progress, systemImage: "checklist").foregroundStyle(.white.opacity(0.6))
                    }
                }
                .font(.system(size: 22, weight: .medium))
                .lineLimit(1)
            }
            .padding(.horizontal, 28)
            .padding(.vertical, 24)
        }
        .frame(width: width, alignment: .leading)
        .frame(minHeight: height ?? 160, maxHeight: height, alignment: .topLeading)
        .background(TVBrand.card)
    }
}
