# Familiar — Releasing

A version tag publishes everything: the Docker images (`familiar-server`, `familiar-client`,
`familiar-backup`, tagged with the version) and a GitHub release with the Android APK, signed with
the release key, to sideload (MOBILE.md §8: no Play Store for now).

```bash
git tag v0.2.0
git push origin v0.2.0
```

CI tests everything first; the release appears under the repository's Releases once the
`android-release` job finishes. A tag with a suffix (`v0.2.0-rc.1`) is published as a pre-release.
Re-running the job replaces the APK on an existing release.

The app's version comes from the tag: `versionName` 0.2.0, `versionCode` 2000 (major × 1,000,000 +
minor × 1,000 + patch), so every later tag installs over the last as an update. Builds from `main`
are debug builds (`familiar-android-debug` on each CI run), version 0.1.0.

## The release key (once)

Android only installs an update signed with the same key as the installed app, so this key is the
app's identity: **keep a copy somewhere safe** (a password manager, the NAS backup). Lose it and the
next release can't update installed phones; they'd have to uninstall it (and sign in again).

1. Make the key (JDK `keytool`; it asks for a password - use the same one for the store and key):

   ```bash
   keytool -genkeypair -keystore familiar-release.jks -storetype PKCS12 -alias familiar -keyalg RSA -keysize 4096 -validity 10000 -dname "CN=Familiar"
   ```

2. Give it to GitHub as four repository secrets (each `gh secret set` prompts for its value):

   ```bash
   base64 -w0 familiar-release.jks | gh secret set ANDROID_KEYSTORE_BASE64
   ```

   ```bash
   gh secret set ANDROID_KEYSTORE_PASSWORD
   ```

   ```bash
   gh secret set ANDROID_KEY_PASSWORD
   ```

   ```bash
   gh secret set ANDROID_KEY_ALIAS --body familiar
   ```

3. Put `familiar-release.jks` somewhere safe and delete the working copy. It isn't needed locally;
   only CI signs.

Without the secrets, a tag still publishes the images, and the `android-release` job fails and says
which secrets are missing.

## Installing a release

Download `familiar-<version>.apk` from the release on the phone and open it (allow installing from
the browser or Files the first time). A phone with a **debug build** (from CI or Android Studio) has
to uninstall it once first, because the keys differ; after that, each release installs over the
last and keeps the sign-in.
