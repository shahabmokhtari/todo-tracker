plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
    id("org.jetbrains.kotlin.plugin.compose")
}

// The version: the Gradle property onethingVersionName or the environment variable ONETHING_VERSION_NAME (release.yml
// passes the tag; a leading "v" is dropped), else 2.0.0 with version code 1 (a local or pull-request build).
// The version code Google Play orders updates by comes from it: major * 10000 + minor * 100 + patch, so 2.0.0 is
// 20000 and 2.1.3 is 20103; a pre-release suffix (2.1.0-beta.1) stays in the name and is left out of the code.
val versionFromRelease = (project.findProperty("onethingVersionName") ?: System.getenv("ONETHING_VERSION_NAME"))
    ?.toString()?.trim()?.removePrefix("v")?.removePrefix("V")?.takeIf { it.isNotEmpty() }

/** The version code of a version name: major * 10000 + minor * 100 + patch (minor and patch under 100). */
fun versionCodeOf(name: String): Int {
    val match = Regex("""^(\d+)\.(\d+)\.(\d+)(?:[-+].*)?$""").matchEntire(name)
        ?: throw GradleException("The version \"$name\" isn't major.minor.patch (like 2.1.0 or 2.1.0-beta.1).")
    val (major, minor, patch) = match.destructured.toList().map { it.toInt() }
    if (minor > 99 || patch > 99) throw GradleException("The version \"$name\" has a minor or patch number over 99, which the version code can't hold.")
    val code = major * 10000 + minor * 100 + patch
    if (code < 1) throw GradleException("The version \"$name\" gives version code 0; Google Play needs 1 or more.")
    return code
}

android {
    namespace = "dev.onething"
    compileSdk = 36

    defaultConfig {
        applicationId = "dev.onething"
        minSdk = 26
        // Google Play: since 31 August 2026 new apps and updates must target Android 16 (API 36) or higher
        // (developer.android.com/google/play/requirements/target-sdk).
        targetSdk = 36
        versionCode = versionFromRelease?.let(::versionCodeOf) ?: 1
        versionName = versionFromRelease ?: "2.0.0"
        // Send feedback: a debug build only logs a note; `gradle assembleDebug -PonethingFeedbackSend=true` sends it
        // for real (to the issues relay). Release builds always send.
        buildConfigField("boolean", "FEEDBACK_SEND", (project.findProperty("onethingFeedbackSend")?.toString() == "true").toString())
    }

    // The release build is signed with the upload key (Play App Signing re-signs it for the store). The key comes from
    // Gradle properties or environment variables, never from the repository: in CI, release.yml writes the keystore
    // from secrets and sets ONETHING_UPLOAD_*. Without them the release build is left unsigned and debug builds are
    // unaffected.
    val upload = listOf("STORE_FILE", "STORE_PASSWORD", "KEY_ALIAS", "KEY_PASSWORD").associateWith { name ->
        (project.findProperty("onethingUpload" + name.split('_').joinToString("") { it.lowercase().replaceFirstChar(Char::uppercase) })
            ?: System.getenv("ONETHING_UPLOAD_$name"))?.toString()?.takeIf { it.isNotBlank() }
    }
    if (upload.values.all { it != null }) {
        signingConfigs {
            create("upload") {
                storeFile = file(upload.getValue("STORE_FILE")!!)
                storePassword = upload.getValue("STORE_PASSWORD")
                keyAlias = upload.getValue("KEY_ALIAS")
                keyPassword = upload.getValue("KEY_PASSWORD")
            }
        }
    }

    buildTypes {
        release {
            signingConfig = signingConfigs.findByName("upload")
        }
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
    // repeatOnLifecycle: the screen's clock and sync run only while the app is on screen.
    implementation("androidx.lifecycle:lifecycle-runtime-ktx:2.8.7")
    implementation("org.jetbrains.kotlinx:kotlinx-serialization-json:1.7.3")
    // Frontmatter values (YAML nodes only, never typed decoding): the tasks folder format, docs/design/phone-sync.md.
    implementation("org.yaml:snakeyaml:2.3")
    // Syncing with the app closed (every 15 minutes or so, and soon after a change from a notification), and the widgets' updates.
    implementation("androidx.work:work-runtime:2.9.1")
    // The home-screen widgets (Now, Today).
    implementation("androidx.glance:glance-appwidget:1.1.1")
    testImplementation("junit:junit:4.13.2")
}
