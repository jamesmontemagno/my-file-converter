# LocalMorph Microsoft Store listing kit

English (United States) listing copy, native app screenshots, reusable brand artwork, and
separate promotional compositions. Nothing in this kit submits or publishes a listing.

The kit includes seven 2560 x 1440 native screenshots, seven separate 2560 x 1800 promotional
graphics, 300/512/1024-pixel tile icons, and 1920 x 1080 text-free hero artwork.
[`contact-sheet.png`](contact-sheet.png) previews the screenshot sequence.
[`en-us/listing-copy.txt`](en-us/listing-copy.txt) provides plain-text listing fields for copying;
[`assets.json`](assets.json) records image dimensions, sizes, provenance, and SHA256 hashes.
[`capture-evidence.json`](capture-evidence.json) records ten actual conversion outputs verified
with ffprobe, including a 38-second compatible join and three accurate split clips.

## Upload mapping

| Partner Center field | Asset |
|---|---|
| Product name, description, short description, features | [`en-us/listing.json`](en-us/listing.json) |
| Desktop screenshots | PNGs in [`en-us/screenshots`](en-us/screenshots), in numbered order |
| Screenshot captions | [`en-us/screenshots.json`](en-us/screenshots.json) |
| 1:1 app tile icon | `logos/app-tile-300.png` |
| Optional 16:9 super hero art | `logos/super-hero-1920x1080.png` |
| Website/social feature graphics, **not Store screenshots** | [`promotional`](promotional) |

The JSON files are a copy-review handoff format, **not** a Partner Center CSV import or an
API submission payload. Paste the individual fields into Partner Center. No credentials or
private Store identity values are needed for this kit.

## Screenshot policy

- Store screenshots are actual captures from the running Windows app, without added headlines,
  logos, device frames, or invented UI. Demo conversions are real, not fabricated progress or
  history entries.
- Captions are supplied separately and are limited to 200 characters.
- Feature-headline compositions are separate marketing assets because Microsoft's screenshot
  guidance says not to add logos, icons, or marketing messages to Store screenshots.
- Original generated sample imagery is used only as demonstration content. It is not a claim
  that LocalMorph generates images. Derivative sample videos are made locally from that imagery.
- This is a source/development-build capture, not evidence that a Store package has passed
  certification. Do not use a screenshot to claim installed Explorer integration until the
  packaged integration has been validated.

## Details to confirm before submission

1. Confirm the reserved product name, category, pricing, markets, and desired support URL.
2. Publish a desktop-specific privacy policy and fill `privacyPolicyUrl`. The existing website
   policy describes browser/bridge conversion and is not sufficient evidence for desktop
   history, preferences, cache files, and external tool/Store links.
3. Complete the age-rating and other Partner Center questionnaires; do not infer an age rating.
4. Leave **What's new** blank for the first submission. A separate update draft is included.
5. Complete package, architecture, licensing, protected-approval, installation, and certification
   gates in [`shipping-checklist.md`](../../docs/shipping-checklist.md). Assets alone do not make
   the app release-ready.
6. Confirm screenshots still match the final shipping build. Optional tools and codecs are
   disclosed in the listing; do not advertise all formats as universally available.

## Verified Microsoft guidance

Requirements checked on 2026-10-16:

- Desktop screenshots: PNG, at least 1366 x 768, up to 3840 x 2160, no more than 50 MB;
  up to ten desktop screenshots. This kit targets readable landscape captures.
- Screenshot captions: at most 200 characters.
- App tile icon: 300 x 300 PNG, strongly recommended for apps.
- Super hero art: 1920 x 1080 or 3840 x 2160 PNG; no text, product title, app UI, or devices.
- Description: at most 10,000 characters.
- Features: at most twenty, each at most 200 characters.
- Short description: at most 1,000 characters; keeping it under 270 is recommended.
- What's new: at most 1,500 characters; leave blank for an initial submission.
- Xbox/game poster and box art are not required for this desktop utility.

Sources:

- [Screenshots, images, and trailers for MSIX apps](https://learn.microsoft.com/windows/apps/publish/publish-your-app/msix/screenshots-and-images)
- [Store listing information for MSIX apps](https://learn.microsoft.com/windows/apps/publish/publish-your-app/msix/add-and-edit-store-listing-info)

## Reproduction

`source/coastline.png` is the original generated demo image. Keep the real native captures as
the source of truth: never use an image generator to redraw application UI. Capture and image
composition helpers live in [`scripts`](scripts).
Run these commands from the repository root; the helpers reuse app sources from this checkout.

The helpers use existing local installations of FFmpeg/ffprobe, ImageMagick, Python with Pillow,
and WinApp CLI. Fonts and logo sources are reused from the app; no separate font download is needed.

The capture helper drives a running app and performs real conversions. Use a demo-only queue
and history; back up and isolate your normal app metadata first, and restore it after closing
the capture process. This kit's capture session preserved the user's original history and
preferences byte-for-byte. The helper targets the main HWND explicitly to avoid including
tooltip windows or WinApp's diagnostic multi-window labels.

```powershell
.\store\microsoft-store\scripts\New-DemoMedia.ps1 -OutputDirectory <demo-folder>
# Launch LocalMorph with the three demo MOV files in a clean demo session, then:
.\store\microsoft-store\scripts\Capture-Screenshots.ps1 -AppPid <pid> -DemoDirectory <demo-folder>
python .\store\microsoft-store\scripts\build-kit.py
```

The original trim capture included WinApp's multi-window composition frame. Only its external
label strip and adjacent tooltip panel were removed; the full native app image was preserved.
The image inventory records this normalization.
