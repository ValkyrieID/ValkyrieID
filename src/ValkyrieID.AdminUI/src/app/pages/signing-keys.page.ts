import { DatePipe } from '@angular/common';
import { httpResource } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { AdminApi } from '../api/admin-api';
import { SigningKeyDto, SigningKeyStatus } from '../api/models';
import { Problem, toProblem } from '../api/problem';
import { ConfirmButtonComponent } from '../shared/confirm-button.component';
import { IdComponent } from '../shared/id.component';
import { PageHeaderComponent } from '../shared/page-header.component';
import { ProblemComponent } from '../shared/problem.component';
import { StackTableDirective } from '../shared/stack-table.directive';
import { EmptyComponent, LoadingComponent } from '../shared/state.component';
import { Notifier, valueOf } from '../shared/util';

const order: Record<SigningKeyStatus, number> = { active: 0, pending: 1, retired: 2 };

@Component({
  selector: 'app-signing-keys-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    DatePipe,
    ConfirmButtonComponent,
    IdComponent,
    PageHeaderComponent,
    ProblemComponent,
    LoadingComponent,
    EmptyComponent,
    StackTableDirective,
  ],
  templateUrl: './signing-keys.page.html',
})
export class SigningKeysPage {
  private readonly api = inject(AdminApi);
  private readonly notifier = inject(Notifier);
  protected readonly keys = httpResource<SigningKeyDto[]>(() => this.api.url(['signing-keys']));
  protected readonly loadProblem = computed(() =>
    this.keys.error() ? toProblem(this.keys.error()) : null,
  );
  /** Active first, then pending, then retired; newest first within each. */
  protected readonly sorted = computed(() =>
    valueOf(this.keys)
      ?.slice()
      .sort(
        (a, b) => order[a.status] - order[b.status] || b.activatesAt.localeCompare(a.activatesAt),
      ),
  );
  protected readonly busy = signal(false);
  protected readonly problem = signal<Problem | null>(null);

  protected statusLabel(status: SigningKeyStatus): string {
    return status.charAt(0).toUpperCase() + status.slice(1);
  }

  protected async rotate(): Promise<void> {
    this.busy.set(true);
    this.problem.set(null);
    try {
      this.keys.value.set(await this.api.rotateSigningKeys());
      this.notifier.announce('A new signing key was created.');
    } catch (error) {
      this.problem.set(toProblem(error));
    } finally {
      this.busy.set(false);
    }
  }
}
