import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, input, model, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { AddSecretRequest, CreatedSecret, SecretDto } from '../api/models';
import { ConfirmButtonComponent } from './confirm-button.component';
import { SecretRevealComponent } from './secret-reveal.component';
import { StackTableDirective } from './stack-table.directive';
import { optional, optionalNumber } from './util';

/** Secrets of a confidential client or an API resource: listed by id and expiry only, added, and removed. */
@Component({
  selector: 'app-secrets',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    DatePipe,
    FormsModule,
    ConfirmButtonComponent,
    SecretRevealComponent,
    StackTableDirective,
  ],
  templateUrl: './secrets.component.html',
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
