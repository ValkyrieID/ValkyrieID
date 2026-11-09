import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { RouterLink } from '@angular/router';

/**
 * The header of every page, as on the account pages: an optional back link, the title, an optional lead line
 * (`lead` or projected `[lead]` content), and the page's primary action (projected `[actions]` content).
 */
@Component({
  selector: 'app-page-header',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink],
  template: `
    <header class="page-header">
      @if (back(); as link) {
        <p class="back">
          <a [routerLink]="link">{{ backLabel() }}</a>
        </p>
      }
      <div class="page-title">
        <h1>{{ heading() }}</h1>
        <ng-content select="[status]" />
        <div class="page-actions">
          <ng-content select="[actions]" />
        </div>
      </div>
      @if (lead(); as text) {
        <p class="lead">{{ text }}</p>
      }
      <ng-content select="[lead]" />
    </header>
  `,
})
export class PageHeaderComponent {
  readonly heading = input.required<string>();
  readonly back = input<string | readonly string[] | null>(null);
  readonly backLabel = input('Back');
  readonly lead = input<string | null>(null);
}
