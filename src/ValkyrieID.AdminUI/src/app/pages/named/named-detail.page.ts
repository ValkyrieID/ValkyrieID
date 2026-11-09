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
import { Router } from '@angular/router';
import { AdminApi, NamedKind } from '../../api/admin-api';
import { GroupDto, RoleDto } from '../../api/models';
import { Problem, toProblem } from '../../api/problem';
import { ConfirmButtonComponent } from '../../shared/confirm-button.component';
import { PageHeaderComponent } from '../../shared/page-header.component';
import { ProblemComponent } from '../../shared/problem.component';
import { LoadingComponent } from '../../shared/state.component';
import { Notifier, optional, valueOf } from '../../shared/util';

@Component({
  selector: 'app-named-detail-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    FormsModule,
    ConfirmButtonComponent,
    PageHeaderComponent,
    ProblemComponent,
    LoadingComponent,
  ],
  templateUrl: './named-detail.page.html',
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
  protected readonly plural = computed(() => (this.kind() === 'roles' ? 'Roles' : 'Groups'));
  protected readonly singular = computed(() => (this.kind() === 'roles' ? 'role' : 'group'));
  protected readonly singularTitle = computed(() => (this.kind() === 'roles' ? 'Role' : 'Group'));
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
