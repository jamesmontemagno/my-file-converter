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
| [`en-us/screenshots/`](en-us/screenshots/) | Five genuine 2560 x 1600 Mac app captures |
| [`en-us/app-previews/`](en-us/app-previews/) | Separate 1920 x 1080 App Store screen recording and poster |
| [`en-us/website-launch/`](en-us/website-launch/) | Separate animated website film, poster, captions, transcript, and storyboard |
| [`en-us/asset-manifest.json`](en-us/asset-manifest.json) | Capture provenance, media properties, hashes, and release caveats |
| [`../../scripts/normalize-mac-store-screenshots.py`](../../scripts/normalize-mac-store-screenshots.py) | Flatten capture transparency and color-manage screenshots to sRGB |
| [`../../scripts/render-mac-app-preview.py`](../../scripts/render-mac-app-preview.py) | Encode and validate the genuine App Store preview |
| [`../../scripts/render-mac-launch-video.py`](../../scripts/render-mac-launch-video.py) | Rebuild the held Mac website film and refresh the manifest |

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
  device frames. Do not resize Windows captures. Normalize freshly captured files with
  `python3 scripts/normalize-mac-store-screenshots.py` before rendering.
- The current captures and App Store preview came from the isolated
  `com.refractored.localmorph.assetcapture` build on Xcode 26.6 / macOS 26.6.2. They are for
  owner review, not evidence from TestFlight or the final Store build; recapture and review them
  against the final TestFlight candidate before submission.
- The website film is a separate, held asset; it is not linked from the public site and does not
  claim that the Mac app is available. Do not publish or submit the media until the demo photo
  and video rights are confirmed.
- The App Store preview is 1920 x 1080, 30 fps, 30 seconds, H.264/yuv420p with stereo AAC.
  This validates the encoded file properties only, not App Store Connect ingestion.

## Rebuilding the media

After replacing any captures or preview source, run from the repository root:

```sh
python3 scripts/normalize-mac-store-screenshots.py
python3 scripts/render-mac-app-preview.py
python3 scripts/render-mac-launch-video.py
```

The screenshot and website-film tools require Python 3, Pillow, and NumPy; the video encoders
require `ffmpeg` and `ffprobe` on `PATH`. The renderers verify their outputs and refresh the
asset manifest. The preview source is retained under `en-us/app-previews/source/`.

## Owner checklist before App Review

- [ ] Desktop privacy policy is live at the `privacyPolicyUrl` in `listing.json`.
- [ ] Paste listing fields, review notes, privacy answers and age rating into App Store Connect.
- [ ] Confirm rights to the coastline demo photo/video used in the captures and videos.
- [ ] Recapture screenshots and preview from the final TestFlight candidate; review every frame
  for paths, personal data, and parity with the submitted build.
- [ ] Upload screenshots and, optionally, the App Store preview.
- [ ] Internal TestFlight acceptance on a clean Mac (#41) with the build selected for review.
- [ ] Paid Apps/Free Apps agreement, tax and banking (not required for free) are active.
- [ ] Choose **Manually release this version**, then submit the tested build (#37).
