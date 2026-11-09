import { DatePipe } from '@angular/common';
import { httpResource } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, computed, inject, input, signal } from '@angular/core';
import { Router } from '@angular/router';
import { AdminApi } from '../../api/admin-api';
import { AddSecretRequest, ClientDto, CreateClientRequest, CreatedSecret } from '../../api/models';
import { Problem, toProblem } from '../../api/problem';
import { ConfirmButtonComponent } from '../../shared/confirm-button.component';
import { PageHeaderComponent } from '../../shared/page-header.component';
import { ProblemComponent } from '../../shared/problem.component';
import { SecretsComponent } from '../../shared/secrets.component';
import { LoadingComponent } from '../../shared/state.component';
import { Notifier } from '../../shared/util';
import { ClientFormComponent, settingsOf } from './client-form.component';

@Component({
  selector: 'app-client-detail-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    DatePipe,
    ClientFormComponent,
    ConfirmButtonComponent,
    PageHeaderComponent,
    ProblemComponent,
    SecretsComponent,
    LoadingComponent,
  ],
  templateUrl: './client-detail.page.html',
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
