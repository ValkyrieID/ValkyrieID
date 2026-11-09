import { DatePipe } from '@angular/common';
import { httpResource } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { AdminApi } from '../../api/admin-api';
import { Page, UserSummary } from '../../api/models';
import { toProblem } from '../../api/problem';
import { PagerComponent } from '../../shared/pager.component';
import { PageHeaderComponent } from '../../shared/page-header.component';
import { ProblemComponent } from '../../shared/problem.component';
import { StackTableDirective } from '../../shared/stack-table.directive';
import { EmptyComponent, LoadingComponent } from '../../shared/state.component';

@Component({
  selector: 'app-user-list-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    FormsModule,
    RouterLink,
    DatePipe,
    PagerComponent,
    ProblemComponent,
    PageHeaderComponent,
    LoadingComponent,
    EmptyComponent,
    StackTableDirective,
  ],
  templateUrl: './user-list.page.html',
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

  protected clear(): void {
    this.text.set('');
    this.search();
  }
}
