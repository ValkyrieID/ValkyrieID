import { DOCUMENT } from '@angular/common';
import { provideHttpClient, withFetch, withInterceptors } from '@angular/common/http';
import {
  ApplicationConfig,
  Injectable,
  inject,
  provideAppInitializer,
  provideBrowserGlobalErrorListeners,
} from '@angular/core';
import { Title } from '@angular/platform-browser';
import {
  RouterStateSnapshot,
  TitleStrategy,
  provideRouter,
  withComponentInputBinding,
} from '@angular/router';
import { routes } from './app.routes';
import { authInterceptor } from './auth/auth.interceptor';
import { AuthService } from './auth/auth.service';
import { APP_SETTINGS, readSettings } from './auth/settings';

/** "Users – ValkyrieID Admin": every page has a unique, descriptive title (WCAG 2.4.2). */
@Injectable({ providedIn: 'root' })
export class AdminTitleStrategy extends TitleStrategy {
  private readonly title = inject(Title);

  override updateTitle(snapshot: RouterStateSnapshot): void {
    const page = this.buildTitle(snapshot);
    this.title.setTitle(page ? `${page} – ValkyrieID Admin` : 'ValkyrieID Admin');
  }
}

export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
    { provide: APP_SETTINGS, useFactory: () => readSettings(inject(DOCUMENT)) },
    provideHttpClient(withFetch(), withInterceptors([authInterceptor])),
    provideRouter(routes, withComponentInputBinding()),
    { provide: TitleStrategy, useExisting: AdminTitleStrategy },
    provideAppInitializer(() => inject(AuthService).initialize(window.location.href)),
  ],
};
