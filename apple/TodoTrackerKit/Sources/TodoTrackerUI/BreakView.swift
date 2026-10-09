#if canImport(SwiftUI)
import SwiftUI
import TodoTrackerKit

/// The full-screen break after a focus session: what to do, how long it lasts, "I'm taking it", "Start next focus now"
/// and "Skip the break". When the break runs out it turns into "Break's over": "Start next focus" or "Not now".
/// The first moment after it appears is ignored, so a key still being typed can't answer it.
public struct BreakView: View {
    @ObservedObject var model: BoardModel
    @State private var shownAt = Date()

    public init(model: BoardModel) {
        self.model = model
    }

    private var settled: Bool { Date().timeIntervalSince(shownAt) > Breaks.settleTime }

    public var body: some View {
        ZStack {
            Rectangle().fill(.ultraThinMaterial).ignoresSafeArea()
            Color.black.opacity(0.55).ignoresSafeArea()
            if let prompt = model.breakPrompt {
                VStack(spacing: 16) {
                    Text(prompt.isOver ? "🎯" : "☕").font(.system(size: 48)).accessibilityHidden(true)
                    Text(prompt.title).font(.largeTitle.bold()).multilineTextAlignment(.center)
                    Text(prompt.tip).font(.title3).foregroundStyle(.white.opacity(0.8)).multilineTextAlignment(.center)
                    if !prompt.isOver {
                        Text(Breaks.clock(until: prompt.until, now: model.now))
                            .font(.system(size: 76, weight: .semibold, design: .rounded))
                            .monospacedDigit()
                            .accessibilityHint("Break time left")
                        Text("Your focus session is done. Step away; the timer tells you when to come back.")
                            .font(.callout).foregroundStyle(.white.opacity(0.75)).multilineTextAlignment(.center)
                    }
                    if let next = model.nextFocusTitle {
                        (Text("Next: ").foregroundStyle(.white.opacity(0.75)) + Text(next).bold())
                            .multilineTextAlignment(.center)
                    }
                    ViewThatFits {
                        HStack(spacing: 12) { buttons(prompt) }
                        VStack(spacing: 10) { buttons(prompt) }
                    }
                    .controlSize(.large)
                    .padding(.top, 8)
                }
                .foregroundStyle(.white)
                .padding(40)
                .frame(maxWidth: 620)
            }
        }
        .onAppear { shownAt = Date() }
        // The break turned into "Break's over" under the person: a click or key in that moment isn't an answer.
        .onChange(of: model.breakPrompt?.isOver) { _, _ in shownAt = Date() }
        .accessibilityElement(children: .contain)
        .accessibilityLabel(model.breakPrompt?.title ?? "Time for a break")
    }

    @ViewBuilder
    private func buttons(_ prompt: BreakPrompt) -> some View {
        if prompt.isOver {
            Button { if settled { model.startNextFocus() } } label: { Label("Start next focus", systemImage: "play.fill") }
                .buttonStyle(.borderedProminent)
                .keyboardShortcut(.defaultAction)
            Button("Not now") { if settled { model.takeBreak() } }
                .buttonStyle(.bordered)
                .keyboardShortcut(.cancelAction)
        } else {
            Button("I’m taking it") { if settled { model.takeBreak() } }
                .buttonStyle(.borderedProminent)
                .keyboardShortcut(.cancelAction)
            Button { if settled { model.startNextFocus() } } label: { Label("Start next focus now", systemImage: "play.fill") }
                .buttonStyle(.bordered)
            Button("Skip the break") { if settled { model.skipBreak() } }
                .buttonStyle(.bordered)
        }
    }
}
#endif
