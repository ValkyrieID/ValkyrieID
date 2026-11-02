import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  input,
  output,
  viewChild,
} from '@angular/core';

let nextId = 0;

/** A button that asks for confirmation in a native modal `<dialog>` (focus trap and Escape built in). */
@Component({
  selector: 'app-confirm-button',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <button
      type="button"
      [class.danger]="danger()"
      [disabled]="disabled()"
      (click)="dialog.showModal()"
    >
      {{ label() }}
    </button>
    <dialog #dialog [attr.aria-labelledby]="id + '-title'" (close)="closed()">
      <h2 [id]="id + '-title'">{{ title() ?? label() }}</h2>
      <p>{{ message() }}</p>
      <form method="dialog" class="actions">
        <button type="submit" value="cancel" class="secondary" autofocus>Cancel</button>
        <button type="submit" value="confirm" [class.danger]="danger()">
          {{ confirmLabel() ?? label() }}
        </button>
      </form>
    </dialog>
  `,
})
export class ConfirmButtonComponent {
  readonly label = input.required<string>();
  readonly message = input.required<string>();
  readonly title = input<string>();
  readonly confirmLabel = input<string>();
  readonly danger = input(false);
  readonly disabled = input(false);
  readonly confirmed = output();

  protected readonly id = `confirm-${nextId++}`;
  private readonly dialog = viewChild.required<ElementRef<HTMLDialogElement>>('dialog');

  protected closed(): void {
    const dialog = this.dialog().nativeElement;
    if (dialog.returnValue === 'confirm') {
      this.confirmed.emit();
    }

    dialog.returnValue = '';
  }
}
