import { DatePipe, KeyValuePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, input, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { AuditEntryDto } from '../api/models';
import { IdComponent } from '../shared/id.component';
import { StackTableDirective } from '../shared/stack-table.directive';

let nextId = 0;

/**
 * Audit entries, newest first. A row shows the time, event, result, and actor; a disclosure button (aria-expanded,
 * aria-controls) opens a full-width row below it with every identifier in full and the details.
 */
@Component({
  selector: 'app-audit-table',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [DatePipe, KeyValuePipe, RouterLink, IdComponent, StackTableDirective],
  templateUrl: './audit-table.component.html',
})
export class AuditTableComponent {
  readonly entries = input.required<readonly AuditEntryDto[]>();

  protected readonly expanded = signal<ReadonlySet<string>>(new Set());
  private readonly prefix = `audit-${nextId++}`;

  protected toggle(id: string): void {
    const next = new Set(this.expanded());
    if (!next.delete(id)) {
      next.add(id);
    }

    this.expanded.set(next);
  }

  protected detailId(entry: AuditEntryDto): string {
    return `${this.prefix}-${entry.id}`;
  }

  protected format(value: unknown): string {
    return typeof value === 'string' ? value : JSON.stringify(value);
  }
}
