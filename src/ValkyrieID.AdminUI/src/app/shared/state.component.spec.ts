import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { EmptyComponent, LoadingComponent } from './state.component';

@Component({
  imports: [LoadingComponent, EmptyComponent],
  template: `
    <app-loading />
    <app-empty message="No clients yet." hint="Create one to get started.">
      <a href="/new">Create the first client</a>
    </app-empty>
  `,
})
class Host {}

describe('state components', () => {
  it('announces loading as a status', () => {
    const fixture = TestBed.createComponent(Host);
    fixture.detectChanges();

    const status = (fixture.nativeElement as HTMLElement).querySelector('[role="status"]');
    expect(status?.textContent?.trim()).toBe('Loading…');
    expect(status?.querySelector('.spinner')?.getAttribute('aria-hidden')).toBe('true');
  });

  it('shows the empty message, hint, and action, with a decorative icon', () => {
    const fixture = TestBed.createComponent(Host);
    fixture.detectChanges();

    const empty = (fixture.nativeElement as HTMLElement).querySelector(
      '.state-empty',
    ) as HTMLElement;
    expect(empty.querySelector('.state-title')?.textContent).toBe('No clients yet.');
    expect(empty.querySelector('.state-hint')?.textContent).toBe('Create one to get started.');
    expect(empty.querySelector('a')?.textContent).toBe('Create the first client');
    expect(empty.querySelector('svg')?.getAttribute('aria-hidden')).toBe('true');
  });
});
