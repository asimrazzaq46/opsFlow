import { HttpErrorResponse } from '@angular/common/http';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { provideTranslateService } from '@ngx-translate/core';
import { of, throwError } from 'rxjs';
import { AuthApi } from '../auth-api';
import { ResetPassword } from './reset-password';

describe('ResetPassword', () => {
  const api = { resetPassword: vi.fn() };
  let fixture: ComponentFixture<ResetPassword>;
  let navigate: ReturnType<typeof vi.spyOn>;
  const el = () => fixture.nativeElement as HTMLElement;

  const create = async (email?: string, token?: string) => {
    fixture = TestBed.createComponent(ResetPassword);
    fixture.componentRef.setInput('email', email);
    fixture.componentRef.setInput('token', token);
    await fixture.whenStable();
  };
  const fill = (password: string, confirm = password) => fixture.componentInstance['form'].setValue({ password, confirm });

  beforeEach(() => {
    vi.clearAllMocks();
    TestBed.configureTestingModule({
      imports: [ResetPassword],
      providers: [provideTranslateService(), provideRouter([]), { provide: AuthApi, useValue: api }],
    });
    navigate = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
  });

  it('removes the link parameters from the address bar and submits them from memory', async () => {
    await create('ada@example.com', 'tok');
    expect(navigate).toHaveBeenCalledWith([], { queryParams: {}, replaceUrl: true });

    api.resetPassword.mockReturnValue(of(undefined));
    fill('longpass');
    fixture.componentInstance['submit']();
    await fixture.whenStable();
    expect(api.resetPassword).toHaveBeenCalledWith('ada@example.com', 'tok', 'longpass');
    expect(el().textContent).toContain('authFlow.reset.doneTitle');
  });

  it('blocks short or mismatching passwords client-side', async () => {
    await create('ada@example.com', 'tok');
    fill('short');
    fixture.componentInstance['submit']();
    fill('longpass', 'other123');
    fixture.componentInstance['submit']();
    expect(api.resetPassword).not.toHaveBeenCalled();
  });

  it('shows the invalid-link state for a rejected token and for missing parameters', async () => {
    await create('ada@example.com', 'tok');
    api.resetPassword.mockReturnValue(throwError(() => new HttpErrorResponse({ status: 400, error: { code: 'BAD_REQUEST' } })));
    fill('longpass');
    fixture.componentInstance['submit']();
    await fixture.whenStable();
    expect(el().textContent).toContain('authFlow.reset.invalidTitle');

    await create(undefined, undefined);
    expect(el().textContent).toContain('authFlow.reset.invalidTitle');
  });
});
