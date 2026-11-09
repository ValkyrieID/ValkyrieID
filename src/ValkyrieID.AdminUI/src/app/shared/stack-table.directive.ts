import { Directive, ElementRef, afterEveryRender, inject } from '@angular/core';

/**
 * Lets a `table.data` stack below 48rem: each row becomes a card and each cell a "label: value" line (styles/stacked-tables.css).
 * Changing a table's `display` can drop its semantics in some browsers, so this sets the explicit ARIA table roles,
 * and copies each column header's text to the cells' `data-label` for the visual labels. A header with only visually
 * hidden text (e.g. "Actions") gives no visual label; a cell spanning every column (an expanded detail row) neither.
 */
@Directive({
  selector: 'table[appStack]',
  host: { class: 'stack', role: 'table' },
})
export class StackTableDirective {
  private readonly table = inject<ElementRef<HTMLTableElement>>(ElementRef).nativeElement;

  constructor() {
    afterEveryRender({ write: () => this.apply() });
  }

  private apply(): void {
    const labels = Array.from(this.table.tHead?.rows[0]?.cells ?? [], labelOf);
    for (const section of [this.table.tHead, ...Array.from(this.table.tBodies)]) {
      if (!section) {
        continue;
      }

      set(section, 'role', 'rowgroup');
      for (const row of Array.from(section.rows)) {
        set(row, 'role', 'row');
        let column = 0;
        for (const cell of Array.from(row.cells)) {
          const header = cell.tagName === 'TH';
          set(
            cell,
            'role',
            header ? (section === this.table.tHead ? 'columnheader' : 'rowheader') : 'cell',
          );
          const label = cell.colSpan >= labels.length ? '' : (labels[column] ?? '');
          if (section !== this.table.tHead && label) {
            set(cell, 'data-label', label);
          } else {
            cell.removeAttribute('data-label');
          }

          column += cell.colSpan;
        }
      }
    }
  }
}

function labelOf(cell: HTMLTableCellElement): string {
  const text = cell.textContent?.trim() ?? '';
  const hidden = Array.from(cell.querySelectorAll('.visually-hidden'), (e) => e.textContent ?? '')
    .join('')
    .trim();
  return text === hidden ? '' : text;
}

function set(element: Element, name: string, value: string): void {
  if (element.getAttribute(name) !== value) {
    element.setAttribute(name, value);
  }
}
