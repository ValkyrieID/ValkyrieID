import { DatePipe } from '@angular/common';
import { httpResource } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { RouterLink } from '@angular/router';
import { AdminApi } from '../api/admin-api';
import { AuditPage, ClientDto, Page, ResourceDto, SigningKeyDto, UserSummary } from '../api/models';
import { toProblem } from '../api/problem';
import { IdComponent } from '../shared/id.component';
import { PageHeaderComponent } from '../shared/page-header.component';
import { ProblemComponent } from '../shared/problem.component';
import { EmptyComponent, LoadingComponent } from '../shared/state.component';
import { valueOf } from '../shared/util';

@Component({
  selector: 'app-dashboard-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    RouterLink,
    DatePipe,
    IdComponent,
    PageHeaderComponent,
    ProblemComponent,
    LoadingComponent,
    EmptyComponent,
  ],
  templateUrl: './dashboard.page.html',
})
export class DashboardPage {
  private readonly api = inject(AdminApi);

  protected readonly users = httpResource<Page<UserSummary>>(() =>
    this.api.url(['users'], { limit: 1 }),
  );
  protected readonly clients = httpResource<Page<ClientDto>>(() =>
    this.api.url(['clients'], { limit: 1 }),
  );
  protected readonly resources = httpResource<Page<ResourceDto>>(() =>
    this.api.url(['resources'], { limit: 1 }),
  );
  protected readonly keys = httpResource<SigningKeyDto[]>(() => this.api.url(['signing-keys']));
  protected readonly audit = httpResource<AuditPage>(() => this.api.auditUrl({}, null, 8));
  protected readonly auditProblem = computed(() =>
    this.audit.error() ? toProblem(this.audit.error()) : null,
  );

  protected readonly activeKey = computed(() =>
    valueOf(this.keys)?.find((k) => k.status === 'active'),
  );
  protected readonly pendingKey = computed(() =>
    valueOf(this.keys)?.some((k) => k.status === 'pending'),
  );

  protected readonly counts = computed(() => ({
    users: valueOf(this.users)?.total ?? '–',
    clients: valueOf(this.clients)?.total ?? '–',
    resources: valueOf(this.resources)?.total ?? '–',
  }));
}
