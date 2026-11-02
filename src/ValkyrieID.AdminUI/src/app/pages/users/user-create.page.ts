import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { AdminApi } from '../../api/admin-api';
import { Problem, toProblem } from '../../api/problem';
import { ProblemComponent } from '../../shared/problem.component';
import { Notifier, optional } from '../../shared/util';

@Component({
  selector: 'app-user-create-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, RouterLink, ProblemComponent],
  template: `
    <p><a routerLink="/users">Users</a></p>
    <h1>New user</h1>
    <app-problem [problem]="problem()" />
    <form class="form" (ngSubmit)="create()">
      <div class="field">
        <label for="new-user-name">Username</label>
        <input
          id="new-user-name"
          name="userName"
          [(ngModel)]="userName"
          required
          maxlength="64"
          autocomplete="off"
          autocapitalize="none"
          spellcheck="false"
          [attr.aria-invalid]="!!problem()?.fields?.['userName']"
        />
      </div>
      <div class="field">
        <label for="new-user-password">Password</label>
        <input
          id="new-user-password"
          name="password"
          type="password"
          [(ngModel)]="password"
          required
          autocomplete="new-password"
          aria-describedby="new-user-password-hint"
          [attr.aria-invalid]="!!problem()?.fields?.['password']"
        />
        <p class="hint" id="new-user-password-hint">Must satisfy the password policy.</p>
      </div>
      <div class="field">
        <label for="new-user-email">Email <span class="optional">(optional)</span></label>
        <input
          id="new-user-email"
          name="email"
          type="email"
          [(ngModel)]="email"
          autocomplete="off"
          [attr.aria-invalid]="!!problem()?.fields?.['email']"
        />
      </div>
      <div class="field">
        <label for="new-user-display">Display name <span class="optional">(optional)</span></label>
        <input
          id="new-user-display"
          name="displayName"
          [(ngModel)]="displayName"
          autocomplete="off"
        />
      </div>
      <div class="field">
        <label for="new-user-given">Given name <span class="optional">(optional)</span></label>
        <input id="new-user-given" name="givenName" [(ngModel)]="givenName" autocomplete="off" />
      </div>
      <div class="field">
        <label for="new-user-family">Family name <span class="optional">(optional)</span></label>
        <input id="new-user-family" name="familyName" [(ngModel)]="familyName" autocomplete="off" />
      </div>
      <div class="actions">
        <button type="submit" [disabled]="busy()">Create user</button>
      </div>
    </form>
  `,
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
