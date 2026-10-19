# LocalMorph Mac App Store kit

English (US) App Store Connect copy, App Review notes, privacy and compliance answers, and the
owner submission checklist for the Mac App Store edition. Nothing in this kit submits or
publishes anything. Builds reach TestFlight through
[`.github/workflows/mac-store-publish.yml`](../../.github/workflows/mac-store-publish.yml); see
[`docs/release-setup.md`](../../docs/release-setup.md).

| File | Use |
|---|---|
| [`en-us/listing.json`](en-us/listing.json) | Name, subtitle, promotional text, description, keywords, URLs, category, copyright |
| [`en-us/metadata.md`](en-us/metadata.md) | Copy/paste App Store Connect handoff, field limits, asset mapping, and submission checks |
| [`review-notes.md`](review-notes.md) | App Review Information > Notes |
| [`app-privacy.md`](app-privacy.md) | App Privacy questionnaire, export compliance, age rating |
| [`en-us/screenshots/`](en-us/screenshots/) | Five clean source captures, plus five upload-ready marketing layouts in `marketing/`; all 2560 x 1600 |
| [`en-us/app-previews/`](en-us/app-previews/) | Separate 1920 x 1080 App Store screen recording and poster |
| [`en-us/website-launch/`](en-us/website-launch/) | Held 30-second, animation-led website film with one genuine app shot, poster, captions, transcript, and storyboard |
| [`en-us/asset-manifest.json`](en-us/asset-manifest.json) | Capture provenance, media properties, hashes, and release caveats |
| [`../../scripts/normalize-mac-store-screenshots.py`](../../scripts/normalize-mac-store-screenshots.py) | Flatten capture transparency and color-manage screenshots to sRGB |
| [`../../scripts/render-mac-app-preview.py`](../../scripts/render-mac-app-preview.py) | Encode and validate the genuine App Store preview |
| [`../../scripts/render-mac-launch-video.py`](../../scripts/render-mac-launch-video.py) | Rebuild the held Mac website film and refresh the manifest |

## App record

| Field | Value |
|---|---|
| Bundle ID | `com.refractored.localmorph` |
| Apple ID | `6824266726` |
| SKU | `localmorph` (owner-reported; not independently verified) |
| Platform | macOS (Mac Catalyst), Apple silicon only, macOS 15.0 or later |
| Primary / secondary category | Utilities / Photo & Video |
| Price / availability | Free, all available territories (owner decision in #38) |
| Encryption | `ITSAppUsesNonExemptEncryption = false` in `Info.plist` |

## Copy rules

- Claim only outputs that the bundled FFmpeg build or macOS built-in converters actually
  produce. `listing.json` > `notClaimed` lists the formats that are gated off on Mac.
- Never mention installing FFmpeg, Homebrew, or other tools; everything ships inside the app.
- The five files directly in `en-us/screenshots/` are genuine source captures of the Mac app at
  2560 x 1600. The `marketing/` subfolder contains additional designed treatments that use those
  captures, with copy and decorative artwork outside the app UI. App Review Guideline 2.3.3
  permits text/image overlays when screenshots still show the app in use and accurately
  represent its functionality. Use the five files in `marketing/` for the rich App Store
  presentation; keep the source captures unchanged. Recapture both sets from the final TestFlight
  candidate before submission. Do not resize Windows captures. Normalize freshly captured files with
  `python3 scripts/normalize-mac-store-screenshots.py` before rendering.
- The current captures and App Store preview came from the isolated
  `com.refractored.localmorph.assetcapture` build on Xcode 26.6 / macOS 26.6.2. They are for
  owner review, not evidence from TestFlight or the final Store build; recapture and review them
  against the final TestFlight candidate before submission.
- The website film is a separate, held asset: animated conversion visuals with one genuine app
  workspace shot. The App Store preview remains a genuine screen recording. The film is not
  linked from the public site and does not claim that the Mac app is available. The owner
  confirmed rights to the demo photo and video on October 19, 2026; their original source is not
  recorded. Review the app shot against the final TestFlight candidate before submission.
- The App Store preview is 1920 x 1080, 30 fps, 30 seconds, H.264/yuv420p with stereo AAC.
  This validates the encoded file properties only, not App Store Connect ingestion.

## Rebuilding the media

After replacing any captures or preview source, run from the repository root:

```sh
python3 scripts/normalize-mac-store-screenshots.py
python3 scripts/render-mac-marketing-screenshots.py
python3 scripts/render-mac-app-preview.py
python3 scripts/render-mac-launch-video.py
```

The screenshot and website-film tools require Python 3, Pillow, and NumPy; the video encoders
require `ffmpeg` and `ffprobe` on `PATH`. The renderers verify their outputs and refresh the
asset manifest. The preview source is retained under `en-us/app-previews/source/`.

## Owner checklist before App Review

- [ ] Desktop privacy policy is live at the `privacyPolicyUrl` in `listing.json`.
- [x] Owner reports listing fields, review notes, privacy/export answers, age rating, and
  applicable agreements entered/verified in App Store Connect; not independently verified.
- [ ] Recheck the app record and SKU in App Store Connect before App Review submission.
- [x] Owner confirmed rights to the coastline demo photo/video on October 19, 2026; original source
  provenance remains unrecorded.
- [ ] Recapture the clean screenshots, marketing layouts, and preview from the final TestFlight candidate; review every frame
  for paths, personal data, and parity with the submitted build.
- [ ] Upload the five marketing layouts from `en-us/screenshots/marketing/` and, optionally, the App Store preview.
- [ ] Internal TestFlight acceptance on a clean Mac (#41) with the build selected for review.
- [ ] Paid Apps/Free Apps agreement, tax and banking (not required for free) are active.
- [ ] Choose **Manually release this version**, then submit the tested build (#37).
