import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  computed,
  effect,
  input,
  viewChild,
} from '@angular/core';
import { Problem } from '../api/problem';

/** An error summary for a failed request; moves focus to itself so screen reader users hear it (WCAG 3.3.1). */
@Component({
  selector: 'app-problem',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (problem(); as p) {
      <div #box class="alert" role="alert" tabindex="-1">
        <p class="alert-title">{{ p.title }}</p>
        @if (lines().length > 0) {
          <ul>
            @for (line of lines(); track $index) {
              <li>{{ line }}</li>
            }
          </ul>
        }
      </div>
    }
  `,
})
export class ProblemComponent {
  readonly problem = input<Problem | null>(null);
  private readonly box = viewChild<ElementRef<HTMLElement>>('box');

  protected readonly lines = computed(() => {
    const p = this.problem();
    if (!p) {
      return [];
    }

    return [
      ...p.messages.filter((m) => m !== p.title),
      ...Object.values(p.fields).flatMap((messages) => messages),
    ];
  });

  constructor() {
    effect(() => this.box()?.nativeElement.focus());
  }
}
