import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { APP_SETTINGS } from '../auth/settings';
import {
  AddClaimRequest,
  AddSecretRequest,
  AuditFilter,
  ClientDto,
  ClientSettingsRequest,
  CreateClientRequest,
  CreateResourceRequest,
  CreateUserRequest,
  CreatedClient,
  CreatedSecret,
  GroupDto,
  NameRequest,
  ResourceDto,
  RoleDto,
  ScopeRequest,
  SigningKeyDto,
  UpdateResourceRequest,
  UpdateScopeRequest,
  UpdateUserRequest,
  UserClaimDto,
  UserDetail,
} from './models';

export type QueryValue = string | number | boolean | null | undefined;

/** Roles and groups share one shape and one set of endpoints. */
export type NamedKind = 'roles' | 'groups';

/**
 * Typed calls to the Admin API (docs/reference/admin-api.md). Reads are done with `httpResource` and
 * {@link AdminApi.url}; writes go through the methods below. All URLs start with the API base, which is what the
 * auth interceptor attaches the token to.
 */
@Injectable({ providedIn: 'root' })
export class AdminApi {
  private readonly http = inject(HttpClient);
  private readonly base = inject(APP_SETTINGS).apiBase;

  /** An absolute Admin API URL; path segments are escaped. */
  url(segments: readonly string[], query: Record<string, QueryValue> = {}): string {
    const path = segments.map((s) => encodeURIComponent(s)).join('/');
    let params = new HttpParams();
    for (const [key, value] of Object.entries(query)) {
      if (value !== null && value !== undefined && value !== '') {
        params = params.set(key, String(value));
      }
    }

    const search = params.toString();
    return `${this.base}${path}${search ? `?${search}` : ''}`;
  }

  // Users
  createUser = (body: CreateUserRequest) => this.post<UserDetail>(['users'], body);
  updateUser = (id: string, body: UpdateUserRequest) => this.put<UserDetail>(['users', id], body);
  deleteUser = (id: string) => this.delete(['users', id]);
  setPassword = (id: string, password: string) =>
    this.post<null>(['users', id, 'password'], { password });
  userAction = (
    id: string,
    action: 'enable' | 'disable' | 'unlock' | 'reset-2fa' | 'require-2fa' | 'unrequire-2fa',
  ) => this.post<UserDetail>(['users', id, action]);
  sendVerification = (id: string) => this.post<null>(['users', id, 'send-verification']);
  addMembership = (id: string, kind: NamedKind, targetId: string) =>
    this.put<UserDetail>(['users', id, kind, targetId]);
  removeMembership = (id: string, kind: NamedKind, targetId: string) =>
    this.delete<UserDetail>(['users', id, kind, targetId]);
  addClaim = (id: string, body: AddClaimRequest) =>
    this.post<UserClaimDto>(['users', id, 'claims'], body);
  removeClaim = (id: string, claimId: string) =>
    this.delete<UserDetail>(['users', id, 'claims', claimId]);
  revokeSession = (id: string, sessionId: string) =>
    this.delete(['users', id, 'sessions', sessionId]);
  revokeAllSessions = (id: string) => this.delete(['users', id, 'sessions']);

  // Roles and groups
  createNamed = (kind: NamedKind, body: NameRequest) => this.post<RoleDto | GroupDto>([kind], body);
  updateNamed = (kind: NamedKind, id: string, body: NameRequest) =>
    this.put<RoleDto | GroupDto>([kind, id], body);
  deleteNamed = (kind: NamedKind, id: string) => this.delete([kind, id]);

  // Clients
  createClient = (body: CreateClientRequest) => this.post<CreatedClient>(['clients'], body);
  updateClient = (id: string, body: ClientSettingsRequest) =>
    this.put<ClientDto>(['clients', id], body);
  deleteClient = (id: string) => this.delete(['clients', id]);
  setClientEnabled = (id: string, enabled: boolean) =>
    this.post<ClientDto>(['clients', id, enabled ? 'enable' : 'disable']);
  addClientSecret = (id: string, body: AddSecretRequest) =>
    this.post<CreatedSecret>(['clients', id, 'secrets'], body);
  removeClientSecret = (id: string, secretId: string) =>
    this.delete(['clients', id, 'secrets', secretId]);

  // API resources
  createResource = (body: CreateResourceRequest) => this.post<ResourceDto>(['resources'], body);
  updateResource = (id: string, body: UpdateResourceRequest) =>
    this.put<ResourceDto>(['resources', id], body);
  deleteResource = (id: string) => this.delete(['resources', id]);
  setResourceEnabled = (id: string, enabled: boolean) =>
    this.post<ResourceDto>(['resources', id, enabled ? 'enable' : 'disable']);
  addScope = (id: string, body: ScopeRequest) =>
    this.post<ResourceDto>(['resources', id, 'scopes'], body);
  updateScope = (id: string, scopeId: string, body: UpdateScopeRequest) =>
    this.put<ResourceDto>(['resources', id, 'scopes', scopeId], body);
  removeScope = (id: string, scopeId: string) =>
    this.delete<ResourceDto>(['resources', id, 'scopes', scopeId]);
  addResourceSecret = (id: string, body: AddSecretRequest) =>
    this.post<CreatedSecret>(['resources', id, 'secrets'], body);
  removeResourceSecret = (id: string, secretId: string) =>
    this.delete(['resources', id, 'secrets', secretId]);

  // Signing keys
  rotateSigningKeys = () => this.post<SigningKeyDto[]>(['signing-keys', 'rotate']);

  auditUrl(filter: AuditFilter, cursor: string | null, limit = 50): string {
    return this.url(['audit'], { ...filter, cursor, limit });
  }

  private post<T>(segments: readonly string[], body: unknown = null): Promise<T> {
    return firstValueFrom(this.http.post<T>(this.url(segments), body));
  }

  private put<T>(segments: readonly string[], body: unknown = null): Promise<T> {
    return firstValueFrom(this.http.put<T>(this.url(segments), body));
  }

  private delete<T = null>(segments: readonly string[]): Promise<T> {
    return firstValueFrom(this.http.delete<T>(this.url(segments)));
  }
}
