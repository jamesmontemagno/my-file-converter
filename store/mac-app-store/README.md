# LocalMorph Mac App Store kit

English (US) App Store Connect copy, App Review notes, privacy and compliance answers, and the
owner submission checklist for the Mac App Store edition. Nothing in this kit submits or
publishes anything. Builds reach TestFlight through
[`.github/workflows/mac-store-publish.yml`](../../.github/workflows/mac-store-publish.yml); see
[`docs/release-setup.md`](../../docs/release-setup.md).

| File | Use |
|---|---|
| [`en-us/listing.json`](en-us/listing.json) | Name, subtitle, promotional text, description, keywords, URLs, category, copyright |
| [`review-notes.md`](review-notes.md) | App Review Information > Notes |
| [`app-privacy.md`](app-privacy.md) | App Privacy questionnaire, export compliance, age rating |
| `en-us/screenshots/` | Native 2560 x 1600 captures (#40, not yet captured) |

## App record

| Field | Value |
|---|---|
| Bundle ID | `com.refractored.localmorph` |
| Apple ID | `6824266726` |
| Platform | macOS (Mac Catalyst), Apple silicon only, macOS 15.0 or later |
| Primary / secondary category | Utilities / Photo & Video |
| Price / availability | Free, all available territories (owner decision in #38) |
| Encryption | `ITSAppUsesNonExemptEncryption = false` in `Info.plist` |

## Copy rules

- Claim only outputs that the bundled FFmpeg build or macOS built-in converters actually
  produce. `listing.json` > `notClaimed` lists the formats that are gated off on Mac.
- Never mention installing FFmpeg, Homebrew, or other tools; everything ships inside the app.
- Screenshots must be genuine captures of the Mac app at 2560 x 1600, without added text or
  device frames. Do not resize Windows captures.

## Owner checklist before App Review

- [ ] Desktop privacy policy is live at the `privacyPolicyUrl` in `listing.json`.
- [ ] Paste listing fields, review notes, privacy answers and age rating into App Store Connect.
- [ ] Upload screenshots (#40) and, optionally, an App Store preview.
- [ ] Internal TestFlight acceptance on a clean Mac (#41) with the build selected for review.
- [ ] Paid Apps/Free Apps agreement, tax and banking (not required for free) are active.
- [ ] Choose **Manually release this version**, then submit the tested build (#37).
