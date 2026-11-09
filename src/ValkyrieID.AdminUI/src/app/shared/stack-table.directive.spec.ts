import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { StackTableDirective } from './stack-table.directive';

@Component({
  imports: [StackTableDirective],
  template: `
    <table appStack>
      <thead>
        <tr>
          <th scope="col">Name</th>
          <th scope="col">Status</th>
          <th scope="col"><span class="visually-hidden">Actions</span></th>
        </tr>
      </thead>
      <tbody>
        @for (row of rows(); track row) {
          <tr>
            <td>{{ row }}</td>
            <td>Enabled</td>
            <td><button type="button">Remove</button></td>
          </tr>
        }
        <tr>
          <td colspan="3">Detail</td>
        </tr>
      </tbody>
    </table>
  `,
})
class Host {
  readonly rows = signal(['a']);
}

describe('StackTableDirective', () => {
  async function render() {
    const fixture = TestBed.createComponent(Host);
    await fixture.whenStable();
    return { fixture, table: fixture.nativeElement.querySelector('table') as HTMLTableElement };
  }

  it('sets the explicit table roles', async () => {
    const { table } = await render();

    expect(table.getAttribute('role')).toBe('table');
    expect(table.classList).toContain('stack');
    expect(table.tHead?.getAttribute('role')).toBe('rowgroup');
    expect(table.tBodies[0].getAttribute('role')).toBe('rowgroup');
    expect(table.rows[0].getAttribute('role')).toBe('row');
    expect(table.rows[0].cells[0].getAttribute('role')).toBe('columnheader');
    expect(table.rows[1].cells[0].getAttribute('role')).toBe('cell');
  });

  it('labels the cells with their column header', async () => {
    const { table } = await render();
    const cells = table.rows[1].cells;

    expect(cells[0].dataset['label']).toBe('Name');
    expect(cells[1].dataset['label']).toBe('Status');
    // Visually hidden headers and full-width rows get no visual label.
    expect(cells[2].hasAttribute('data-label')).toBe(false);
    expect(table.rows[2].cells[0].hasAttribute('data-label')).toBe(false);
    expect(table.rows[0].cells[0].hasAttribute('data-label')).toBe(false);
  });

  it('labels rows added later', async () => {
    const { fixture, table } = await render();

    fixture.componentInstance.rows.set(['a', 'b']);
    await fixture.whenStable();

    expect(table.rows[2].getAttribute('role')).toBe('row');
    expect(table.rows[2].cells[0].dataset['label']).toBe('Name');
  });
});
