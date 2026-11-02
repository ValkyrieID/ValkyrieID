import { httpResource } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { AdminApi } from '../../api/admin-api';
import { ClientDto, Page } from '../../api/models';
import { toProblem } from '../../api/problem';
import { PagerComponent } from '../../shared/pager.component';
import { ProblemComponent } from '../../shared/problem.component';

@Component({
  selector: 'app-client-list-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, RouterLink, PagerComponent, ProblemComponent],
  template: `
    <div class="page-header">
      <h1>Clients</h1>
      <a class="button" routerLink="/clients/new">New client</a>
    </div>
    <form class="search" role="search" (ngSubmit)="search()">
      <label for="client-search">Search by client id or name</label>
      <input id="client-search" name="q" type="search" [(ngModel)]="text" maxlength="200" />
      <button type="submit">Search</button>
    </form>
    <app-problem [problem]="problem()" />
    @if (clients.value(); as page) {
      @if (page.items.length === 0) {
        <p>No clients found.</p>
      } @else {
        <div class="table-wrap">
          <table class="data">
            <thead>
              <tr>
                <th scope="col">Client id</th>
                <th scope="col">Name</th>
                <th scope="col">Type</th>
                <th scope="col">Grant types</th>
                <th scope="col">Status</th>
              </tr>
            </thead>
            <tbody>
              @for (client of page.items; track client.id) {
                <tr>
                  <td>
                    <a [routerLink]="['/clients', client.id]">{{ client.clientId }}</a>
                    @if (client.builtIn) {
                      <span class="badge">built-in</span>
                    }
                  </td>
                  <td>{{ client.displayName ?? '–' }}</td>
                  <td>{{ client.type }}</td>
                  <td>{{ client.grantTypes.join(', ') }}</td>
                  <td>
                    @if (client.enabled) {
                      Enabled
                    } @else {
                      <span class="badge warn">disabled</span>
                    }
                  </td>
                </tr>
              }
            </tbody>
          </table>
        </div>
      }
      <app-pager [total]="page.total" [limit]="limit" [(offset)]="offset" />
    } @else if (clients.isLoading()) {
      <p role="status">Loading…</p>
    }
  `,
})
export class ClientListPage {
  private readonly api = inject(AdminApi);
  protected readonly limit = 50;
  protected readonly text = signal('');
  protected readonly query = signal('');
  protected readonly offset = signal(0);
  protected readonly clients = httpResource<Page<ClientDto>>(() =>
    this.api.url(['clients'], { q: this.query(), limit: this.limit, offset: this.offset() }),
  );
  protected readonly problem = computed(() =>
    this.clients.error() ? toProblem(this.clients.error()) : null,
  );

  protected search(): void {
    this.query.set(this.text().trim());
    this.offset.set(0);
  }
}
