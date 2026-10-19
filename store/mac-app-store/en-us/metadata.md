# LocalMorph Mac App Store metadata (English U.S.)

Copy/paste handoff for App Store Connect. This is not an API payload and does not submit or
publish anything. `listing.json` is the structured source of truth; keep its values synchronized
with this guide. Apple field limits are noted beside the copy.

## App Information

| Field | Value |
|---|---|
| Name | `LocalMorph` |
| Subtitle | `Private file converter` |
| SKU | `localmorph` (owner-supplied; verify before submission because it cannot be changed after app creation) |
| Bundle ID | `com.refractored.localmorph` |
| Apple ID | `6824266726` |
| Primary language | English (U.S.) |
| Primary category | Utilities |
| Secondary category | Photo & Video |
| Copyright | `2026 Refractored LLC` |
| Price | Free |
| Availability | All available territories |

The name and subtitle are within Apple's 30-character limit. The selected macOS category in
App Store Connect should match the app's built category.

## Version information — macOS, English (U.S.)

### Promotional Text

144 / 170 characters:

```text
Convert video, audio, photos, and documents right on your Mac. Nothing is uploaded, and FFmpeg is built in, so there is nothing else to install.
```

### Description

2,188 / 4,000 characters:

```text
Your files. Your Mac. The format you need.

LocalMorph is a file converter that works entirely on your Mac. Turn a screen recording into a shareable MP4, shrink a clip to fit a size limit, extract the audio from a video, convert a folder of iPhone photos, or turn a Markdown file into a PDF. Your files are never uploaded to a conversion server.

EVERYTHING INCLUDED
LocalMorph includes its own FFmpeg and FFprobe and uses converters built into macOS for photos, PDF pages, and documents. There is nothing else to download or install, and it works offline.

START SIMPLE, GO DEEPER WHEN YOU NEED TO
Add files or folders, or drop them on the window. Pick a quick preset or an output format, and LocalMorph checks the whole batch before converting. Preview video, audio, and images, then trim, grab a frame, or choose tracks.

VIDEO
Convert to MP4, MOV, or MKV with H.264, HEVC, or ProRes, using Apple's hardware video encoder when available. Fit a clip under a target file size for sharing. Change resolution, frame rate, rotation, and speed, or crop, fade, deinterlace, and denoise. Burn subtitles into the picture, or keep, convert, and extract subtitle tracks. Tone-map HDR video to SDR for wider compatibility.

AUDIO
Extract or convert audio to AAC, Apple Lossless, FLAC, or WAV. Choose tracks, replace audio, normalize loudness to a measured target, and trim silence at the start and end.

PHOTOS AND DOCUMENTS
Convert HEIC, RAW, PSD, WebP, and other photos to PNG, JPEG, HEIC, AVIF, TIFF, GIF, BMP, ICO, or PDF. Render the first page of a PDF as an image. Convert Markdown, HTML, RTF, and text to PDF, RTF, HTML, or plain text.

JOIN, SPLIT, AND MORE
Join compatible clips without re-encoding, split a video into equal parts, or make a contact sheet or audio waveform image.

A QUEUE YOU CAN FOLLOW
Import up to 2,000 files at a time, follow progress, and cancel a single job or the whole batch. Your originals are never overwritten. History lets you reopen, reveal, or repeat completed conversions.

PRIVATE BY DESIGN
LocalMorph does not collect data, has no accounts, and does not connect to the internet. You choose which files and folders it can read and where it saves results.
```

### Keywords

93 / 100 bytes:

```text
video,audio,image,convert,converter,ffmpeg,mp4,heic,compress,batch,offline,subtitle,trim,hevc
```

### What's New

Leave blank for the initial version.

## URLs and support

| App Store Connect field | Value | Check |
|---|---|---|
| Support URL | `https://github.com/jamesmontemagno/my-file-converter/issues` | Verify this remains the desired public support channel |
| Marketing URL | `https://localmorph.com` | Verify the page is live and appropriate for the Mac listing |
| Privacy Policy URL | `https://localmorph.com/#/privacy` | **Must verify the desktop policy is publicly reachable before submission** |

## App Review Information

Use [`../review-notes.md`](../review-notes.md) for the review notes and test steps. No sign-in is
required. Enter the App Review contact name, email, and phone directly in App Store Connect; those
owner contact details are intentionally not stored in this repository.

## Privacy, export compliance, and age rating

Use [`../app-privacy.md`](../app-privacy.md) as the questionnaire answer sheet:

- **App Privacy:** `No, we do not collect data from this app` / Data Not Collected, based on the
  current Store-build audit. Local history, bookmarks, preferences, diagnostics, and temporary
  work files remain on the Mac.
- **Export compliance:** the Store build declares
  `ITSAppUsesNonExemptEncryption = false`; use the documented answer and verify it against the
  final build.
- **Age rating:** answer the descriptors as recorded in `app-privacy.md`; expected rating is 4+.
- **Content rights:** complete the App Store Connect declaration truthfully. The owner confirmed
  rights to the demo photo/video used in the assets; original source provenance remains
  unrecorded.

## Screenshots and preview to upload

Upload the five marketing-treated screenshots in numbered order from:

`en-us/screenshots/marketing/`

Each is a 2560 x 1600 PNG. The clean captures directly in `en-us/screenshots/` are retained as
sources, not the recommended marketing set. The optional 30-second App Store preview is:

`en-us/app-previews/localmorph-mac-app-preview.mp4`

Its poster is `en-us/app-previews/localmorph-mac-app-preview-poster.png`. Do not upload the
separate, animation-led website film from `en-us/website-launch/` as the App Store preview; that
film has one genuine app shot, while the preview is a genuine screen recording. The current
captures and preview came from an isolated asset-capture build, not the final TestFlight
candidate; recapture and review them against the exact build selected for App Review.

## Submission checklist

- [ ] Compare each value here with App Store Connect; the owner previously reported the fields
  entered/verified, but they have not been independently audited.
- [ ] Verify the SKU, app record, category, bundle ID, availability, pricing, seller agreements,
  and applicable account details.
- [ ] Confirm the privacy policy and support/marketing URLs resolve publicly.
- [ ] Recheck every feature and format claim against completed normal-workspace/TestFlight
  acceptance in #41.
- [ ] Recapture/review screenshots and preview from the final TestFlight candidate; confirm no
  personal paths or unsupported claims appear.
- [ ] Select the same tested TestFlight build for review. The current beta build is not, by itself,
  a submission approval.
- [ ] Choose manual release after approval. App Review submission and public availability remain
  separate owner actions.

## Apple references

- [App information](https://developer.apple.com/help/app-store-connect/reference/app-information/app-information/)
- [Platform version information](https://developer.apple.com/help/app-store-connect/reference/app-information/platform-version-information/)
- [App privacy](https://developer.apple.com/help/app-store-connect/reference/app-privacy/)
- [Mac screenshot specifications](https://developer.apple.com/help/app-store-connect/reference/app-information/screenshot-specifications/)
- [Upload app previews and screenshots](https://developer.apple.com/help/app-store-connect/manage-app-information/upload-app-previews-and-screenshots/)
