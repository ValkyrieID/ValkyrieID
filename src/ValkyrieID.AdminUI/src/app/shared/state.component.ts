import { ChangeDetectionStrategy, Component, input } from '@angular/core';

/** A loading indicator: a status message (announced politely) with a decorative spinner. */
@Component({
  selector: 'app-loading',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <p class="state state-loading" role="status">
      <span class="spinner" aria-hidden="true"></span>{{ label() }}
    </p>
  `,
})
export class LoadingComponent {
  readonly label = input('Loading…');
}

/** An empty list: an icon, a message, an optional hint, and projected actions (e.g. "Create the first client"). */
@Component({
  selector: 'app-empty',
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './empty.component.html',
})
export class EmptyComponent {
  readonly message = input.required<string>();
  readonly hint = input<string | null>(null);
}
