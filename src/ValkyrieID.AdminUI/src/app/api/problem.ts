import { HttpErrorResponse } from '@angular/common/http';

/** RFC 9457 problem details, as the Admin API returns them. */
export interface Problem {
  readonly status: number;
  readonly title: string;
  /** Messages that aren't about one field (the `""` key, `detail`, or the title). */
  readonly messages: readonly string[];
  /** Field messages, keyed by the camelCase field name. */
  readonly fields: Readonly<Record<string, readonly string[]>>;
}

export function toProblem(error: unknown): Problem {
  if (!(error instanceof HttpErrorResponse)) {
    return { status: 0, title: 'Something went wrong.', messages: [], fields: {} };
  }

  if (error.status === 0) {
    return {
      status: 0,
      title: "The server couldn't be reached. Check your connection and try again.",
      messages: [],
      fields: {},
    };
  }

  const body: unknown = error.error;
  const record = typeof body === 'object' && body !== null ? (body as Record<string, unknown>) : {};
  const title = typeof record['title'] === 'string' ? record['title'] : defaultTitle(error.status);
  const messages: string[] = [];
  const fields: Record<string, string[]> = {};

  if (typeof record['detail'] === 'string') {
    messages.push(record['detail']);
  }

  const errors = record['errors'];
  if (typeof errors === 'object' && errors !== null) {
    for (const [key, value] of Object.entries(errors as Record<string, unknown>)) {
      const list = Array.isArray(value)
        ? value.filter((v): v is string => typeof v === 'string')
        : [];
      if (key === '') {
        messages.push(...list);
      } else {
        const field = key.charAt(0).toLowerCase() + key.slice(1);
        fields[field] = [...(fields[field] ?? []), ...list];
      }
    }
  }

  return { status: error.status, title, messages, fields };
}

function defaultTitle(status: number): string {
  switch (status) {
    case 400:
      return 'The request is invalid.';
    case 401:
      return 'Your session has expired.';
    case 403:
      return 'You are not allowed to do this.';
    case 404:
      return 'It no longer exists.';
    case 409:
      return 'The change conflicts with the current state.';
    default:
      return 'The server ran into a problem. Try again.';
  }
}
