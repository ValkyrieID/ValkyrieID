import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  computed,
  inject,
  input,
  signal,
} from '@angular/core';
import { canCopy, shortId } from './ids';
import { Notifier } from './util';

/**
 * An identifier, shortened visually (see `shortId`). The full value is the element's text for assistive technology
 * (visually hidden), and the copy button copies it. Without the Clipboard API the button isn't shown; the full value
 * stays available in the detail views.
 */
@Component({
  selector: 'app-id',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { '[class.id-full]': 'full()' },
  templateUrl: './id.component.html',
})
export class IdComponent {
  private readonly notifier = inject(Notifier);

  readonly value = input.required<string>();
  /** What the value is, for the copy button and its announcement, e.g. "user ID". */
  readonly label = input('ID');
  /** Off where many IDs would add many buttons (e.g. the dashboard summary). */
  readonly allowCopy = input(true);
  /** Shows the value in full (detail views), still with the copy button. */
  readonly full = input(false);

  protected readonly short = computed(() => (this.full() ? this.value() : shortId(this.value())));
  protected readonly copyable = computed(() => this.allowCopy() && canCopy());
  protected readonly copied = signal(false);
  private timer: ReturnType<typeof setTimeout> | undefined;

  constructor() {
    inject(DestroyRef).onDestroy(() => clearTimeout(this.timer));
  }

  protected async copy(): Promise<void> {
    const label = this.label();
    try {
      await navigator.clipboard.writeText(this.value());
      this.copied.set(true);
      clearTimeout(this.timer);
      this.timer = setTimeout(() => this.copied.set(false), 2000);
      this.notifier.announce(`${label.charAt(0).toUpperCase()}${label.slice(1)} copied.`);
    } catch {
      this.notifier.announce(`The ${label} couldn't be copied.`);
    }
  }
}
