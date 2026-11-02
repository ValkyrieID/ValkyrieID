import { httpResource } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { AdminApi } from '../../api/admin-api';
import { Page, ResourceDto } from '../../api/models';
import { toProblem } from '../../api/problem';
import { PagerComponent } from '../../shared/pager.component';
import { ProblemComponent } from '../../shared/problem.component';

@Component({
  selector: 'app-resource-list-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, RouterLink, PagerComponent, ProblemComponent],
  template: `
    <div class="page-header">
      <h1>API resources</h1>
      <a class="button" routerLink="/resources/new">New API resource</a>
    </div>
    <p class="hint">
      An API resource is an API protected with ValkyrieID tokens. Its name is the token audience
      (<code>aud</code>); its scopes are what clients can request.
    </p>
    <form class="search" role="search" (ngSubmit)="search()">
      <label for="resource-search">Search by name</label>
      <input id="resource-search" name="q" type="search" [(ngModel)]="text" maxlength="200" />
      <button type="submit">Search</button>
    </form>
    <app-problem [problem]="problem()" />
    @if (resources.value(); as page) {
      @if (page.items.length === 0) {
        <p>No API resources found.</p>
      } @else {
        <div class="table-wrap">
          <table class="data">
            <thead>
              <tr>
                <th scope="col">Name</th>
                <th scope="col">Display name</th>
                <th scope="col">Scopes</th>
                <th scope="col">Status</th>
              </tr>
            </thead>
            <tbody>
              @for (resource of page.items; track resource.id) {
                <tr>
                  <td>
                    <a [routerLink]="['/resources', resource.id]">{{ resource.name }}</a>
                    @if (resource.builtIn) {
                      <span class="badge">built-in</span>
                    }
                  </td>
                  <td>{{ resource.displayName ?? '–' }}</td>
                  <td class="wrap">{{ scopeNames(resource) }}</td>
                  <td>
                    @if (resource.enabled) {
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
    } @else if (resources.isLoading()) {
      <p role="status">Loading…</p>
    }
  `,
})
export class ResourceListPage {
  private readonly api = inject(AdminApi);
  protected readonly limit = 50;
  protected readonly text = signal('');
  protected readonly query = signal('');
  protected readonly offset = signal(0);
  protected readonly resources = httpResource<Page<ResourceDto>>(() =>
    this.api.url(['resources'], { q: this.query(), limit: this.limit, offset: this.offset() }),
  );
  protected readonly problem = computed(() =>
    this.resources.error() ? toProblem(this.resources.error()) : null,
  );

  protected scopeNames(resource: ResourceDto): string {
    return resource.scopes.map((s) => s.name).join(', ') || '–';
  }

  protected search(): void {
    this.query.set(this.text().trim());
    this.offset.set(0);
  }
}
