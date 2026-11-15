import SwiftUI
import OneThingKit
import OneThingTV

/// The living-room screen: the one thing to do now in huge type, the focus timer and time tracker when they run, a
/// short Up next row and what got done today. Select on the task marks it done (after asking); Play/Pause starts,
/// pauses and resumes the focus timer.
struct TVNowView: View {
    @ObservedObject var model: BoardModel
    let needsSetup: Bool
    let openSync: () -> Void

    @State private var confirming: TVHero?
    @State private var open: TVOpenTask?
    @State private var celebration: String?
    /// The task to do now has the focus when the screen opens: Select marks it done (after asking).
    @FocusState private var heroFocused: Bool

    var body: some View {
        let screen = TVScreen.build(model.board, now: model.now)
        ZStack {
            VStack(alignment: .leading, spacing: 28) {
                header(screen)
                HStack(alignment: .center, spacing: 64) {
                    hero(screen)
                        .frame(maxWidth: .infinity, alignment: .leading)
                    if let focus = screen.focus {
                        TVFocusDial(clock: focus)
                            .transition(.scale.combined(with: .opacity))
                    }
                }
                Spacer(minLength: 0)
                upNext(screen)
            }
            if let celebration {
                TVCelebrationView(text: celebration)
                    .transition(.opacity)
                    .allowsHitTesting(false)
            }
        }
        .animation(.spring(duration: 0.5), value: screen.focus != nil)
        .defaultFocus($heroFocused, true)
        .onPlayPauseCommand { playPause(screen) }
        .onChange(of: screen.doneToday) { old, new in
            guard TVCelebration.shouldCelebrate(from: old, to: new) else { return }
            celebrate(screen.cheer ?? "Done!")
        }
        .alert("Mark “\(confirming?.title ?? "")” done?", isPresented: Binding(get: { confirming != nil }, set: { if !$0 { confirming = nil } })) {
            Button("Mark done") {
                if let hero = confirming { model.complete(hero.id) }
                confirming = nil
            }
            Button("Not yet", role: .cancel) { confirming = nil }
        } message: {
            Text("It moves to Done, and the next thing comes up.")
        }
        .fullScreenCover(item: $open) { task in
            TVDetailsView(model: model, id: task.id, canMarkDone: true)
        }
    }

    // MARK: Parts

    private func header(_ screen: TVScreen) -> some View {
        HStack(alignment: .center, spacing: 24) {
            Text(Presentation.greeting(model.now))
                .font(.system(size: 40, weight: .semibold))
                .foregroundStyle(.white.opacity(0.7))
            if let tracker = screen.tracker {
                TVChip(text: "\(tracker.title) · \(tracker.elapsed)", systemImage: "stopwatch", tint: Color(tvHex: "#a5b4fc")!)
                    .monospacedDigit()
                    .lineLimit(1)
            }
            Spacer()
            TVDoneBadge(count: screen.doneToday)
        }
    }

    @ViewBuilder private func hero(_ screen: TVScreen) -> some View {
        if let hero = screen.hero {
            Button { confirming = hero } label: { TVHeroCard(hero: hero) }
                .buttonStyle(.card)
                .focused($heroFocused)
                .accessibilityHint("Marks it done, after asking")
        } else {
            VStack(alignment: .leading, spacing: 28) {
                Text(needsSetup ? "Your one thing shows up here" : "All clear ✨")
                    .font(.system(size: 96, weight: .bold, design: .rounded))
                Text(needsSetup ? "Set up sync to bring in the tasks from your phone or computer." : screen.emptyMessage)
                    .font(.system(size: 40))
                    .foregroundStyle(.white.opacity(0.7))
                if needsSetup {
                    Button("Set up sync", action: openSync)
                        .font(.system(size: 32, weight: .semibold))
                } else if screen.waitingCount > 0 {
                    Text("\(screen.waitingCount) waiting until later.")
                        .font(.system(size: 32))
                        .foregroundStyle(.white.opacity(0.5))
                }
            }
            .padding(.vertical, 40)
        }
    }

    @ViewBuilder private func timerHint(_ screen: TVScreen) -> some View {
        let text: String = {
            guard let focus = screen.focus else { return "Play/Pause starts a \(model.board.pomodoro.settings.focusMinutes)-minute focus session" }
            return focus.isPaused ? "Play/Pause carries on" : "Play/Pause pauses the timer"
        }()
        if screen.hero != nil || screen.focus != nil {
            Label(text, systemImage: "playpause.fill")
                .font(.system(size: 26, weight: .medium))
                .foregroundStyle(.white.opacity(0.5))
        }
    }

    @ViewBuilder private func upNext(_ screen: TVScreen) -> some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack {
                if !screen.upNext.isEmpty {
                    Text("Up next")
                        .font(.system(size: 30, weight: .bold))
                        .foregroundStyle(.white.opacity(0.6))
                }
                Spacer()
                timerHint(screen)
            }
            if !screen.upNext.isEmpty {
                ScrollView(.horizontal) {
                    LazyHStack(spacing: 40) {
                        ForEach(screen.upNext) { card in
                            Button { open = TVOpenTask(id: card.id) } label: { TVCardView(card: card, height: 160) }
                                .buttonStyle(.card)
                        }
                    }
                    .padding(.vertical, 20)
                    .padding(.horizontal, 8)
                }
                .scrollClipDisabled()
            }
        }
    }

    // MARK: The remote

    private func playPause(_ screen: TVScreen) {
        switch TVRemote.playPause(model.board.pomodoro, hero: screen.hero?.id) {
        case .start(let id): model.startFocus(id)
        case .pause: model.pausePomodoro()
        case .resume: model.resumePomodoro()
        }
    }

    private func celebrate(_ text: String) {
        withAnimation(.easeOut(duration: 0.3)) { celebration = text }
        Task {
            try? await Task.sleep(nanoseconds: 2_800_000_000)
            withAnimation(.easeIn(duration: 0.6)) { celebration = nil }
        }
    }
}

/// The task to do now, as big as it gets: its group, its title and when it's due.
struct TVHeroCard: View {
    let hero: TVHero

    var body: some View {
        VStack(alignment: .leading, spacing: 26) {
            HStack(spacing: 16) {
                Circle().fill(Color(tvHex: hero.groupColor) ?? .white).frame(width: 24, height: 24)
                    .overlay(Circle().stroke(.white.opacity(0.7), lineWidth: 3))
                Text(([hero.group?.uppercased() ?? "DO THIS NOW"] + (hero.step.map { [$0.uppercased()] } ?? [])).joined(separator: "  ·  "))
                    .font(.system(size: 26, weight: .bold))
                    .tracking(3)
                    .foregroundStyle(.white.opacity(0.85))
            }
            if let parent = hero.card.parentTitle {
                Text(parent)
                    .font(.system(size: 34, weight: .medium))
                    .foregroundStyle(.white.opacity(0.75))
                    .lineLimit(1)
            }
            Text(hero.title)
                .font(.system(size: 108, weight: .bold, design: .rounded))
                .foregroundStyle(.white)
                .lineLimit(2)
                .minimumScaleFactor(0.7)
                .multilineTextAlignment(.leading)
                .fixedSize(horizontal: false, vertical: true)
                .frame(maxWidth: .infinity, alignment: .leading)
            if hero.when != nil || hero.reminder != nil {
                HStack(spacing: 18) {
                    if let when = hero.when {
                        TVChip(text: when.text, systemImage: when.symbol, tint: when.urgent ? .white : .white)
                            .background(Capsule().fill(when.urgent ? TVBrand.danger.opacity(0.55) : .white.opacity(0.08)))
                    }
                    if let reminder = hero.reminder {
                        TVChip(text: reminder, systemImage: "bell.fill", tint: .white)
                            .background(Capsule().fill(TVBrand.warning.opacity(0.35)))
                    }
                }
            }
        }
        .padding(.horizontal, 56)
        .padding(.vertical, 40)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(TVBrand.gradient)
    }
}

/// The focus timer: a ring that fills as the session goes, with the time left in big, steady digits.
struct TVFocusDial: View {
    let clock: TVFocusClock

    var body: some View {
        ZStack {
            Circle().stroke(.white.opacity(0.1), lineWidth: 22)
            Circle()
                .trim(from: 0, to: clock.fraction)
                .stroke(clock.isBreak ? AnyShapeStyle(Color(tvHex: "#34d399")!) : AnyShapeStyle(TVBrand.gradient),
                        style: StrokeStyle(lineWidth: 22, lineCap: .round))
                .rotationEffect(.degrees(-90))
                .animation(.linear(duration: 1), value: clock.fraction)
            VStack(spacing: 10) {
                Text(clock.isPaused ? "Paused" : clock.label)
                    .font(.system(size: 30, weight: .semibold))
                    .foregroundStyle(clock.isPaused ? TVBrand.warning : .white.opacity(0.7))
                Text(clock.time)
                    .font(.system(size: 112, weight: .bold, design: .rounded))
                    .monospacedDigit()
                    .contentTransition(.numericText())
                if let task = clock.taskTitle, !clock.isBreak {
                    Text(task)
                        .font(.system(size: 24))
                        .foregroundStyle(.white.opacity(0.55))
                        .lineLimit(1)
                        .frame(maxWidth: 300)
                }
            }
        }
        .frame(width: 440, height: 440)
        .opacity(clock.isPaused ? 0.75 : 1)
    }
}

/// "4 done today" at the top right.
struct TVDoneBadge: View {
    let count: Int

    var body: some View {
        HStack(spacing: 14) {
            Image(systemName: count > 0 ? "checkmark.seal.fill" : "checkmark.seal")
                .foregroundStyle(count > 0 ? AnyShapeStyle(TVBrand.gradient) : AnyShapeStyle(.white.opacity(0.4)))
            Text("\(count)").font(.system(size: 40, weight: .bold, design: .rounded)).monospacedDigit()
            Text("done today").foregroundStyle(.white.opacity(0.6))
        }
        .font(.system(size: 30, weight: .semibold))
        .padding(.horizontal, 28)
        .padding(.vertical, 14)
        .background(Capsule().fill(.white.opacity(0.08)))
        .contentTransition(.numericText())
        .accessibilityElement(children: .combine)
    }
}

/// A burst of brand-colored confetti and a cheer when a task gets done.
struct TVCelebrationView: View {
    let text: String
    @State private var burst = false

    private static let colors: [Color] = [TVBrand.indigo, TVBrand.violet, TVBrand.pink, Color(tvHex: "#fbbf24")!, Color(tvHex: "#34d399")!, .white]

    var body: some View {
        ZStack {
            Color.black.opacity(0.35).ignoresSafeArea()
            ForEach(0..<48, id: \.self) { i in
                let angle = Double(i) / 48 * 2 * .pi + Double(i % 5) * 0.11
                let distance = 380.0 + Double((i * 37) % 260)
                RoundedRectangle(cornerRadius: 4)
                    .fill(Self.colors[i % Self.colors.count])
                    .frame(width: i % 3 == 0 ? 14 : 22, height: i % 3 == 0 ? 30 : 14)
                    .rotationEffect(.degrees(burst ? Double(i * 47) : 0))
                    .offset(x: burst ? cos(angle) * distance : 0, y: burst ? sin(angle) * distance * 0.7 + 120 : 0)
                    .opacity(burst ? 0 : 1)
            }
            VStack(spacing: 18) {
                Text("🎉").font(.system(size: 140))
                Text("Done!").font(.system(size: 96, weight: .heavy, design: .rounded))
                Text(text).font(.system(size: 40, weight: .medium)).foregroundStyle(.white.opacity(0.8))
            }
            .scaleEffect(burst ? 1 : 0.7)
        }
        .onAppear {
            withAnimation(.easeOut(duration: 1.8)) { burst = true }
        }
    }
}
