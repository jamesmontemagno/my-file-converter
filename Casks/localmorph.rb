cask "localmorph" do
  auto_updates true
  version "0.0.0"
  sha256 "0000000000000000000000000000000000000000000000000000000000000000"

  url "https://github.com/jamesmontemagno/my-file-converter/releases/download/v#{version}-mac/LocalMorph-v#{version}-mac.zip"
  name "LocalMorph"
  desc "Convert video, audio, images, and documents locally with FFmpeg"
  homepage "https://localmorph.com"

  livecheck do
    url "https://localmorph.com/appcast.xml"
    strategy :sparkle
  end

  depends_on macos: ">= :ventura"
  depends_on formula: "ffmpeg"

  app "LocalMorph.app"

  zap trash: [
    "~/Library/Application Support/com.refractored.localmorph",
    "~/Library/Caches/com.refractored.localmorph",
    "~/Library/Preferences/com.refractored.localmorph.plist",
  ]
end
