const uuid = /[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}/gi;

/** Long values without a UUID (e.g. key ids) are cut after this many characters. */
const maxPlain = 24;

/**
 * The visual short form of an identifier: every UUID in it becomes its first 8 characters and an ellipsis (so
 * `admin:<uuid>` stays readable as `admin:1a2b3c4d…`), other long values their first 12 characters. Short values
 * are unchanged. The full value always stays available as text for assistive technology and copying.
 */
export function shortId(value: string): string {
  const shortened = value.replace(uuid, (match) => `${match.slice(0, 8)}…`);
  if (shortened !== value) {
    return shortened;
  }

  return value.length > maxPlain ? `${value.slice(0, 12)}…` : value;
}

/** Whether the browser can copy text: the Clipboard API exists only in secure contexts. */
export function canCopy(): boolean {
  return typeof navigator !== 'undefined' && typeof navigator.clipboard?.writeText === 'function';
}
