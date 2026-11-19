import { copyFileSync, mkdirSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

// The branding files live once, in assets/branding. Angular can't read files outside its workspace, so they are
// copied into public/ (git-ignored) before each build.
const source = fileURLToPath(new URL('../../../assets/branding/', import.meta.url));
const target = fileURLToPath(new URL('../public/', import.meta.url));

const files = {
  'wall-light.png': 'img/wall-light.png',
  'wall-dark.png': 'img/wall-dark.png',
  'android-chrome-192x192.png': 'android-chrome-192x192.png',
  'android-chrome-512x512.png': 'android-chrome-512x512.png',
  'apple-touch-icon.png': 'apple-touch-icon.png',
  'favicon-16x16.png': 'favicon-16x16.png',
  'favicon-32x32.png': 'favicon-32x32.png',
  'favicon.ico': 'favicon.ico',
};

for (const [from, to] of Object.entries(files)) {
  const destination = join(target, to);
  mkdirSync(dirname(destination), { recursive: true });
  copyFileSync(join(source, from), destination);
}
