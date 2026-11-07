import { DatePipe } from '@angular/common';
import { httpResource } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { RouterLink } from '@angular/router';
import { AdminApi } from '../api/admin-api';
import { AuditPage, ClientDto, Page, ResourceDto, SigningKeyDto, UserSummary } from '../api/models';
import { valueOf } from '../shared/util';
import { AuditTableComponent } from './audit-table.component';

@Component({
  selector: 'app-dashboard-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, DatePipe, AuditTableComponent],
  template: `
    <h1>Dashboard</h1>
    <ul class="cards">
      <li>
        <a routerLink="/users"
          ><span class="count">{{
            (users.hasValue() ? users.value() : undefined)?.total ?? '–'
          }}</span>
          Users</a
        >
      </li>
      <li>
        <a routerLink="/clients"
          ><span class="count">{{
            (clients.hasValue() ? clients.value() : undefined)?.total ?? '–'
          }}</span>
          Clients</a
        >
      </li>
      <li>
        <a routerLink="/resources"
          ><span class="count">{{
            (resources.hasValue() ? resources.value() : undefined)?.total ?? '–'
          }}</span>
          API resources</a
        >
      </li>
      <li>
        <a routerLink="/signing-keys">
          <span class="count">{{ activeKey()?.algorithm ?? '–' }}</span> Signing key
          @if (activeKey(); as key) {
            <span class="muted">since {{ key.activatesAt | date: 'mediumDate' }}</span>
          }
          @if (pendingKey()) {
            <span class="badge">Rotation pending</span>
          }
        </a>
      </li>
    </ul>

    <h2>Recent activity</h2>
    @if (audit.hasValue() ? audit.value() : undefined; as page) {
      <app-audit-table [entries]="page.items" />
      <p><a routerLink="/audit">Open the audit log</a></p>
    } @else if (audit.isLoading()) {
      <p role="status">Loading…</p>
    }
  `,
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
  protected readonly audit = httpResource<AuditPage>(() => this.api.auditUrl({}, null, 10));

  protected readonly activeKey = computed(() =>
    valueOf(this.keys)?.find((k) => k.status === 'active'),
  );
  protected readonly pendingKey = computed(() =>
    valueOf(this.keys)?.some((k) => k.status === 'pending'),
  );
}
