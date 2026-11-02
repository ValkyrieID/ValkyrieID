import { ChangeDetectionStrategy, Component, computed, input, model } from '@angular/core';

/** Previous/next paging for `limit`/`offset` lists. */
@Component({
  selector: 'app-pager',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (total() > limit()) {
      <nav class="pager" aria-label="Pages">
        <button
          type="button"
          class="secondary"
          [disabled]="offset() === 0"
          (click)="offset.set(max(0, offset() - limit()))"
        >
          Previous
        </button>
        <span>{{ from() }}–{{ to() }} of {{ total() }}</span>
        <button
          type="button"
          class="secondary"
          [disabled]="to() >= total()"
          (click)="offset.set(offset() + limit())"
        >
          Next
        </button>
      </nav>
    }
  `,
})
export class PagerComponent {
  readonly total = input.required<number>();
  readonly limit = input(50);
  readonly offset = model(0);

  protected readonly from = computed(() => Math.min(this.total(), this.offset() + 1));
  protected readonly to = computed(() => Math.min(this.total(), this.offset() + this.limit()));
  protected readonly max = Math.max;
}
