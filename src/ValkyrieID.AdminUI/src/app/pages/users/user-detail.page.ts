import { DatePipe } from '@angular/common';
import { httpResource } from '@angular/common/http';
import {
  ChangeDetectionStrategy,
  Component,
  computed,
  inject,
  input,
  linkedSignal,
  signal,
} from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { AdminApi, NamedKind } from '../../api/admin-api';
import { GroupDto, Page, RoleDto, SessionDto, UserDetail } from '../../api/models';
import { Problem, toProblem } from '../../api/problem';
import { ConfirmButtonComponent } from '../../shared/confirm-button.component';
import { ProblemComponent } from '../../shared/problem.component';
import { Notifier, optional, valueOf } from '../../shared/util';

@Component({
  selector: 'app-user-detail-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, RouterLink, DatePipe, ConfirmButtonComponent, ProblemComponent],
  templateUrl: './user-detail.page.html',
})
export class UserDetailPage {
  private readonly api = inject(AdminApi);
  private readonly router = inject(Router);
  private readonly notifier = inject(Notifier);

  readonly id = input.required<string>();

  protected readonly user = httpResource<UserDetail>(() => this.api.url(['users', this.id()]));
  protected readonly sessions = httpResource<SessionDto[]>(() =>
    this.api.url(['users', this.id(), 'sessions']),
  );
  protected readonly allRoles = httpResource<Page<RoleDto>>(() =>
    this.api.url(['roles'], { limit: 200 }),
  );
  protected readonly allGroups = httpResource<Page<GroupDto>>(() =>
    this.api.url(['groups'], { limit: 200 }),
  );

  protected readonly busy = signal(false);
  protected readonly problem = signal<Problem | null>(null);
  protected readonly loadProblem = computed(() =>
    this.user.error() ? toProblem(this.user.error()) : null,
  );

  // Profile form, reset whenever the user is (re)loaded.
  protected readonly userName = linkedSignal(() => valueOf(this.user)?.userName ?? '');
  protected readonly email = linkedSignal(() => valueOf(this.user)?.email ?? '');
  protected readonly emailVerified = linkedSignal(() => valueOf(this.user)?.emailVerified ?? false);
  protected readonly displayName = linkedSignal(() => valueOf(this.user)?.displayName ?? '');
  protected readonly givenName = linkedSignal(() => valueOf(this.user)?.givenName ?? '');
  protected readonly familyName = linkedSignal(() => valueOf(this.user)?.familyName ?? '');

  protected readonly newPassword = signal('');
  protected readonly roleToAdd = signal('');
  protected readonly groupToAdd = signal('');
  protected readonly claimType = signal('');
  protected readonly claimValue = signal('');

  protected readonly availableRoles = computed(() => {
    const assigned = new Set(valueOf(this.user)?.roles.map((r) => r.id));
    return valueOf(this.allRoles)?.items.filter((r) => !assigned.has(r.id)) ?? [];
  });
  protected readonly availableGroups = computed(() => {
    const assigned = new Set(valueOf(this.user)?.groups.map((g) => g.id));
    return valueOf(this.allGroups)?.items.filter((g) => !assigned.has(g.id)) ?? [];
  });

  protected saveProfile(): Promise<void> {
    return this.update(
      () =>
        this.api.updateUser(this.id(), {
          userName: this.userName().trim(),
          email: optional(this.email()),
          emailVerified: this.emailVerified(),
          displayName: optional(this.displayName()),
          givenName: optional(this.givenName()),
          familyName: optional(this.familyName()),
        }),
      'The profile was saved.',
    );
  }

  protected action(
    action: 'enable' | 'disable' | 'unlock' | 'reset-2fa' | 'require-2fa' | 'unrequire-2fa',
    message: string,
  ): Promise<void> {
    return this.update(() => this.api.userAction(this.id(), action), message, true);
  }

  protected async sendVerification(): Promise<void> {
    await this.run(
      () => this.api.sendVerification(this.id()),
      'A verification email is on its way.',
    );
  }

  protected async setPassword(): Promise<void> {
    const ok = await this.run(
      () => this.api.setPassword(this.id(), this.newPassword()),
      'The password was set. The user was signed out everywhere.',
    );
    if (ok) {
      this.newPassword.set('');
      this.sessions.reload();
      this.user.reload();
    }
  }

  protected addMembership(kind: NamedKind): Promise<void> {
    const target = kind === 'roles' ? this.roleToAdd() : this.groupToAdd();
    if (!target) {
      return Promise.resolve();
    }

    return this.update(
      () => this.api.addMembership(this.id(), kind, target),
      kind === 'roles' ? 'The role was assigned.' : 'The user was added to the group.',
      kind === 'roles',
    );
  }

  protected removeMembership(kind: NamedKind, targetId: string): Promise<void> {
    return this.update(
      () => this.api.removeMembership(this.id(), kind, targetId),
      kind === 'roles' ? 'The role was removed.' : 'The user was removed from the group.',
      kind === 'roles',
    );
  }

  protected async addClaim(): Promise<void> {
    const ok = await this.run(
      () =>
        this.api.addClaim(this.id(), { type: this.claimType().trim(), value: this.claimValue() }),
      'The claim was added.',
    );
    if (ok) {
      this.claimType.set('');
      this.claimValue.set('');
      this.user.reload();
    }
  }

  protected removeClaim(claimId: string): Promise<void> {
    return this.update(() => this.api.removeClaim(this.id(), claimId), 'The claim was removed.');
  }

  protected async revokeSession(sessionId: string): Promise<void> {
    if (
      await this.run(() => this.api.revokeSession(this.id(), sessionId), 'The session was revoked.')
    ) {
      this.sessions.reload();
    }
  }

  protected async revokeAllSessions(): Promise<void> {
    if (await this.run(() => this.api.revokeAllSessions(this.id()), 'All sessions were revoked.')) {
      this.sessions.reload();
    }
  }

  protected async deleteUser(): Promise<void> {
    const name = valueOf(this.user)?.userName ?? '';
    if (await this.run(() => this.api.deleteUser(this.id()), `User ${name} was deleted.`)) {
      await this.router.navigate(['/users']);
    }
  }

  /** Runs an action that returns the updated user; optionally reloads the sessions it may have revoked. */
  private async update(
    call: () => Promise<UserDetail>,
    message: string,
    revokesSessions = false,
  ): Promise<void> {
    let updated: UserDetail | undefined;
    if (await this.run(async () => (updated = await call()), message)) {
      if (updated) {
        this.user.value.set(updated);
      }

      if (revokesSessions) {
        this.sessions.reload();
      }
    }
  }

  private async run(call: () => Promise<unknown>, message: string): Promise<boolean> {
    this.busy.set(true);
    this.problem.set(null);
    try {
      await call();
      this.notifier.announce(message);
      return true;
    } catch (error) {
      this.problem.set(toProblem(error));
      return false;
    } finally {
      this.busy.set(false);
    }
  }
}
