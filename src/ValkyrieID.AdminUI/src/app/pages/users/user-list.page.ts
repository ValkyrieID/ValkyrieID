import { DatePipe } from '@angular/common';
import { httpResource } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { AdminApi } from '../../api/admin-api';
import { Page, UserSummary } from '../../api/models';
import { toProblem } from '../../api/problem';
import { PagerComponent } from '../../shared/pager.component';
import { ProblemComponent } from '../../shared/problem.component';

@Component({
  selector: 'app-user-list-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, RouterLink, DatePipe, PagerComponent, ProblemComponent],
  template: `
    <div class="page-header">
      <h1>Users</h1>
      <a class="button" routerLink="/users/new">New user</a>
    </div>
    <form class="search" role="search" (ngSubmit)="search()">
      <label for="user-search">Search by username or email</label>
      <input id="user-search" name="q" type="search" [(ngModel)]="text" maxlength="200" />
      <button type="submit">Search</button>
    </form>
    <app-problem [problem]="problem()" />
    @if (users.value(); as page) {
      @if (page.items.length === 0) {
        <p>No users found.</p>
      } @else {
        <div class="table-wrap">
          <table class="data">
            <thead>
              <tr>
                <th scope="col">Username</th>
                <th scope="col">Name</th>
                <th scope="col">Email</th>
                <th scope="col">Status</th>
                <th scope="col">Roles</th>
                <th scope="col">Created</th>
              </tr>
            </thead>
            <tbody>
              @for (user of page.items; track user.id) {
                <tr>
                  <td>
                    <a [routerLink]="['/users', user.id]">{{ user.userName }}</a>
                  </td>
                  <td>{{ user.displayName ?? '–' }}</td>
                  <td class="wrap">
                    {{ user.email ?? '–' }}
                    @if (user.email && !user.emailVerified) {
                      <span class="badge">unverified</span>
                    }
                  </td>
                  <td>
                    @if (!user.enabled) {
                      <span class="badge warn">disabled</span>
                    }
                    @if (user.lockedOut) {
                      <span class="badge warn">locked out</span>
                    }
                    @if (user.twoFactorEnabled) {
                      <span class="badge ok">2FA</span>
                    } @else if (user.twoFactorRequired) {
                      <span class="badge">2FA required</span>
                    }
                  </td>
                  <td>{{ user.roles.join(', ') || '–' }}</td>
                  <td>{{ user.createdAt | date: 'mediumDate' }}</td>
                </tr>
              }
            </tbody>
          </table>
        </div>
      }
      <app-pager [total]="page.total" [limit]="limit" [(offset)]="offset" />
    } @else if (users.isLoading()) {
      <p role="status">Loading…</p>
    }
  `,
})
export class UserListPage {
  private readonly api = inject(AdminApi);
  protected readonly limit = 50;
  protected readonly text = signal('');
  protected readonly query = signal('');
  protected readonly offset = signal(0);

  protected readonly users = httpResource<Page<UserSummary>>(() =>
    this.api.url(['users'], { q: this.query(), limit: this.limit, offset: this.offset() }),
  );
  protected readonly problem = computed(() =>
    this.users.error() ? toProblem(this.users.error()) : null,
  );

  protected search(): void {
    this.query.set(this.text().trim());
    this.offset.set(0);
  }
}
