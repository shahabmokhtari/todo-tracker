plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
    id("org.jetbrains.kotlin.plugin.compose")
}

android {
    namespace = "dev.todotracker"
    compileSdk = 35

    defaultConfig {
        applicationId = "dev.todotracker"
        minSdk = 26
        targetSdk = 35
        versionCode = 1
        versionName = "1.0.0"
    }

    buildFeatures {
        compose = true
        buildConfig = true
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }

    kotlinOptions {
        jvmTarget = "17"
        allWarningsAsErrors = true
    }

    testOptions {
        unitTests.isReturnDefaultValues = true
    }
}

// Tasks (WorkItem) change in place: a screen given the same task must redraw when the board changes, so a
// composable isn't skipped just because its task is the same object (Kotlin 2's strong skipping would).
// The core tests read the shared cases in tests/fixtures: a change there runs them again.
tasks.withType<Test>().configureEach {
    inputs.dir(rootProject.file("../tests/fixtures")).withPropertyName("sharedFixtures")
}

composeCompiler {
    featureFlags = setOf(org.jetbrains.kotlin.compose.compiler.gradle.ComposeFeatureFlag.StrongSkipping.disabled())
}

dependencies {
    val composeBom = platform("androidx.compose:compose-bom:2024.12.01")
    implementation(composeBom)
    implementation("androidx.compose.material3:material3")
    implementation("androidx.compose.ui:ui")
    implementation("androidx.compose.ui:ui-tooling-preview")
    implementation("androidx.activity:activity-compose:1.9.3")
    implementation("androidx.core:core-ktx:1.13.1")
    implementation("androidx.lifecycle:lifecycle-viewmodel-compose:2.8.7")
    implementation("org.jetbrains.kotlinx:kotlinx-serialization-json:1.7.3")
    // Frontmatter values (YAML nodes only, never typed decoding): the tasks folder format, docs/design/phone-sync.md.
    implementation("org.yaml:snakeyaml:2.3")
    // Scanning a computer's pairing QR code: on the phone, offline, without Google Play services.
    implementation("com.journeyapps:zxing-android-embedded:4.3.0")
    // Connected to a computer, a check every 15 minutes or so with the app closed (reminders added there still ring).
    implementation("androidx.work:work-runtime:2.9.1")
    testImplementation("junit:junit:4.13.2")
}
