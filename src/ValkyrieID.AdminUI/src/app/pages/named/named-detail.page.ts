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
import { AdminApi, NamedKind } from '../../api/admin-api';
import { GroupDto, RoleDto } from '../../api/models';
import { Problem, toProblem } from '../../api/problem';
import { ConfirmButtonComponent } from '../../shared/confirm-button.component';
import { ProblemComponent } from '../../shared/problem.component';
import { Notifier, optional, valueOf } from '../../shared/util';

@Component({
  selector: 'app-named-detail-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, RouterLink, ConfirmButtonComponent, ProblemComponent],
  template: `
    <p>
      <a [routerLink]="['/' + kind()]">{{ kind() === 'roles' ? 'Roles' : 'Groups' }}</a>
    </p>
    @if (loadProblem()) {
      <h1>{{ kind() === 'roles' ? 'Role' : 'Group' }}</h1>
    }
    <app-problem [problem]="loadProblem()" />
    @if (item.hasValue() ? item.value() : undefined; as current) {
      <h1>{{ current.name }}</h1>
      <p class="muted">{{ current.members }} members</p>
      <app-problem [problem]="problem()" />
      @if (builtIn()) {
        <p class="notice">This role is built in. It can't be renamed or deleted.</p>
      } @else {
        <form class="form" (ngSubmit)="save()">
          <div class="field">
            <label for="named-edit-name">Name</label>
            <input
              id="named-edit-name"
              name="name"
              [(ngModel)]="name"
              required
              maxlength="100"
              autocomplete="off"
              spellcheck="false"
              aria-describedby="named-edit-name-hint"
            />
            @if (kind() === 'roles') {
              <p class="hint" id="named-edit-name-hint">
                Renaming a role signs out its members' sessions.
              </p>
            }
          </div>
          <div class="field">
            <label for="named-edit-description"
              >Description <span class="optional">(optional)</span></label
            >
            <input
              id="named-edit-description"
              name="description"
              [(ngModel)]="description"
              autocomplete="off"
            />
          </div>
          <div class="actions">
            <button type="submit" [disabled]="busy() || !name().trim()">Save</button>
          </div>
        </form>

        <section class="danger-zone" aria-labelledby="named-delete-title">
          <h2 id="named-delete-title">Delete</h2>
          <app-confirm-button
            [label]="'Delete ' + current.name"
            [title]="'Delete ' + current.name + '?'"
            [message]="deleteMessage()"
            [danger]="true"
            [disabled]="busy()"
            (confirmed)="remove()"
          />
        </section>
      }
    } @else if (item.isLoading()) {
      <p role="status">Loading…</p>
    }
  `,
})
export class NamedDetailPage {
  private readonly api = inject(AdminApi);
  private readonly router = inject(Router);
  private readonly notifier = inject(Notifier);

  readonly kind = input.required<NamedKind>();
  readonly id = input.required<string>();

  protected readonly item = httpResource<RoleDto | GroupDto>(() =>
    this.api.url([this.kind(), this.id()]),
  );
  protected readonly loadProblem = computed(() =>
    this.item.error() ? toProblem(this.item.error()) : null,
  );
  protected readonly builtIn = computed(() => {
    const value = valueOf(this.item);
    return !!value && 'builtIn' in value && value.builtIn;
  });
  protected readonly deleteMessage = computed(() =>
    this.kind() === 'roles'
      ? "Members lose the role, and their sessions are signed out. This can't be undone."
      : "Members are removed from the group. This can't be undone.",
  );

  protected readonly name = linkedSignal(() => valueOf(this.item)?.name ?? '');
  protected readonly description = linkedSignal(() => valueOf(this.item)?.description ?? '');
  protected readonly busy = signal(false);
  protected readonly problem = signal<Problem | null>(null);

  protected async save(): Promise<void> {
    await this.run(async () => {
      this.item.value.set(
        await this.api.updateNamed(this.kind(), this.id(), {
          name: this.name().trim(),
          description: optional(this.description()),
        }),
      );
      this.notifier.announce('Saved.');
    });
  }

  protected async remove(): Promise<void> {
    await this.run(async () => {
      await this.api.deleteNamed(this.kind(), this.id());
      this.notifier.announce('Deleted.');
      await this.router.navigate(['/' + this.kind()]);
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
