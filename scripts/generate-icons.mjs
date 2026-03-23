import { readFileSync, writeFileSync } from 'fs';
import { join, dirname } from 'path';
import { fileURLToPath } from 'url';
import sharp from 'sharp';

const __dirname = dirname(fileURLToPath(import.meta.url));
const publicDir = join(__dirname, '..', 'public');

const iconSvg = readFileSync(join(publicDir, 'icon.svg'));
const ogSvg = readFileSync(join(publicDir, 'og-image.svg'));

async function generate() {
  // PWA 512x512
  await sharp(iconSvg).resize(512, 512).png().toFile(join(publicDir, 'pwa-512x512.png'));
  console.log('✓ pwa-512x512.png');

  // PWA 192x192
  await sharp(iconSvg).resize(192, 192).png().toFile(join(publicDir, 'pwa-192x192.png'));
  console.log('✓ pwa-192x192.png');

  // Apple touch icon 180x180
  await sharp(iconSvg).resize(180, 180).png().toFile(join(publicDir, 'apple-touch-icon.png'));
  console.log('✓ apple-touch-icon.png');

  // OG image 1200x630
  await sharp(ogSvg).resize(1200, 630).png().toFile(join(publicDir, 'og-image.png'));
  console.log('✓ og-image.png');

  console.log('Done — all icons generated.');
}

generate().catch((err) => {
  console.error(err);
  process.exit(1);
});
