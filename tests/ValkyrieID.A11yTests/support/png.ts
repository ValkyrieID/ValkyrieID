import { inflateSync } from 'node:zlib';

/** A decoded image: 8-bit RGBA, row by row. */
export interface Image {
  width: number;
  height: number;
  data: Uint8Array;
}

const signature = [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a];

/**
 * Decodes the PNGs Chromium writes for screenshots: 8-bit truecolor (RGB) or truecolor with alpha (RGBA), not
 * interlaced, with any of the five row filters (PNG specification, sections 7.3 and 9).
 */
export function decodePng(png: Uint8Array): Image {
  const buffer = Buffer.from(png.buffer, png.byteOffset, png.byteLength);
  if (!signature.every((byte, i) => buffer[i] === byte)) {
    throw new Error('Not a PNG file.');
  }

  let width = 0;
  let height = 0;
  let channels = 0;
  const compressed: Buffer[] = [];
  for (let offset = 8; offset < buffer.length;) {
    const length = buffer.readUInt32BE(offset);
    const type = buffer.toString('latin1', offset + 4, offset + 8);
    const chunk = buffer.subarray(offset + 8, offset + 8 + length);
    if (type === 'IHDR') {
      width = chunk.readUInt32BE(0);
      height = chunk.readUInt32BE(4);
      const [bitDepth, colorType, , , interlace] = chunk.subarray(8, 13);
      if (bitDepth !== 8 || (colorType !== 2 && colorType !== 6) || interlace !== 0) {
        throw new Error(
          `Unsupported PNG (bit depth ${bitDepth}, color type ${colorType}, interlace ${interlace}).`,
        );
      }

      channels = colorType === 6 ? 4 : 3;
    } else if (type === 'IDAT') {
      compressed.push(chunk);
    } else if (type === 'IEND') {
      break;
    }

    offset += 12 + length;
  }

  const raw = inflateSync(Buffer.concat(compressed));
  const stride = width * channels;
  const pixels = new Uint8Array(stride * height);
  for (let y = 0; y < height; y++) {
    const filter = raw[y * (stride + 1)];
    const source = y * (stride + 1) + 1;
    const row = y * stride;
    for (let x = 0; x < stride; x++) {
      const left = x >= channels ? (pixels[row + x - channels] ?? 0) : 0;
      const up = y > 0 ? (pixels[row - stride + x] ?? 0) : 0;
      const upLeft = y > 0 && x >= channels ? (pixels[row - stride + x - channels] ?? 0) : 0;
      let predictor: number;
      switch (filter) {
        case 0:
          predictor = 0;
          break;
        case 1:
          predictor = left;
          break;
        case 2:
          predictor = up;
          break;
        case 3:
          predictor = (left + up) >> 1;
          break;
        case 4: {
          const p = left + up - upLeft;
          const pa = Math.abs(p - left);
          const pb = Math.abs(p - up);
          const pc = Math.abs(p - upLeft);
          predictor = pa <= pb && pa <= pc ? left : pb <= pc ? up : upLeft;
          break;
        }
        default:
          throw new Error(`Invalid PNG row filter ${filter}.`);
      }

      pixels[row + x] = ((raw[source + x] ?? 0) + predictor) & 0xff;
    }
  }

  if (channels === 4) {
    return { width, height, data: pixels };
  }

  const data = new Uint8Array(width * height * 4);
  for (let i = 0, j = 0; i < pixels.length; i += 3, j += 4) {
    data[j] = pixels[i] ?? 0;
    data[j + 1] = pixels[i + 1] ?? 0;
    data[j + 2] = pixels[i + 2] ?? 0;
    data[j + 3] = 255;
  }

  return { width, height, data };
}
