import { httpResource } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, computed, inject, input, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { AdminApi, NamedKind } from '../../api/admin-api';
import { GroupDto, Page, RoleDto } from '../../api/models';
import { Problem, toProblem } from '../../api/problem';
import { PagerComponent } from '../../shared/pager.component';
import { ProblemComponent } from '../../shared/problem.component';
import { Notifier, optional } from '../../shared/util';

/** Roles and groups: same shape, same endpoints; the kind comes from the route data. */
@Component({
  selector: 'app-named-list-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, RouterLink, PagerComponent, ProblemComponent],
  template: `
    <h1>{{ title() }}</h1>
    <p class="hint">
      {{ title() }} are emitted in the <code>{{ kind() }}</code> claim.
      @if (kind() === 'roles') {
        The built-in <code>admin</code> role grants access to this admin UI and the Admin API.
      }
    </p>
    <form class="search" role="search" (ngSubmit)="search()">
      <label [for]="kind() + '-search'">Search</label>
      <input [id]="kind() + '-search'" name="q" type="search" [(ngModel)]="text" maxlength="200" />
      <button type="submit">Search</button>
    </form>
    <app-problem [problem]="listProblem()" />
    @if (items.value(); as page) {
      @if (page.items.length === 0) {
        <p>None found.</p>
      } @else {
        <div class="table-wrap">
          <table class="data">
            <thead>
              <tr>
                <th scope="col">Name</th>
                <th scope="col">Description</th>
                <th scope="col">Members</th>
              </tr>
            </thead>
            <tbody>
              @for (item of page.items; track item.id) {
                <tr>
                  <td>
                    <a [routerLink]="['/' + kind(), item.id]">{{ item.name }}</a>
                    @if (isBuiltIn(item)) {
                      <span class="badge">built-in</span>
                    }
                  </td>
                  <td>{{ item.description ?? '–' }}</td>
                  <td>{{ item.members }}</td>
                </tr>
              }
            </tbody>
          </table>
        </div>
      }
      <app-pager [total]="page.total" [limit]="limit" [(offset)]="offset" />
    } @else if (items.isLoading()) {
      <p role="status">Loading…</p>
    }

    <section aria-labelledby="create-title">
      <h2 id="create-title">{{ kind() === 'roles' ? 'New role' : 'New group' }}</h2>
      <app-problem [problem]="problem()" />
      <form class="form" (ngSubmit)="create()">
        <div class="field">
          <label for="named-name">Name</label>
          <input
            id="named-name"
            name="name"
            [(ngModel)]="name"
            required
            maxlength="100"
            autocomplete="off"
            spellcheck="false"
          />
        </div>
        <div class="field">
          <label for="named-description"
            >Description <span class="optional">(optional)</span></label
          >
          <input
            id="named-description"
            name="description"
            [(ngModel)]="description"
            autocomplete="off"
          />
        </div>
        <div class="actions">
          <button type="submit" [disabled]="busy() || !name().trim()">Create</button>
        </div>
      </form>
    </section>
  `,
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
