import { HttpClient } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { firstValueFrom } from 'rxjs';
import { AdminApi } from '../api/admin-api';
import { AuditEntryDto, AuditFilter, AuditPage } from '../api/models';
import { Problem, toProblem } from '../api/problem';
import { ProblemComponent } from '../shared/problem.component';
import { optional } from '../shared/util';
import { AuditTableComponent } from './audit-table.component';

@Component({
  selector: 'app-audit-log-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, AuditTableComponent, ProblemComponent],
  template: `
    <h1>Audit log</h1>
    <form class="filters" (ngSubmit)="apply()">
      <div class="field">
        <label for="audit-type">Event type</label>
        <input
          id="audit-type"
          name="type"
          [(ngModel)]="type"
          aria-describedby="audit-type-hint"
          autocomplete="off"
        />
        <p class="hint" id="audit-type-hint">
          For example user.created, or user. for all user events.
        </p>
      </div>
      <div class="field">
        <label for="audit-actor">Actor</label>
        <input
          id="audit-actor"
          name="actor"
          [(ngModel)]="actor"
          aria-describedby="audit-actor-hint"
          autocomplete="off"
        />
        <p class="hint" id="audit-actor-hint">cli, system, or admin:&lt;user id&gt;.</p>
      </div>
      <div class="field">
        <label for="audit-subject">Subject (user id)</label>
        <input id="audit-subject" name="subject" [(ngModel)]="subject" autocomplete="off" />
      </div>
      <div class="field">
        <label for="audit-client">Client id</label>
        <input id="audit-client" name="clientId" [(ngModel)]="clientId" autocomplete="off" />
      </div>
      <div class="field">
        <label for="audit-success">Result</label>
        <select id="audit-success" name="success" [(ngModel)]="success">
          <option value="">Any</option>
          <option value="true">Success</option>
          <option value="false">Failure</option>
        </select>
      </div>
      <div class="field">
        <label for="audit-from">From</label>
        <input id="audit-from" name="from" type="datetime-local" [(ngModel)]="from" />
      </div>
      <div class="field">
        <label for="audit-to">Until</label>
        <input id="audit-to" name="to" type="datetime-local" [(ngModel)]="to" />
      </div>
      <div class="actions">
        <button type="submit">Apply filters</button>
        <button type="button" class="secondary" (click)="clear()">Clear</button>
      </div>
    </form>

    <app-problem [problem]="problem()" />
    <app-audit-table [entries]="entries()" />
    @if (loading()) {
      <p role="status">Loading…</p>
    } @else if (next()) {
      <button type="button" class="secondary" (click)="load(false)">Load more</button>
    }
  `,
})
export class AuditLogPage {
  private readonly api = inject(AdminApi);
  private readonly http = inject(HttpClient);

  protected readonly type = signal('');
  protected readonly actor = signal('');
  protected readonly subject = signal('');
  protected readonly clientId = signal('');
  protected readonly success = signal('');
  protected readonly from = signal('');
  protected readonly to = signal('');

  protected readonly entries = signal<AuditEntryDto[]>([]);
  protected readonly next = signal<string | null>(null);
  protected readonly loading = signal(false);
  protected readonly problem = signal<Problem | null>(null);
  private filter: AuditFilter = {};

  constructor() {
    void this.load(true);
  }

  protected apply(): void {
    this.filter = {
      type: optional(this.type()) ?? undefined,
      actor: optional(this.actor()) ?? undefined,
      subject: optional(this.subject()) ?? undefined,
      clientId: optional(this.clientId()) ?? undefined,
      success: this.success() === '' ? undefined : this.success() === 'true',
      from: toIso(this.from()),
      to: toIso(this.to()),
    };
    void this.load(true);
  }

  protected clear(): void {
    for (const field of [
      this.type,
      this.actor,
      this.subject,
      this.clientId,
      this.success,
      this.from,
      this.to,
    ]) {
      field.set('');
    }

    this.apply();
  }

  protected async load(reset: boolean): Promise<void> {
    this.loading.set(true);
    this.problem.set(null);
    try {
      const page = await firstValueFrom(
        this.http.get<AuditPage>(this.api.auditUrl(this.filter, reset ? null : this.next())),
      );
      this.entries.set(reset ? page.items : [...this.entries(), ...page.items]);
      this.next.set(page.next);
    } catch (error) {
      this.problem.set(toProblem(error));
    } finally {
      this.loading.set(false);
    }
  }
}

/** `datetime-local` values are local time; the API takes ISO 8601. */
function toIso(value: string): string | undefined {
  return value ? new Date(value).toISOString() : undefined;
}
