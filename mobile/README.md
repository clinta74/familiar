# Familiar for Android (and later iOS)

Kotlin Multiplatform and Compose Multiplatform; the plan is [docs/MOBILE.md](../docs/MOBILE.md).

```
shared/       protocol, API client (REST + SSE with a bearer token), conversation state, the Compose UI
androidApp/   the Android shell: activity, manifest, icon, network security config
```

## Build and install

Needs JDK 17+ and the Android SDK with platform 37 (Android Studio installs both). Point
`local.properties` at the SDK (`sdk.dir=C:/Users/you/AppData/Local/Android/Sdk`), then:

```
./gradlew :shared:testAndroidHostTest      # shared tests on the JVM
./gradlew :androidApp:installDebug         # build and install on a connected phone
```

CI builds the same debug APK on every push (the `familiar-android-debug` artifact) for sideloading.

## Pointing the app at a server

The app asks for the server address and signs in with a username and password; the phone gets its
own token, listed (and revocable) in the web app's Settings › Devices.

- **The NAS over the VPN:** `https://<nas>:7280`. Its certificate is self-signed, so install it on
  the phone first (Settings › Security › Encryption & credentials › Install a certificate › CA
  certificate); the app trusts user-installed certificates, and nothing else unusual.
- **The dev server on this PC, phone on USB:** `adb reverse tcp:5280 tcp:5280`, then
  `http://127.0.0.1:5280` in the app.
- **An emulator:** `http://10.0.2.2:5280` reaches the PC's dev server.

Plain `http` is allowed (home network or VPN only); the sign-in screen says when a connection isn't
encrypted.
