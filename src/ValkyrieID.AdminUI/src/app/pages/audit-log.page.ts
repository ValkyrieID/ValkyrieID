import { HttpClient } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { firstValueFrom } from 'rxjs';
import { AdminApi } from '../api/admin-api';
import { AuditEntryDto, AuditFilter, AuditPage } from '../api/models';
import { Problem, toProblem } from '../api/problem';
import { ProblemComponent } from '../shared/problem.component';
import { PageHeaderComponent } from '../shared/page-header.component';
import { EmptyComponent, LoadingComponent } from '../shared/state.component';
import { optional } from '../shared/util';
import { AuditTableComponent } from './audit-table.component';

@Component({
  selector: 'app-audit-log-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    FormsModule,
    AuditTableComponent,
    ProblemComponent,
    PageHeaderComponent,
    LoadingComponent,
    EmptyComponent,
  ],
  templateUrl: './audit-log.page.html',
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
