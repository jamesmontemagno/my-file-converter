# App Privacy, export compliance and age rating

Audited against the Mac App Store build: `Platforms/MacCatalyst/Store.entitlements`, app and Core
sources, bundled tools and `PrivacyInfo.xcprivacy`.

## App Privacy: Data Not Collected

Answer **No, we do not collect data from this app.**

Evidence:

- The Store entitlements are only `app-sandbox`, `files.user-selected.read-write` and
  `files.bookmarks.app-scope`. There is no `network.client` entitlement, so the sandbox blocks
  outbound connections. Bundled FFmpeg/FFprobe get `app-sandbox` and `inherit` only, and the
  FFmpeg build is configured with `--disable-network`.
- No analytics, advertising, crash-reporting or account SDKs. No `HttpClient` use in the app.
- Links (website, support, tool documentation) open in the user's browser only on click.
- Everything stays on the Mac, in the app container:

| Data | Location | Purpose |
|---|---|---|
| Conversion history (paths, formats, sizes, times) | `history.json` | Show, reveal and repeat past conversions |
| Security-scoped bookmarks | `file-access.json` | Keep access to user-chosen files/folders after relaunch |
| Preferences (theme, output folder, last formats) | `NSUserDefaults` | Restore settings |
| Diagnostics log (local paths, errors) | `Library/Logs/localmorph.log` (rotating) | User can view or copy it from Tools |
| Temporary work files | `Caches/work` | Conversion staging, removed afterwards |

Apple's definition of "collect" means transmitting data off the device, so on-device storage is
not collection.

## Privacy manifest

`Platforms/iOS/Resources/PrivacyInfo.xcprivacy` (bundled into the Mac Store app) has no tracking
domains or collected data types (equivalent to none) and declares these required-reason APIs: FileTimestamp `C617.1`, SystemBootTime `35F9.1`, DiskSpace `E174.1`, UserDefaults `CA92.1`.

## Export compliance

`ITSAppUsesNonExemptEncryption = false`. The app does not implement or call encryption. The
bundled FFmpeg has networking (and therefore TLS) disabled; the only cryptography is macOS
platform code signing and file-access APIs.

## Age rating

Answer **None** to every content descriptor; **No** to unrestricted web access, user-generated
content, messaging, gambling and contests. The expected result is **4+**.
