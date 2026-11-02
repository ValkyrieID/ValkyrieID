import { HttpErrorResponse } from '@angular/common/http';
import { toProblem } from './problem';

describe('toProblem', () => {
  it('maps RFC 9457 validation errors to messages and fields', () => {
    const problem = toProblem(
      new HttpErrorResponse({
        status: 400,
        error: {
          title: 'One or more validation errors occurred.',
          status: 400,
          errors: {
            '': ['The authorization_code grant needs at least one redirect URI.'],
            RedirectUris: ['Not an https URL.'],
          },
        },
      }),
    );

    expect(problem.status).toBe(400);
    expect(problem.title).toBe('One or more validation errors occurred.');
    expect(problem.messages).toEqual([
      'The authorization_code grant needs at least one redirect URI.',
    ]);
    expect(problem.fields).toEqual({ redirectUris: ['Not an https URL.'] });
  });

  it('uses the detail of a conflict', () => {
    const problem = toProblem(
      new HttpErrorResponse({
        status: 409,
        error: { title: 'Conflict', detail: 'The last enabled admin cannot be disabled.' },
      }),
    );

    expect(problem.messages).toEqual(['The last enabled admin cannot be disabled.']);
  });

  it('describes network errors and bodies that are not problem details', () => {
    expect(toProblem(new HttpErrorResponse({ status: 0 })).title).toContain("couldn't be reached");
    expect(toProblem(new HttpErrorResponse({ status: 404, error: 'Not found' })).title).toBe(
      'It no longer exists.',
    );
    expect(toProblem(new Error('x')).status).toBe(0);
  });
});
