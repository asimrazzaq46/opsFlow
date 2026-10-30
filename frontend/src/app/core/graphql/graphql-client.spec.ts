import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { describe, expect, it, vi } from 'vitest';
import { authInterceptor } from '../auth/auth.interceptor';
import { AuthService } from '../auth/auth.service';
import { TenantService } from '../auth/tenant.service';
import { DashboardDocument } from './generated';
import { GraphqlClient, GraphqlError } from './graphql-client';

function setup(tenantId: string | null) {
  const auth = { accessToken: () => 'tok', refresh: vi.fn() };
  TestBed.configureTestingModule({
    providers: [
      provideHttpClient(withInterceptors([authInterceptor])),
      provideHttpClientTesting(),
      { provide: AuthService, useValue: auth },
      { provide: TenantService, useValue: { tenantId: () => tenantId } },
    ],
  });
  return { client: TestBed.inject(GraphqlClient), http: TestBed.inject(HttpTestingController) };
}

describe('GraphqlClient', () => {
  it('posts the printed typed document to the tenant route with the Bearer token', () => {
    const { client, http } = setup('t-1');
    let result: unknown;
    client.query(DashboardDocument, { filter: {} }).subscribe((r) => (result = r));

    const req = http.expectOne('/api/v1/tenants/t-1/graphql');
    expect(req.request.headers.get('Authorization')).toBe('Bearer tok');
    expect(req.request.body.query).toContain('query Dashboard');
    req.flush({ data: { dashboard: { timeZone: 'UTC' } } });
    expect(result).toEqual({ dashboard: { timeZone: 'UTC' } });
  });

  it('turns GraphQL errors into a coded GraphqlError', () => {
    const { client, http } = setup('t-1');
    let error: unknown;
    client.query(DashboardDocument).subscribe({ error: (e) => (error = e) });

    http.expectOne('/api/v1/tenants/t-1/graphql').flush({ data: null, errors: [{ message: 'no', extensions: { code: 'FORBIDDEN' } }] });
    expect(error).toBeInstanceOf(GraphqlError);
    expect((error as GraphqlError).code).toBe('FORBIDDEN');
  });

  it('refuses to run without an active workspace', () => {
    const { client, http } = setup(null);
    let failed = false;
    client.query(DashboardDocument).subscribe({ error: () => (failed = true) });
    http.expectNone(() => true);
    expect(failed).toBe(true);
  });
});
