import org.gradle.api.file.FileSystemOperations
import javax.inject.Inject

plugins {
    alias(libs.plugins.android.application)
    alias(libs.plugins.compose.compiler)
}

android {
    namespace = "app.familiar.android"
    compileSdk = libs.versions.android.compile.sdk.get().toInt()

    defaultConfig {
        applicationId = "app.familiar"
        minSdk = libs.versions.android.min.sdk.get().toInt()
        targetSdk = libs.versions.android.target.sdk.get().toInt()
        versionCode = 1
        versionName = "0.1.0"

        // Phones (arm64) and the emulator (x86_64): ONNX Runtime's native code is a few MB per ABI.
        ndk { abiFilters += listOf("arm64-v8a", "x86_64") }
    }

    buildFeatures {
        compose = true
    }

    buildTypes {
        release {
            isMinifyEnabled = false
        }
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }
}

// The avatar page (client/avatar.html, docs/MOBILE.md §1.1) is built from the web client's avatar
// code and bundled as assets/avatar/: the app renders it offline and never loads it from a server.
// Hands-free's voice-activity model is the web client's too, bundled as assets/vad/.
val client: Directory = rootProject.layout.projectDirectory.dir("../client")

val buildAvatarPage = tasks.register<Exec>("buildAvatarPage") {
    description = "Builds the avatar page with the web client's toolchain (needs npm ci in client/)."
    workingDir = client.asFile
    val npm = if (System.getProperty("os.name").startsWith("Windows")) listOf("cmd", "/c", "npm") else listOf("npm")
    commandLine(npm + listOf("run", "build:embed"))
    inputs.dir(client.dir("src/avatar"))
    inputs.dir(client.dir("src/embed"))
    inputs.files(client.file("avatar.html"), client.file("vite.embed.config.ts"), client.file("package-lock.json"))
    outputs.dir(client.dir("dist-embed"))
}

abstract class AvatarAssets : DefaultTask() {
    @get:InputDirectory
    abstract val page: DirectoryProperty

    @get:InputFile
    abstract val vad: RegularFileProperty

    @get:OutputDirectory
    abstract val output: DirectoryProperty

    @get:Inject
    abstract val files: FileSystemOperations

    @TaskAction
    fun copy() {
        files.sync {
            into(output)
            from(page) { into("avatar") }
            from(vad) { into("vad") }
        }
    }
}

val avatarAssets = tasks.register<AvatarAssets>("avatarAssets") {
    dependsOn(buildAvatarPage)
    page.set(client.dir("dist-embed"))
    // Straight from the package (npm ci in client/), as the web client's copy script takes it.
    vad.set(client.file("node_modules/@ricky0123/vad-web/dist/silero_vad_v5.onnx"))
}

androidComponents {
    onVariants { variant ->
        variant.sources.assets?.addGeneratedSourceDirectory(avatarAssets, AvatarAssets::output)
    }
}

dependencies {
    implementation(project(":shared"))
    implementation(libs.androidx.activity.compose)
    // Push through ntfy (UnifiedPush), docs/MOBILE.md §8.
    implementation(libs.unifiedpush.connector)
    implementation(libs.coroutines.core)
    implementation(libs.compose.runtime)
    implementation(libs.compose.ui)
    implementation(libs.compose.material3)
}
