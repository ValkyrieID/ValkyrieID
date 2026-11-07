import { DatePipe } from '@angular/common';
import { httpResource } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { AdminApi } from '../api/admin-api';
import { SigningKeyDto } from '../api/models';
import { Problem, toProblem } from '../api/problem';
import { ConfirmButtonComponent } from '../shared/confirm-button.component';
import { ProblemComponent } from '../shared/problem.component';
import { Notifier } from '../shared/util';

@Component({
  selector: 'app-signing-keys-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [DatePipe, ConfirmButtonComponent, ProblemComponent],
  template: `
    <div class="page-header">
      <h1>Signing keys</h1>
      <app-confirm-button
        label="Rotate now"
        title="Rotate the signing key?"
        message="A new key is published at once and starts signing tokens after the publish lead time. Tokens signed by the current key stay valid."
        confirmLabel="Rotate"
        [disabled]="busy()"
        (confirmed)="rotate()"
      />
    </div>
    <p>
      Pending keys are published but don't sign yet; retired keys are still published for the tokens
      they signed. Private keys never leave the server.
    </p>
    <app-problem [problem]="problem()" />
    @if (keys.hasValue() ? keys.value() : undefined; as list) {
      <div class="table-wrap">
        <table class="data">
          <thead>
            <tr>
              <th scope="col">Key id</th>
              <th scope="col">Algorithm</th>
              <th scope="col">Status</th>
              <th scope="col">Created</th>
              <th scope="col">Signs from</th>
            </tr>
          </thead>
          <tbody>
            @for (key of list; track key.id) {
              <tr>
                <td>
                  <code>{{ key.id }}</code>
                </td>
                <td>{{ key.algorithm }}</td>
                <td>
                  <span class="badge" [class.ok]="key.status === 'active'">{{ key.status }}</span>
                </td>
                <td>{{ key.createdAt | date: 'medium' }}</td>
                <td>{{ key.activatesAt | date: 'medium' }}</td>
              </tr>
            }
          </tbody>
        </table>
      </div>
    } @else if (keys.isLoading()) {
      <p role="status">Loading…</p>
    }
  `,
})
export class SigningKeysPage {
  private readonly api = inject(AdminApi);
  private readonly notifier = inject(Notifier);
  protected readonly keys = httpResource<SigningKeyDto[]>(() => this.api.url(['signing-keys']));
  protected readonly busy = signal(false);
  protected readonly problem = signal<Problem | null>(null);

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
