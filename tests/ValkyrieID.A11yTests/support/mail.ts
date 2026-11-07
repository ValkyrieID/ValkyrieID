import { readdirSync, readFileSync } from 'node:fs';
import { join } from 'node:path';
import { expect } from '@playwright/test';
import { mailDir } from './env.ts';

/** Decodes a quoted-printable body (RFC 2045 §6.7), enough to find links. */
function decodeQuotedPrintable(value: string): string {
  return value
    .replace(/=\r?\n/g, '')
    .replace(/=([0-9A-F]{2})/gi, (_, hex: string) => String.fromCharCode(parseInt(hex, 16)));
}

/** Waits for an email to `to` that arrived after `since` (ms) and returns the first link to `path`. */
export async function linkFromMail(to: string, path: string, since: number): Promise<string> {
  let link: string | undefined;
  await expect
    .poll(
      () => {
        const files = readdirSync(mailDir)
          .filter((name) => Number(name.split('-')[0]) >= since)
          .sort();
        for (const file of files) {
          const raw = readFileSync(join(mailDir, file), 'utf8');
          const message = /^Content-Transfer-Encoding:\s*quoted-printable/im.test(raw)
            ? decodeQuotedPrintable(raw)
            : raw;
          if (!new RegExp(`^To:.*${to.replace(/[.+]/g, '\\$&')}`, 'im').test(message)) {
            continue;
          }

          const match = new RegExp(`https?://[^\\s"<>]*${path}\\?[^\\s"<>]+`).exec(message);
          if (match) {
            link = match[0].replace(/&amp;/g, '&');
            return true;
          }
        }

        return false;
      },
      { message: `an email to ${to} with a link to ${path}`, timeout: 20_000 },
    )
    .toBe(true);
  if (link === undefined) {
    throw new Error(`No email to ${to} with a link to ${path}.`);
  }

  return link;
}
