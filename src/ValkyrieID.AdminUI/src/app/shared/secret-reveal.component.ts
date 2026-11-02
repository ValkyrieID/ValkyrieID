import { ChangeDetectionStrategy, Component, input, output, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { CreatedSecret } from '../api/models';

/** A newly created secret: shown once, since only its hash is stored. */
@Component({
  selector: 'app-secret-reveal',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [DatePipe],
  template: `
    <section class="notice" aria-labelledby="secret-title">
      <h2 id="secret-title">New secret</h2>
      <p>Copy it now. It is stored only as a hash and can't be shown again.</p>
      <p>
        <code class="secret" data-testid="secret">{{ secret().secret }}</code>
      </p>
      @if (secret().expiresAt; as expiresAt) {
        <p>Expires {{ expiresAt | date: 'medium' }}.</p>
      }
      <div class="actions">
        <button type="button" (click)="copy()">Copy</button>
        <button type="button" class="secondary" (click)="dismissed.emit()">Done</button>
      </div>
      <p role="status">{{ copied() ? 'Copied to the clipboard.' : '' }}</p>
    </section>
  `,
})
export class SecretRevealComponent {
  readonly secret = input.required<CreatedSecret>();
  readonly dismissed = output();
  protected readonly copied = signal(false);

  protected async copy(): Promise<void> {
    await navigator.clipboard.writeText(this.secret().secret);
    this.copied.set(true);
  }
}
