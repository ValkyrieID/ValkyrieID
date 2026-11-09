import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { Router } from '@angular/router';
import { AdminApi } from '../../api/admin-api';
import { CreateClientRequest, CreatedClient } from '../../api/models';
import { Problem, toProblem } from '../../api/problem';
import { PageHeaderComponent } from '../../shared/page-header.component';
import { ProblemComponent } from '../../shared/problem.component';
import { SecretRevealComponent } from '../../shared/secret-reveal.component';
import { Notifier } from '../../shared/util';
import { ClientFormComponent } from './client-form.component';

@Component({
  selector: 'app-client-create-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ClientFormComponent, PageHeaderComponent, ProblemComponent, SecretRevealComponent],
  template: `
    <app-page-header
      heading="New client"
      back="/clients"
      backLabel="Clients"
      lead="An application that signs users in or calls APIs."
    />
    @if (created(); as result) {
      <p>
        The client <code>{{ result.client.clientId }}</code> was created.
      </p>
      @if (result.secret; as secret) {
        <app-secret-reveal [secret]="secret" (dismissed)="open(result)" />
      }
    } @else {
      <app-problem [problem]="problem()" />
      <app-client-form [busy]="busy()" [problem]="problem()" (submitted)="create($event)" />
    }
  `,
})
export class ClientCreatePage {
  private readonly api = inject(AdminApi);
  private readonly router = inject(Router);
  private readonly notifier = inject(Notifier);
  protected readonly busy = signal(false);
  protected readonly problem = signal<Problem | null>(null);
  protected readonly created = signal<CreatedClient | null>(null);

  protected async create(request: CreateClientRequest): Promise<void> {
    this.busy.set(true);
    this.problem.set(null);
    try {
      const result = await this.api.createClient(request);
      this.notifier.announce(`The client ${result.client.clientId} was created.`);
      if (result.secret) {
        // Shown once: a confidential client's first secret.
        this.created.set(result);
      } else {
        await this.open(result);
      }
    } catch (error) {
      this.problem.set(toProblem(error));
    } finally {
      this.busy.set(false);
    }
  }

  protected async open(result: CreatedClient): Promise<void> {
    await this.router.navigate(['/clients', result.client.id]);
  }
}
