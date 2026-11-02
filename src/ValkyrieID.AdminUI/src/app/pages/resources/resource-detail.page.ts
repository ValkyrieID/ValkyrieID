import { DatePipe } from '@angular/common';
import { httpResource } from '@angular/common/http';
import {
  ChangeDetectionStrategy,
  Component,
  computed,
  inject,
  input,
  linkedSignal,
  signal,
} from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { AdminApi } from '../../api/admin-api';
import { AddSecretRequest, CreatedSecret, ResourceDto, ScopeDto } from '../../api/models';
import { Problem, toProblem } from '../../api/problem';
import { ConfirmButtonComponent } from '../../shared/confirm-button.component';
import { ProblemComponent } from '../../shared/problem.component';
import { SecretsComponent } from '../../shared/secrets.component';
import { Notifier, optional } from '../../shared/util';

@Component({
  selector: 'app-resource-detail-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    FormsModule,
    RouterLink,
    DatePipe,
    ConfirmButtonComponent,
    ProblemComponent,
    SecretsComponent,
  ],
  templateUrl: './resource-detail.page.html',
})
export class ResourceDetailPage {
  private readonly api = inject(AdminApi);
  private readonly router = inject(Router);
  private readonly notifier = inject(Notifier);

  readonly id = input.required<string>();
  protected readonly resource = httpResource<ResourceDto>(() =>
    this.api.url(['resources', this.id()]),
  );
  protected readonly loadProblem = computed(() =>
    this.resource.error() ? toProblem(this.resource.error()) : null,
  );
  protected readonly busy = signal(false);
  protected readonly problem = signal<Problem | null>(null);
  protected readonly createdSecret = signal<CreatedSecret | null>(null);

  protected readonly displayName = linkedSignal(() => this.resource.value()?.displayName ?? '');
  protected readonly allowClientSecretPost = linkedSignal(
    () => this.resource.value()?.allowClientSecretPost ?? false,
  );
  /** Scope descriptions being edited, by scope id. */
  protected readonly descriptions = linkedSignal(
    () => new Map((this.resource.value()?.scopes ?? []).map((s) => [s.id, s.description ?? ''])),
  );
  protected readonly scopeName = signal('');
  protected readonly scopeDescription = signal('');

  protected description(scope: ScopeDto): string {
    return this.descriptions().get(scope.id) ?? '';
  }

  protected setDescription(scope: ScopeDto, event: Event): void {
    const next = new Map(this.descriptions());
    next.set(scope.id, (event.target as HTMLInputElement).value);
    this.descriptions.set(next);
  }

  protected save(): Promise<void> {
    return this.update(
      () =>
        this.api.updateResource(this.id(), {
          displayName: optional(this.displayName()),
          allowClientSecretPost: this.allowClientSecretPost(),
        }),
      'The API resource was saved.',
    );
  }

  protected setEnabled(enabled: boolean): Promise<void> {
    return this.update(
      () => this.api.setResourceEnabled(this.id(), enabled),
      enabled ? 'The API resource was enabled.' : 'The API resource was disabled.',
    );
  }

  protected async addScope(): Promise<void> {
    await this.update(
      () =>
        this.api.addScope(this.id(), {
          name: this.scopeName().trim(),
          description: optional(this.scopeDescription()),
        }),
      'The scope was added.',
    );
    if (!this.problem()) {
      this.scopeName.set('');
      this.scopeDescription.set('');
    }
  }

  protected saveScope(scope: ScopeDto): Promise<void> {
    return this.update(
      () =>
        this.api.updateScope(this.id(), scope.id, {
          description: optional(this.description(scope)),
        }),
      `The scope ${scope.name} was saved.`,
    );
  }

  protected removeScope(scope: ScopeDto): Promise<void> {
    return this.update(
      () => this.api.removeScope(this.id(), scope.id),
      `The scope ${scope.name} was removed.`,
    );
  }

  protected addSecret(request: AddSecretRequest): Promise<void> {
    return this.run(async () => {
      this.createdSecret.set(await this.api.addResourceSecret(this.id(), request));
      this.resource.reload();
      this.notifier.announce('A secret was added.');
    });
  }

  protected removeSecret(secretId: string): Promise<void> {
    return this.run(async () => {
      await this.api.removeResourceSecret(this.id(), secretId);
      this.resource.reload();
      this.notifier.announce('The secret was removed.');
    });
  }

  protected remove(): Promise<void> {
    return this.run(async () => {
      await this.api.deleteResource(this.id());
      this.notifier.announce('The API resource was deleted.');
      await this.router.navigate(['/resources']);
    });
  }

  private update(call: () => Promise<ResourceDto>, message: string): Promise<void> {
    return this.run(async () => {
      this.resource.value.set(await call());
      this.notifier.announce(message);
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
