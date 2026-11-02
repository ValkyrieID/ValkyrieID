import {
  ChangeDetectionStrategy,
  Component,
  computed,
  input,
  linkedSignal,
  output,
} from '@angular/core';
import { FormsModule } from '@angular/forms';
import {
  ClientDto,
  ClientSettingsRequest,
  ClientType,
  CreateClientRequest,
} from '../../api/models';
import { Problem } from '../../api/problem';
import { lines, optional, optionalNumber } from '../../shared/util';

export const GRANT_TYPES = [
  { value: 'authorization_code', label: 'Authorization code (with PKCE)' },
  { value: 'refresh_token', label: 'Refresh token' },
  { value: 'client_credentials', label: 'Client credentials' },
] as const;

/** `PUT /clients/{id}` takes the settings without the client id, type, and first-secret expiry. */
export function settingsOf(request: CreateClientRequest): ClientSettingsRequest {
  return {
    grantTypes: request.grantTypes,
    scopes: request.scopes,
    displayName: request.displayName,
    redirectUris: request.redirectUris,
    postLogoutRedirectUris: request.postLogoutRedirectUris,
    corsOrigins: request.corsOrigins,
    backChannelLogoutUri: request.backChannelLogoutUri,
    requirePkce: request.requirePkce,
    allowClientSecretPost: request.allowClientSecretPost,
    accessTokenLifetime: request.accessTokenLifetime,
    idTokenLifetime: request.idTokenLifetime,
    refreshTokenLifetime: request.refreshTokenLifetime,
    refreshTokenAbsoluteLifetime: request.refreshTokenAbsoluteLifetime,
  };
}

/** The client configuration form, for creating (all fields) and editing (without id, type, and secret expiry). */
@Component({
  selector: 'app-client-form',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule],
  templateUrl: './client-form.component.html',
})
export class ClientFormComponent {
  /** The client being edited; `null` to create one. */
  readonly client = input<ClientDto | null>(null);
  readonly readOnly = input(false);
  readonly busy = input(false);
  readonly problem = input<Problem | null>(null);
  readonly submitted = output<CreateClientRequest>();

  protected readonly grantTypes = GRANT_TYPES;
  protected readonly creating = computed(() => this.client() === null);

  protected readonly clientId = linkedSignal(() => this.client()?.clientId ?? '');
  protected readonly type = linkedSignal<ClientType>(() => this.client()?.type ?? 'confidential');
  protected readonly displayName = linkedSignal(() => this.client()?.displayName ?? '');
  protected readonly grants = linkedSignal(
    () => new Set<string>(this.client()?.grantTypes ?? ['authorization_code']),
  );
  protected readonly scopes = linkedSignal(
    () => this.client()?.scopes.join(' ') ?? 'openid profile',
  );
  protected readonly redirectUris = linkedSignal(
    () => this.client()?.redirectUris.join('\n') ?? '',
  );
  protected readonly postLogoutRedirectUris = linkedSignal(
    () => this.client()?.postLogoutRedirectUris.join('\n') ?? '',
  );
  protected readonly corsOrigins = linkedSignal(() => this.client()?.corsOrigins.join('\n') ?? '');
  protected readonly backChannelLogoutUri = linkedSignal(
    () => this.client()?.backChannelLogoutUri ?? '',
  );
  protected readonly requirePkce = linkedSignal(() => this.client()?.requirePkce ?? true);
  protected readonly allowClientSecretPost = linkedSignal(
    () => this.client()?.allowClientSecretPost ?? false,
  );
  protected readonly accessTokenLifetime = linkedSignal<number | null>(
    () => this.client()?.accessTokenLifetime ?? null,
  );
  protected readonly idTokenLifetime = linkedSignal<number | null>(
    () => this.client()?.idTokenLifetime ?? null,
  );
  protected readonly refreshTokenLifetime = linkedSignal<number | null>(
    () => this.client()?.refreshTokenLifetime ?? null,
  );
  protected readonly refreshTokenAbsoluteLifetime = linkedSignal<number | null>(
    () => this.client()?.refreshTokenAbsoluteLifetime ?? null,
  );
  protected readonly secretExpiresDays = linkedSignal<number | null>(() => null);

  protected readonly isPublic = computed(() => this.type() === 'public');

  protected fieldError(name: string): readonly string[] {
    return this.problem()?.fields[name] ?? [];
  }

  protected toggleGrant(grant: string, event: Event): void {
    const on = (event.target as HTMLInputElement).checked;
    const next = new Set(this.grants());
    if (on) {
      next.add(grant);
    } else {
      next.delete(grant);
    }

    this.grants.set(next);
  }

  protected submit(): void {
    this.submitted.emit({
      clientId: this.clientId().trim(),
      type: this.type(),
      displayName: optional(this.displayName()),
      grantTypes: GRANT_TYPES.map((g) => g.value).filter((g) => this.grants().has(g)),
      scopes: this.scopes()
        .split(/\s+/)
        .filter((s) => s.length > 0),
      redirectUris: lines(this.redirectUris()),
      postLogoutRedirectUris: lines(this.postLogoutRedirectUris()),
      corsOrigins: lines(this.corsOrigins()),
      backChannelLogoutUri: optional(this.backChannelLogoutUri()),
      // RFC 9700 §2.1.1: public clients always use PKCE.
      requirePkce: this.isPublic() || this.requirePkce(),
      allowClientSecretPost: !this.isPublic() && this.allowClientSecretPost(),
      accessTokenLifetime: optionalNumber(this.accessTokenLifetime()),
      idTokenLifetime: optionalNumber(this.idTokenLifetime()),
      refreshTokenLifetime: optionalNumber(this.refreshTokenLifetime()),
      refreshTokenAbsoluteLifetime: optionalNumber(this.refreshTokenAbsoluteLifetime()),
      secretExpiresDays: this.isPublic() ? null : optionalNumber(this.secretExpiresDays()),
    });
  }
}
