# App Review notes

Paste into App Store Connect > App Review Information > Notes. No sign-in is required.

LocalMorph converts video, audio, image and document files entirely on the Mac. It makes no
network connections (there is no network entitlement) and collects no data.

How to test:
1. Click "Add files" (or drag files onto the window) and choose any video, photo or audio file.
   Access is granted only through the system open panel, Finder "Open With", or drag and drop.
2. Choose a quick preset or an output format on the right, then click "Convert".
3. On the first conversion, LocalMorph asks for an output folder with the system folder
   picker. It saves a security-scoped bookmark so it can keep writing there after relaunch.
4. Completed files appear in History, where they can be revealed in Finder or reopened.

Bundled tools: the app includes FFmpeg and FFprobe 9.0.2, built from source under the LGPL with
libass, FreeType, FriBidi, HarfBuzz and zimg, in Contents/Resources/FFmpeg. They are signed
with the app, sandboxed with com.apple.security.inherit, and run only as child processes on
files the user selected. Nothing is downloaded or installed at runtime. Licenses are shown in
Tools > Third-party licenses. Photo and document conversion uses macOS frameworks (ImageIO,
Core Graphics, the text system).

Formats that would need other engines (for example MP3, Office documents) are shown as
unavailable instead of offering an external install.
