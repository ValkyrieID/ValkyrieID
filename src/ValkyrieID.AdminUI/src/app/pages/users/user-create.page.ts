import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { AdminApi } from '../../api/admin-api';
import { Problem, toProblem } from '../../api/problem';
import { PageHeaderComponent } from '../../shared/page-header.component';
import { ProblemComponent } from '../../shared/problem.component';
import { Notifier, optional } from '../../shared/util';

@Component({
  selector: 'app-user-create-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, RouterLink, PageHeaderComponent, ProblemComponent],
  templateUrl: './user-create.page.html',
})
export class UserCreatePage {
  private readonly api = inject(AdminApi);
  private readonly router = inject(Router);
  private readonly notifier = inject(Notifier);

  protected readonly userName = signal('');
  protected readonly password = signal('');
  protected readonly email = signal('');
  protected readonly displayName = signal('');
  protected readonly givenName = signal('');
  protected readonly familyName = signal('');
  protected readonly busy = signal(false);
  protected readonly problem = signal<Problem | null>(null);

  protected async create(): Promise<void> {
    this.busy.set(true);
    this.problem.set(null);
    try {
      const user = await this.api.createUser({
        userName: this.userName().trim(),
        password: this.password(),
        email: optional(this.email()),
        displayName: optional(this.displayName()),
        givenName: optional(this.givenName()),
        familyName: optional(this.familyName()),
      });
      this.password.set('');
      this.notifier.announce(`User ${user.userName} was created.`);
      await this.router.navigate(['/users', user.id]);
    } catch (error) {
      this.problem.set(toProblem(error));
    } finally {
      this.busy.set(false);
    }
  }
}
