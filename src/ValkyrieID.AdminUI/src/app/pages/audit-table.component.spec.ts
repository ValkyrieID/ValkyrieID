import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { AuditEntryDto } from '../api/models';
import { AuditTableComponent } from './audit-table.component';

const entry: AuditEntryDto = {
  id: 'e0000000-0000-4000-8000-000000000001',
  timestamp: '2026-11-09T10:00:00Z',
  eventType: 'user.updated',
  success: false,
  subjectId: '5ab0c0de-0000-4000-8000-000000000002',
  actor: 'admin:1a2b3c4d-0000-4000-8000-000000000003',
  clientId: 'valkyrieid-admin',
  ipAddress: '203.0.113.7',
  details: { changed: ['email'], reason: 'test' },
};

describe('AuditTableComponent', () => {
  async function render() {
    TestBed.configureTestingModule({ providers: [provideRouter([])] });
    const fixture = TestBed.createComponent(AuditTableComponent);
    fixture.componentRef.setInput('entries', [entry]);
    await fixture.whenStable();
    return { fixture, element: fixture.nativeElement as HTMLElement };
  }

  it('shows a compact row with a shortened actor', async () => {
    const { element } = await render();

    const rows = element.querySelectorAll('tbody tr');
    expect(rows.length).toBe(1);
    expect(rows[0].textContent).toContain('user.updated');
    expect(rows[0].textContent).toContain('Failure');
    expect(rows[0].querySelector('app-id [aria-hidden="true"]')?.textContent).toBe(
      'admin:1a2b3c4d…',
    );
    // Long identifiers and details only appear when expanded.
    expect(rows[0].textContent).not.toContain('203.0.113.7');
  });

  it('expands and collapses the details with a disclosure button', async () => {
    const { fixture, element } = await render();
    const button = element.querySelector('button[aria-expanded]') as HTMLButtonElement;

    expect(button.getAttribute('aria-expanded')).toBe('false');
    expect(button.hasAttribute('aria-controls')).toBe(false);
    expect(button.textContent).toContain('Details of user.updated');

    button.click();
    await fixture.whenStable();

    expect(button.getAttribute('aria-expanded')).toBe('true');
    const detail = element.querySelector(`#${button.getAttribute('aria-controls')}`) as HTMLElement;
    expect(detail.getAttribute('colspan')).toBe('5');
    expect(detail.textContent).toContain(entry.actor);
    expect(detail.textContent).toContain(entry.subjectId);
    expect(detail.textContent).toContain('203.0.113.7');
    expect(detail.textContent).toContain('["email"]');
    expect(detail.textContent).toContain('test');
    expect(detail.querySelector('a')?.getAttribute('href')).toBe(`/users/${entry.subjectId}`);

    button.click();
    await fixture.whenStable();

    expect(button.getAttribute('aria-expanded')).toBe('false');
    expect(element.querySelectorAll('tbody tr').length).toBe(1);
  });
});
