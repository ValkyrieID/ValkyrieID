import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { AdminApi } from '../../api/admin-api';
import { Problem, toProblem } from '../../api/problem';
import { ProblemComponent } from '../../shared/problem.component';
import { Notifier, lines, optional } from '../../shared/util';

@Component({
  selector: 'app-resource-create-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, RouterLink, ProblemComponent],
  template: `
    <p><a routerLink="/resources">API resources</a></p>
    <h1>New API resource</h1>
    <app-problem [problem]="problem()" />
    <form class="form" (ngSubmit)="create()">
      <div class="field">
        <label for="resource-name">Name</label>
        <input
          id="resource-name"
          name="name"
          [(ngModel)]="name"
          required
          autocomplete="off"
          autocapitalize="none"
          spellcheck="false"
          aria-describedby="resource-name-hint"
        />
        <p class="hint" id="resource-name-hint">
          The audience (<code>aud</code>) of its access tokens, e.g. <code>orders-api</code>. Can't
          be changed later.
        </p>
      </div>
      <div class="field">
        <label for="resource-display">Display name <span class="optional">(optional)</span></label>
        <input
          id="resource-display"
          name="displayName"
          [(ngModel)]="displayName"
          autocomplete="off"
        />
      </div>
      <div class="field">
        <label for="resource-scopes">Scopes <span class="optional">(one per line)</span></label>
        <textarea
          id="resource-scopes"
          name="scopes"
          rows="3"
          [(ngModel)]="scopes"
          spellcheck="false"
          aria-describedby="resource-scopes-hint"
        ></textarea>
        <p class="hint" id="resource-scopes-hint">
          E.g. <code>orders.read</code>. You can add descriptions afterwards.
        </p>
      </div>
      <div class="actions">
        <button type="submit" [disabled]="busy()">Create API resource</button>
      </div>
    </form>
  `,
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
