import { createHmac } from 'node:crypto';
import { existsSync, readFileSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { runDir } from './env.ts';

const stepSeconds = 30;
const usedStepsFile = join(runDir, 'totp-steps.json');

function base32Decode(value: string): Buffer {
  const alphabet = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ234567';
  let bits = '';
  for (const char of value.replace(/[\s=]/g, '').toUpperCase()) {
    const index = alphabet.indexOf(char);
    if (index < 0) {
      throw new Error(`Invalid base32 character '${char}'.`);
    }

    bits += index.toString(2).padStart(5, '0');
  }

  const bytes = [];
  for (let i = 0; i + 8 <= bits.length; i += 8) {
    bytes.push(parseInt(bits.slice(i, i + 8), 2));
  }

  return Buffer.from(bytes);
}

/** RFC 6238 TOTP (HMAC-SHA1, 30 s, 6 digits) for the time step `step`. */
export function totp(secret: string, step: number): string {
  const counter = Buffer.alloc(8);
  counter.writeBigUInt64BE(BigInt(step));
  const hmac = createHmac('sha1', base32Decode(secret)).update(counter).digest();
  const offset = hmac.readUInt8(hmac.length - 1) & 0x0f;
  const code = (hmac.readUInt32BE(offset) & 0x7fffffff) % 1_000_000;
  return code.toString().padStart(6, '0');
}

/**
 * A code the server hasn't accepted yet: it refuses a time step that was already used, so this waits for the next
 * step when the current one was used before (by any test process).
 */
export async function freshTotp(secret: string): Promise<string> {
  const used: Record<string, number> = existsSync(usedStepsFile)
    ? (JSON.parse(readFileSync(usedStepsFile, 'utf8')) as Record<string, number>)
    : {};
  let step = Math.floor(Date.now() / 1000 / stepSeconds);
  const last = used[secret] ?? -1;
  if (step <= last) {
    step = last + 1;
    await new Promise((resolve) =>
      setTimeout(resolve, step * stepSeconds * 1000 - Date.now() + 500),
    );
  }

  used[secret] = step;
  writeFileSync(usedStepsFile, JSON.stringify(used));
  return totp(secret, step);
}
