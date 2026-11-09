import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  ElementRef,
  inject,
  viewChild,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { Title } from '@angular/platform-browser';
import { NavigationEnd, Router, RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { filter } from 'rxjs';
import { AuthService } from './auth/auth.service';
import { Notifier } from './shared/util';
import { ThemeSwitchComponent } from './shared/theme-switch.component';

@Component({
  selector: 'app-root',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterOutlet, RouterLink, RouterLinkActive, ThemeSwitchComponent],
  templateUrl: './app.html',
})
export class App {
  protected readonly auth = inject(AuthService);
  protected readonly notifier = inject(Notifier);
  protected readonly router = inject(Router);
  private readonly title = inject(Title);
  private readonly main = viewChild<ElementRef<HTMLElement>>('main');

  protected readonly nav = [
    {
      id: 'overview',
      label: null,
      items: [{ path: '/', label: 'Dashboard', exact: true }],
    },
    {
      id: 'identity',
      label: 'Identity',
      items: [
        { path: '/users', label: 'Users', exact: false },
        { path: '/roles', label: 'Roles', exact: false },
        { path: '/groups', label: 'Groups', exact: false },
      ],
    },
    {
      id: 'applications',
      label: 'Applications',
      items: [
        { path: '/clients', label: 'Clients', exact: false },
        { path: '/resources', label: 'API resources', exact: false },
      ],
    },
    {
      id: 'system',
      label: 'System',
      items: [
        { path: '/signing-keys', label: 'Signing keys', exact: false },
        { path: '/audit', label: 'Audit log', exact: false },
        { path: '/settings', label: 'Settings', exact: false },
      ],
    },
  ] as const;

  constructor() {
    // After client-side navigation, move focus to the new page and announce its title, like a page load would.
    let first = true;
    this.router.events
      .pipe(
        filter((e) => e instanceof NavigationEnd),
        takeUntilDestroyed(inject(DestroyRef)),
      )
      .subscribe(() => {
        if (first) {
          first = false;
          return;
        }

        this.main()?.nativeElement.focus();
        this.notifier.announce(this.title.getTitle());
      });
  }

  protected skipToContent(event: Event): void {
    event.preventDefault();
    this.main()?.nativeElement.focus();
  }

  protected signIn(): void {
    void this.auth.signIn(this.router.url, false);
  }

  protected renew(): void {
    void this.auth.signIn(this.router.url, true);
  }

  protected stepUp(): void {
    void this.auth.signIn(this.router.url, false, true);
  }

  protected signOut(): void {
    void this.auth.signOut();
  }
}
