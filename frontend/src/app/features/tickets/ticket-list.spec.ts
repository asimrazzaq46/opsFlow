import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { convertToParamMap, provideRouter, Router } from '@angular/router';
import { provideTranslateService } from '@ngx-translate/core';
import { of, Subject } from 'rxjs';
import { PagedResultOfTicketSummaryDto } from '../../core/api/generated/models';
import { TenantService } from '../../core/auth/tenant.service';
import { TicketList, parseTicketQuery } from './ticket-list';
import { TicketQuery, TicketsApi } from './tickets-api';

const page = (n: number): PagedResultOfTicketSummaryDto => ({ items: [], page: 1, pageSize: 25, totalCount: n });

describe('parseTicketQuery', () => {
  it('uses safe defaults', () => {
    expect(parseTicketQuery(convertToParamMap({}))).toEqual({
      search: undefined, status: undefined, priority: undefined, categoryId: undefined, state: undefined,
      sortBy: 'updatedAt', descending: true, page: 1, pageSize: 25,
    });
  });

  it('keeps only whitelisted values from the URL', () => {
    const q = parseTicketQuery(convertToParamMap({
      q: '  vpn  ', status: 'OnHold', priority: 'Urgent', category: '0b7e1c1a-4b9e-4c55-9a1e-6f0f0a5b2c11',
      state: 'Overdue', sort: 'priority', dir: 'asc', page: '3', size: '50',
    }));
    expect(q).toEqual({
      search: 'vpn', status: 'OnHold', priority: 'Urgent', categoryId: '0b7e1c1a-4b9e-4c55-9a1e-6f0f0a5b2c11',
      state: 'Overdue', sortBy: 'priority', descending: false, page: 3, pageSize: 50,
    });
    const bad = parseTicketQuery(convertToParamMap({ status: 'Hacked', sort: 'password', page: '-2', size: '5000', category: "1' or 1=1" }));
    expect(bad.status).toBeUndefined();
    expect(bad.sortBy).toBe('updatedAt');
    expect(bad.page).toBe(1);
    expect(bad.pageSize).toBe(25);
    expect(bad.categoryId).toBeUndefined();
  });
});

describe('TicketList', () => {
  const tenantId = signal<string | null>('W1');
  let list: ReturnType<typeof vi.fn>;

  beforeEach(() => {
    tenantId.set('W1');
    list = vi.fn((_q: TicketQuery) => of(page(60)));
    TestBed.configureTestingModule({
      providers: [
        provideRouter([{ path: 'tickets', component: TicketList }]),
        provideTranslateService(),
        { provide: TicketsApi, useValue: { list, categories: () => of([]) } },
        { provide: TenantService, useValue: { tenantId, current: signal(null), isStaff: signal(true) } },
      ],
    });
  });

  it('sends the URL query to the server once and reloads when the workspace changes', async () => {
    await TestBed.inject(Router).navigateByUrl('/tickets?status=Open&page=2');
    const fixture = TestBed.createComponent(TicketList);
    await fixture.whenStable();
    expect(list).toHaveBeenCalledTimes(1);
    expect(list.mock.calls[0][0]).toMatchObject({ status: 'Open', page: 2, pageSize: 25 });

    tenantId.set('W2');
    await fixture.whenStable();
    expect(list).toHaveBeenCalledTimes(2);
  });

  it('never shows a response that belongs to the previous workspace', async () => {
    const pending = new Subject<PagedResultOfTicketSummaryDto>();
    list.mockReturnValueOnce(of(page(5))).mockReturnValueOnce(pending);
    await TestBed.inject(Router).navigateByUrl('/tickets');
    const fixture = TestBed.createComponent(TicketList);
    await fixture.whenStable();
    const cmp = fixture.componentInstance as unknown as { page: () => PagedResultOfTicketSummaryDto | null };
    expect(cmp.page()?.totalCount).toBe(5);

    tenantId.set('W2'); // the W2 request is still running
    fixture.detectChanges();
    expect(cmp.page()).toBeNull();
  });
});
