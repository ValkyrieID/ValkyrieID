/** Labels and help for the settings sections and fields of the Admin API (docs/reference/configuration.md). */

export interface FieldMeta {
  readonly label: string;
  readonly help?: string;
}

export interface SectionMeta {
  readonly title: string;
  readonly description: string;
  readonly fields: Readonly<Record<string, FieldMeta>>;
}

export const SECTIONS: Readonly<Record<string, SectionMeta>> = {
  tokens: {
    title: 'Token lifetimes',
    description:
      'Defaults for all clients; a client can override them. Changes apply to tokens issued afterwards.',
    fields: {
      accessTokenLifetime: { label: 'Access token lifetime' },
      idTokenLifetime: { label: 'ID token lifetime' },
      refreshTokenSlidingLifetime: {
        label: 'Refresh token sliding lifetime',
        help: 'A refresh token expires when it is unused this long.',
      },
      refreshTokenAbsoluteLifetime: {
        label: 'Refresh token absolute lifetime',
        help: 'A refresh token family ends this long after the sign-in. At least the sliding lifetime.',
      },
    },
  },
  session: {
    title: 'Sign-in session',
    description: 'The single sign-on session shared by all applications. Applies to new sessions.',
    fields: {
      lifetime: { label: 'Session lifetime', help: 'Absolute; not extended by activity.' },
    },
  },
  passwordPolicy: {
    title: 'Password policy',
    description:
      'Applies when a password is set or changed. NIST SP 800-63B recommends length and a breached-password check over composition rules.',
    fields: {
      minimumLength: { label: 'Minimum length', help: 'At least 8 characters.' },
      maximumLength: { label: 'Maximum length', help: '64 to 1024 characters.' },
      requireUppercase: { label: 'Require an uppercase letter' },
      requireLowercase: { label: 'Require a lowercase letter' },
      requireDigit: { label: 'Require a digit' },
      requireNonAlphanumeric: { label: 'Require a character that is not a letter or digit' },
      checkBreachedPasswords: {
        label: 'Reject breached passwords',
        help: 'Checks Have I Been Pwned with k-anonymity: only 5 characters of the password hash are sent.',
      },
    },
  },
  lockout: {
    title: 'Account lockout',
    description: 'Locks an account after repeated failed sign-ins.',
    fields: {
      maxFailedAttempts: { label: 'Failed attempts before lockout' },
      duration: { label: 'Lockout duration' },
    },
  },
  twoFactor: {
    title: 'Two-factor authentication',
    description: 'Users who must use 2FA and have not set it up do so at their next sign-in.',
    fields: {
      required: { label: 'Require 2FA for every user' },
    },
  },
  adminApi: {
    title: 'Admin access',
    description: 'Requirements for using the admin UI and the Admin API.',
    fields: {
      requireTwoFactor: {
        label: 'Require 2FA for admins',
        help: 'Admins must sign in with a second factor to use the Admin API. You can only turn this on while your own sign-in used one.',
      },
    },
  },
  email: {
    title: 'Email',
    description: 'Email verification and the lifetime of links sent by email. Requires SMTP.',
    fields: {
      verificationAtSignIn: {
        label: 'Email verification after sign-in',
        help: 'Prompt: offered once per sign-in. Require: users return to the application only once their address is verified.',
      },
      passwordResetLifetime: {
        label: 'Password reset link lifetime',
        help: '5 minutes to 24 hours.',
      },
      verificationLifetime: {
        label: 'Verification link lifetime',
        help: '5 minutes to 7 days.',
      },
    },
  },
  smtp: {
    title: 'SMTP',
    description: 'The mail server. Email features are on while a host is set.',
    fields: {
      host: { label: 'Host' },
      port: { label: 'Port' },
      security: {
        label: 'Security',
        help: 'StartTls or Tls; None is only allowed for a server on this machine. The server certificate is always validated.',
      },
      userName: { label: 'User name' },
      password: {
        label: 'Password',
        help: 'Stored encrypted. Leave empty to keep the current one.',
      },
      fromAddress: { label: 'Sender address' },
      fromName: { label: 'Sender name', help: 'Defaults to the product name.' },
    },
  },
  branding: {
    title: 'Branding',
    description:
      'The look of the sign-in and account pages. Image and stylesheet files come from the branding directory configured on the server.',
    fields: {
      productName: { label: 'Product name' },
      primaryColor: { label: 'Primary color', help: 'Hex, named, rgb() or hsl().' },
      backgroundColor: { label: 'Background color' },
      surfaceColor: { label: 'Card color' },
      textColor: {
        label: 'Text color',
        help: 'Keep a contrast ratio of at least 4.5:1 with the background colors.',
      },
      logo: { label: 'Logo' },
      backgroundImage: { label: 'Background image' },
      customCss: { label: 'Custom stylesheet' },
    },
  },
  auditLog: {
    title: 'Audit log',
    description: 'How long audit log entries are kept.',
    fields: {
      retention: { label: 'Retention', help: '0 keeps entries forever; otherwise 1 to 3650 days.' },
    },
  },
};

export function sectionMeta(name: string): SectionMeta {
  return SECTIONS[name] ?? { title: name, description: '', fields: {} };
}

export function fieldMeta(section: string, field: string): FieldMeta {
  return sectionMeta(section).fields[field] ?? { label: field };
}

export type DurationUnit = 'seconds' | 'minutes' | 'hours' | 'days';

export const DURATION_UNITS: readonly { unit: DurationUnit; seconds: number }[] = [
  { unit: 'days', seconds: 86400 },
  { unit: 'hours', seconds: 3600 },
  { unit: 'minutes', seconds: 60 },
  { unit: 'seconds', seconds: 1 },
];

/** The largest unit that represents `seconds` exactly, e.g. 1800 → 30 minutes. */
export function splitDuration(seconds: number): { amount: number; unit: DurationUnit } {
  const match = DURATION_UNITS.find((u) => seconds !== 0 && seconds % u.seconds === 0);
  return match
    ? { amount: seconds / match.seconds, unit: match.unit }
    : { amount: seconds, unit: 'seconds' };
}

export function toSeconds(amount: number, unit: DurationUnit): number {
  return amount * (DURATION_UNITS.find((u) => u.unit === unit)?.seconds ?? 1);
}

/** A setting value for display, e.g. "30 minutes" or "Off". */
export function describeValue(kind: string, value: string | number | boolean | null): string {
  if (value === null || value === '') {
    return 'none';
  }

  if (kind === 'duration' && typeof value === 'number') {
    const { amount, unit } = splitDuration(value);
    return `${amount} ${amount === 1 ? unit.slice(0, -1) : unit}`;
  }

  if (typeof value === 'boolean') {
    return value ? 'on' : 'off';
  }

  return String(value);
}
