import { DatePipe, KeyValuePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { AuditEntryDto } from '../api/models';

/** Audit entries, newest first. */
@Component({
  selector: 'app-audit-table',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [DatePipe, KeyValuePipe],
  template: `
    @if (entries().length === 0) {
      <p>No entries.</p>
    } @else {
      <div class="table-wrap">
        <table class="data">
          <thead>
            <tr>
              <th scope="col">Time</th>
              <th scope="col">Event</th>
              <th scope="col">Result</th>
              <th scope="col">Actor</th>
              <th scope="col">Subject</th>
              <th scope="col">Client</th>
              <th scope="col">IP address</th>
              <th scope="col">Details</th>
            </tr>
          </thead>
          <tbody>
            @for (entry of entries(); track entry.id) {
              <tr>
                <td>
                  <time [attr.datetime]="entry.timestamp">{{
                    entry.timestamp | date: 'medium'
                  }}</time>
                </td>
                <td>
                  <code>{{ entry.eventType }}</code>
                </td>
                <td>
                  @if (entry.success) {
                    Success
                  } @else {
                    <span class="badge warn">Failure</span>
                  }
                </td>
                <td class="wrap">{{ entry.actor ?? '–' }}</td>
                <td class="wrap">{{ entry.subjectId ?? '–' }}</td>
                <td>{{ entry.clientId ?? '–' }}</td>
                <td>{{ entry.ipAddress ?? '–' }}</td>
                <td>
                  @if (entry.details; as details) {
                    <dl class="details">
                      @for (item of details | keyvalue; track item.key) {
                        <dt>{{ item.key }}</dt>
                        <dd>{{ format(item.value) }}</dd>
                      }
                    </dl>
                  }
                </td>
              </tr>
            }
          </tbody>
        </table>
      </div>
    }
  `,
})
export class AuditTableComponent {
  readonly entries = input.required<readonly AuditEntryDto[]>();

  protected format(value: unknown): string {
    return typeof value === 'string' ? value : JSON.stringify(value);
  }
}
