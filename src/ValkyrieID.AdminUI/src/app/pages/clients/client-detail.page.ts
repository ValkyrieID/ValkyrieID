import { DatePipe } from '@angular/common';
import { httpResource } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, computed, inject, input, signal } from '@angular/core';
import { Router, RouterLink } from '@angular/router';
import { AdminApi } from '../../api/admin-api';
import { AddSecretRequest, ClientDto, CreateClientRequest, CreatedSecret } from '../../api/models';
import { Problem, toProblem } from '../../api/problem';
import { ConfirmButtonComponent } from '../../shared/confirm-button.component';
import { ProblemComponent } from '../../shared/problem.component';
import { SecretsComponent } from '../../shared/secrets.component';
import { Notifier } from '../../shared/util';
import { ClientFormComponent, settingsOf } from './client-form.component';

@Component({
  selector: 'app-client-detail-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    RouterLink,
    DatePipe,
    ClientFormComponent,
    ConfirmButtonComponent,
    ProblemComponent,
    SecretsComponent,
  ],
  template: `
    <p><a routerLink="/clients">Clients</a></p>
    @if (loadProblem()) {
      <h1>Client</h1>
    }
    <app-problem [problem]="loadProblem()" />
    @if (client.hasValue() ? client.value() : undefined; as c) {
      <div class="page-header">
        <h1>{{ c.displayName ?? c.clientId }}</h1>
        @if (!c.builtIn) {
          @if (c.enabled) {
            <app-confirm-button
              label="Disable"
              [title]="'Disable ' + c.clientId + '?'"
              message="The client can't authenticate or start sign-ins, and its refresh tokens are revoked."
              [danger]="true"
              [disabled]="busy()"
              (confirmed)="setEnabled(false)"
            />
          } @else {
            <button type="button" [disabled]="busy()" (click)="setEnabled(true)">Enable</button>
          }
        }
      </div>
      <p class="muted">
        Created {{ c.createdAt | date: 'medium' }} ·
        {{ c.enabled ? 'Enabled' : 'Disabled' }}
      </p>
      @if (c.builtIn) {
        <p class="notice">
          This is the built-in client of the admin UI. Its configuration is managed by the server
          and can't be changed.
        </p>
      }
      <app-problem [problem]="problem()" />

      <section aria-labelledby="config-title">
        <h2 id="config-title">Configuration</h2>
        <app-client-form
          [client]="c"
          [readOnly]="c.builtIn"
          [busy]="busy()"
          [problem]="problem()"
          (submitted)="save($event)"
        />
      </section>

      @if (c.type === 'confidential') {
        <section aria-labelledby="secrets-title">
          <h2 id="secrets-title">Secrets</h2>
          <app-secrets
            prefix="client"
            [secrets]="c.secrets"
            [busy]="busy()"
            [(created)]="createdSecret"
            (add)="addSecret($event)"
            (remove)="removeSecret($event)"
          />
        </section>
      }

      @if (!c.builtIn) {
        <section class="danger-zone" aria-labelledby="delete-title">
          <h2 id="delete-title">Delete client</h2>
          <app-confirm-button
            label="Delete client"
            [title]="'Delete ' + c.clientId + '?'"
            message="The client, its secrets, and its refresh tokens are deleted. This can't be undone."
            [danger]="true"
            [disabled]="busy()"
            (confirmed)="remove()"
          />
        </section>
      }
    } @else if (client.isLoading()) {
      <p role="status">Loading…</p>
    }
  `,
})
export class ClientDetailPage {
  private readonly api = inject(AdminApi);
  private readonly router = inject(Router);
  private readonly notifier = inject(Notifier);

  readonly id = input.required<string>();
  protected readonly client = httpResource<ClientDto>(() => this.api.url(['clients', this.id()]));
  protected readonly loadProblem = computed(() =>
    this.client.error() ? toProblem(this.client.error()) : null,
  );
  protected readonly busy = signal(false);
  protected readonly problem = signal<Problem | null>(null);
  protected readonly createdSecret = signal<CreatedSecret | null>(null);

  protected save(request: CreateClientRequest): Promise<void> {
    return this.run(async () => {
      this.client.value.set(await this.api.updateClient(this.id(), settingsOf(request)));
      this.notifier.announce('The client was saved.');
    });
  }

  protected setEnabled(enabled: boolean): Promise<void> {
    return this.run(async () => {
      this.client.value.set(await this.api.setClientEnabled(this.id(), enabled));
      this.notifier.announce(enabled ? 'The client was enabled.' : 'The client was disabled.');
    });
  }

  protected addSecret(request: AddSecretRequest): Promise<void> {
    return this.run(async () => {
      this.createdSecret.set(await this.api.addClientSecret(this.id(), request));
      this.client.reload();
      this.notifier.announce('A secret was added.');
    });
  }

  protected removeSecret(secretId: string): Promise<void> {
    return this.run(async () => {
      await this.api.removeClientSecret(this.id(), secretId);
      this.client.reload();
      this.notifier.announce('The secret was removed.');
    });
  }

  protected remove(): Promise<void> {
    return this.run(async () => {
      await this.api.deleteClient(this.id());
      this.notifier.announce('The client was deleted.');
      await this.router.navigate(['/clients']);
    });
  }

  private async run(call: () => Promise<void>): Promise<void> {
    this.busy.set(true);
    this.problem.set(null);
    try {
      await call();
    } catch (error) {
      this.problem.set(toProblem(error));
    } finally {
      this.busy.set(false);
    }
  }
}
