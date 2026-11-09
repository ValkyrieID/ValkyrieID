import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { AdminApi } from '../../api/admin-api';
import { Problem, toProblem } from '../../api/problem';
import { PageHeaderComponent } from '../../shared/page-header.component';
import { ProblemComponent } from '../../shared/problem.component';
import { Notifier, lines, optional } from '../../shared/util';

@Component({
  selector: 'app-resource-create-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, RouterLink, PageHeaderComponent, ProblemComponent],
  templateUrl: './resource-create.page.html',
})
export class ResourceCreatePage {
  private readonly api = inject(AdminApi);
  private readonly router = inject(Router);
  private readonly notifier = inject(Notifier);
  protected readonly name = signal('');
  protected readonly displayName = signal('');
  protected readonly scopes = signal('');
  protected readonly busy = signal(false);
  protected readonly problem = signal<Problem | null>(null);

  protected async create(): Promise<void> {
    this.busy.set(true);
    this.problem.set(null);
    try {
      const resource = await this.api.createResource({
        name: this.name().trim(),
        displayName: optional(this.displayName()),
        scopes: lines(this.scopes()).map((name) => ({ name, description: null })),
      });
      this.notifier.announce(`The API resource ${resource.name} was created.`);
      await this.router.navigate(['/resources', resource.id]);
    } catch (error) {
      this.problem.set(toProblem(error));
    } finally {
      this.busy.set(false);
    }
  }
}
