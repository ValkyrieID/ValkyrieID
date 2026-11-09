import { httpResource } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { AdminApi } from '../../api/admin-api';
import { Page, ResourceDto } from '../../api/models';
import { toProblem } from '../../api/problem';
import { PagerComponent } from '../../shared/pager.component';
import { PageHeaderComponent } from '../../shared/page-header.component';
import { ProblemComponent } from '../../shared/problem.component';
import { StackTableDirective } from '../../shared/stack-table.directive';
import { EmptyComponent, LoadingComponent } from '../../shared/state.component';

@Component({
  selector: 'app-resource-list-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    FormsModule,
    RouterLink,
    PagerComponent,
    ProblemComponent,
    PageHeaderComponent,
    LoadingComponent,
    EmptyComponent,
    StackTableDirective,
  ],
  templateUrl: './resource-list.page.html',
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

  protected search(): void {
    this.query.set(this.text().trim());
    this.offset.set(0);
  }

  protected clear(): void {
    this.text.set('');
    this.search();
  }
}
