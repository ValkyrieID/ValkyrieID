import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { ConfirmButtonComponent } from './confirm-button.component';

@Component({
  imports: [ConfirmButtonComponent],
  template: `<app-confirm-button
    label="Delete"
    message="This can't be undone."
    [danger]="true"
    (confirmed)="count.set(count() + 1)"
  />`,
})
class Host {
  readonly count = signal(0);
}

describe('ConfirmButtonComponent', () => {
  function render() {
    const fixture = TestBed.createComponent(Host);
    fixture.detectChanges();
    const dialog = fixture.nativeElement.querySelector('dialog') as HTMLDialogElement;
    return { fixture, dialog };
  }

  function close(dialog: HTMLDialogElement, returnValue: string): void {
    dialog.returnValue = returnValue;
    dialog.dispatchEvent(new Event('close'));
  }

  it('labels the dialog with its title and shows the message', () => {
    const { dialog } = render();

    const title = dialog.querySelector('h2') as HTMLElement;
    expect(dialog.getAttribute('aria-labelledby')).toBe(title.id);
    expect(title.textContent?.trim()).toBe('Delete');
    expect(dialog.textContent).toContain("This can't be undone.");
  });

  it('emits only when confirmed', () => {
    const { fixture, dialog } = render();

    close(dialog, 'cancel');
    close(dialog, '');
    expect(fixture.componentInstance.count()).toBe(0);

    close(dialog, 'confirm');
    expect(fixture.componentInstance.count()).toBe(1);
  });

  it('focuses Cancel first, so Enter does not confirm by accident', () => {
    const { dialog } = render();

    const cancel = dialog.querySelector('button[value="cancel"]') as HTMLButtonElement;
    expect(cancel.hasAttribute('autofocus')).toBe(true);
  });
});
