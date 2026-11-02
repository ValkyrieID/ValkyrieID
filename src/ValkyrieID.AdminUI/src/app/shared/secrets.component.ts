import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, input, model, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { AddSecretRequest, CreatedSecret, SecretDto } from '../api/models';
import { ConfirmButtonComponent } from './confirm-button.component';
import { SecretRevealComponent } from './secret-reveal.component';
import { optional, optionalNumber } from './util';

/** Secrets of a confidential client or an API resource: listed by id and expiry only, added, and removed. */
@Component({
  selector: 'app-secrets',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [DatePipe, FormsModule, ConfirmButtonComponent, SecretRevealComponent],
  template: `
    @if (created(); as secret) {
      <app-secret-reveal [secret]="secret" (dismissed)="created.set(null)" />
    }
    @if (secrets().length === 0) {
      <p>No secrets.</p>
    } @else {
      <div class="table-wrap">
        <table class="data">
          <thead>
            <tr>
              <th scope="col">Description</th>
              <th scope="col">Created</th>
              <th scope="col">Expires</th>
              <th scope="col">Status</th>
              <th scope="col"><span class="visually-hidden">Actions</span></th>
            </tr>
          </thead>
          <tbody>
            @for (secret of secrets(); track secret.id) {
              <tr>
                <td>{{ secret.description ?? '–' }}</td>
                <td>{{ secret.createdAt | date: 'medium' }}</td>
                <td>{{ secret.expiresAt ? (secret.expiresAt | date: 'medium') : 'Never' }}</td>
                <td>
                  @if (secret.active) {
                    Active
                  } @else {
                    <span class="badge warn">expired</span>
                  }
                </td>
                <td>
                  <app-confirm-button
                    label="Remove"
                    title="Remove this secret?"
                    message="Anything still using it can no longer authenticate."
                    [danger]="true"
                    [disabled]="busy()"
                    (confirmed)="remove.emit(secret.id)"
                  />
                </td>
              </tr>
            }
          </tbody>
        </table>
      </div>
    }
    <form class="inline" (ngSubmit)="submit()">
      <div class="field">
        <label [for]="prefix() + '-secret-description'"
          >Description <span class="optional">(optional)</span></label
        >
        <input
          [id]="prefix() + '-secret-description'"
          name="description"
          [(ngModel)]="description"
          autocomplete="off"
        />
      </div>
      <div class="field">
        <label [for]="prefix() + '-secret-days'"
          >Expires after (days) <span class="optional">(empty: never)</span></label
        >
        <input
          [id]="prefix() + '-secret-days'"
          name="expiresDays"
          type="number"
          min="1"
          [(ngModel)]="expiresDays"
        />
      </div>
      <button type="submit" [disabled]="busy()">Add secret</button>
    </form>
  `,
})
export class SecretsComponent {
  readonly secrets = input.required<readonly SecretDto[]>();
  readonly prefix = input('item');
  readonly busy = input(false);
  /** The secret just added; shown until dismissed. */
  readonly created = model<CreatedSecret | null>(null);
  readonly add = output<AddSecretRequest>();
  readonly remove = output<string>();

  protected readonly description = signal('');
  protected readonly expiresDays = signal<number | null>(null);

  protected submit(): void {
    this.add.emit({
      description: optional(this.description()),
      expiresDays: optionalNumber(this.expiresDays()),
    });
    this.description.set('');
    this.expiresDays.set(null);
  }
}
