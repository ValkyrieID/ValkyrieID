import { httpResource } from '@angular/common/http';
import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  computed,
  inject,
  input,
  signal,
  viewChild,
} from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { AdminApi, NamedKind } from '../../api/admin-api';
import { GroupDto, Page, RoleDto } from '../../api/models';
import { Problem, toProblem } from '../../api/problem';
import { PagerComponent } from '../../shared/pager.component';
import { PageHeaderComponent } from '../../shared/page-header.component';
import { ProblemComponent } from '../../shared/problem.component';
import { StackTableDirective } from '../../shared/stack-table.directive';
import { EmptyComponent, LoadingComponent } from '../../shared/state.component';
import { Notifier, optional } from '../../shared/util';

/** Roles and groups: same shape, same endpoints; the kind comes from the route data. */
@Component({
  selector: 'app-named-list-page',
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
  templateUrl: './named-list.page.html',
})
export class NamedListPage {
  private readonly api = inject(AdminApi);
  private readonly notifier = inject(Notifier);

  readonly kind = input.required<NamedKind>();
  protected readonly title = computed(() => (this.kind() === 'roles' ? 'Roles' : 'Groups'));
  protected readonly limit = 50;
  protected readonly text = signal('');
  protected readonly query = signal('');
  protected readonly offset = signal(0);
  protected readonly name = signal('');
  protected readonly description = signal('');
  protected readonly busy = signal(false);
  protected readonly problem = signal<Problem | null>(null);
  private readonly nameInput = viewChild.required<ElementRef<HTMLInputElement>>('nameInput');

  protected readonly items = httpResource<Page<RoleDto | GroupDto>>(() =>
    this.api.url([this.kind()], { q: this.query(), limit: this.limit, offset: this.offset() }),
  );
  protected readonly listProblem = computed(() =>
    this.items.error() ? toProblem(this.items.error()) : null,
  );

  protected isBuiltIn(item: RoleDto | GroupDto): boolean {
    return 'builtIn' in item && item.builtIn;
  }

  protected search(): void {
    this.query.set(this.text().trim());
    this.offset.set(0);
  }

  protected clear(): void {
    this.text.set('');
    this.search();
  }

  /** The header's "New" action: the form is on this page, so it moves the focus there. */
  protected startCreate(): void {
    const input = this.nameInput().nativeElement;
    input.scrollIntoView({ block: 'center' });
    input.focus();
  }

  protected async create(): Promise<void> {
    this.busy.set(true);
    this.problem.set(null);
    try {
      const created = await this.api.createNamed(this.kind(), {
        name: this.name().trim(),
        description: optional(this.description()),
      });
      this.name.set('');
      this.description.set('');
      this.items.reload();
      this.notifier.announce(`${created.name} was created.`);
    } catch (error) {
      this.problem.set(toProblem(error));
    } finally {
      this.busy.set(false);
    }
  }
}
