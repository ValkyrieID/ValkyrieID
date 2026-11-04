/**
 * Admin API types, written by hand from the OpenAPI document (docs/public/openapi/admin-api.json).
 * models.spec.ts checks them against the document, so a changed API fails the tests.
 */

export interface Page<T> {
  items: T[];
  total: number;
}

export interface NamedRef {
  id: string;
  name: string;
}

export interface UserSummary {
  id: string;
  userName: string;
  email: string | null;
  emailVerified: boolean;
  displayName: string | null;
  enabled: boolean;
  lockedOut: boolean;
  twoFactorEnabled: boolean;
  twoFactorRequired: boolean;
  roles: string[];
  createdAt: string;
}

export interface UserClaimDto {
  id: string;
  type: string;
  value: string;
}

export interface UserDetail {
  id: string;
  userName: string;
  email: string | null;
  emailVerified: boolean;
  displayName: string | null;
  givenName: string | null;
  familyName: string | null;
  enabled: boolean;
  lockedOut: boolean;
  lockoutEnd: string | null;
  accessFailedCount: number;
  twoFactorEnabled: boolean;
  twoFactorRequired: boolean;
  roles: NamedRef[];
  groups: NamedRef[];
  claims: UserClaimDto[];
  createdAt: string;
  updatedAt: string;
  passwordChangedAt: string | null;
}

export interface CreateUserRequest {
  userName: string;
  password: string;
  email: string | null;
  displayName: string | null;
  givenName: string | null;
  familyName: string | null;
}

export interface UpdateUserRequest {
  userName: string;
  email: string | null;
  emailVerified: boolean;
  displayName: string | null;
  givenName: string | null;
  familyName: string | null;
}

export interface SetPasswordRequest {
  password: string;
}

export interface AddClaimRequest {
  type: string;
  value: string;
}

export interface SessionDto {
  id: string;
  authenticatedAt: string;
  lastActivityAt: string;
  expiresAt: string;
  authenticationMethods: string[];
  ipAddress: string | null;
  userAgent: string | null;
  clients: string[];
}

export interface RoleDto {
  id: string;
  name: string;
  description: string | null;
  builtIn: boolean;
  members: number;
}

export interface GroupDto {
  id: string;
  name: string;
  description: string | null;
  members: number;
}

export interface NameRequest {
  name: string;
  description: string | null;
}

export type ClientType = 'confidential' | 'public';

export interface SecretDto {
  id: string;
  description: string | null;
  createdAt: string;
  expiresAt: string | null;
  active: boolean;
}

export interface CreatedSecret {
  id: string;
  secret: string;
  expiresAt: string | null;
}

export interface ClientDto {
  id: string;
  clientId: string;
  displayName: string | null;
  type: ClientType;
  enabled: boolean;
  builtIn: boolean;
  grantTypes: string[];
  scopes: string[];
  redirectUris: string[];
  postLogoutRedirectUris: string[];
  corsOrigins: string[];
  backChannelLogoutUri: string | null;
  requirePkce: boolean;
  allowClientSecretPost: boolean;
  accessTokenLifetime: number | null;
  idTokenLifetime: number | null;
  refreshTokenLifetime: number | null;
  refreshTokenAbsoluteLifetime: number | null;
  createdAt: string;
  secrets: SecretDto[];
}

export interface ClientSettingsRequest {
  grantTypes: string[];
  scopes: string[];
  displayName: string | null;
  redirectUris: string[] | null;
  postLogoutRedirectUris: string[] | null;
  corsOrigins: string[] | null;
  backChannelLogoutUri: string | null;
  requirePkce: boolean;
  allowClientSecretPost: boolean;
  accessTokenLifetime: number | null;
  idTokenLifetime: number | null;
  refreshTokenLifetime: number | null;
  refreshTokenAbsoluteLifetime: number | null;
}

export interface CreateClientRequest extends ClientSettingsRequest {
  clientId: string;
  type: ClientType;
  secretExpiresDays: number | null;
}

export interface CreatedClient {
  client: ClientDto;
  secret: CreatedSecret | null;
}

export interface AddSecretRequest {
  description: string | null;
  expiresDays: number | null;
}

export interface ScopeDto {
  id: string;
  name: string;
  description: string | null;
}

export interface ResourceDto {
  id: string;
  name: string;
  displayName: string | null;
  enabled: boolean;
  builtIn: boolean;
  allowClientSecretPost: boolean;
  createdAt: string;
  scopes: ScopeDto[];
  secrets: SecretDto[];
}

export interface ScopeRequest {
  name: string;
  description: string | null;
}

export interface CreateResourceRequest {
  name: string;
  displayName: string | null;
  scopes: ScopeRequest[];
}

export interface UpdateResourceRequest {
  displayName: string | null;
  allowClientSecretPost: boolean;
}

export interface UpdateScopeRequest {
  description: string | null;
}

export type SigningKeyStatus = 'pending' | 'active' | 'retired';

export interface SigningKeyDto {
  id: string;
  algorithm: string;
  createdAt: string;
  activatesAt: string;
  status: SigningKeyStatus;
}

export interface AuditEntryDto {
  id: string;
  timestamp: string;
  eventType: string;
  success: boolean;
  subjectId: string | null;
  actor: string | null;
  clientId: string | null;
  ipAddress: string | null;
  details: Record<string, unknown> | null;
}

export interface AuditPage {
  items: AuditEntryDto[];
  next: string | null;
}

export interface AuditFilter {
  type?: string;
  subject?: string;
  actor?: string;
  clientId?: string;
  success?: boolean;
  from?: string;
  to?: string;
}

/** Durations are whole seconds; enums and files are strings from `options`. */
export type SettingKind =
  'duration' | 'integer' | 'boolean' | 'string' | 'enum' | 'file' | 'secret';

export type SettingSource = 'default' | 'database' | 'configuration';

export type SettingValue = string | number | boolean | null;

export interface SettingDto {
  name: string;
  key: string;
  kind: SettingKind;
  /** Always `null` for secrets; see `isSet`. */
  value: SettingValue;
  defaultValue: SettingValue;
  /** Set by appsettings or an environment variable; can't be changed through the API. */
  locked: boolean;
  source: SettingSource;
  isSet: boolean | null;
  options: string[] | null;
}

export interface SettingsSectionDto {
  name: string;
  settings: SettingDto[];
}

export interface SettingsDto {
  sections: SettingsSectionDto[];
}

/** `PATCH /settings/{section}`: only the listed settings change; `null` resets one. */
export type SettingsChange = Record<string, SettingValue>;

export interface SmtpTestResult {
  success: boolean;
  result: string;
  to: string;
}
