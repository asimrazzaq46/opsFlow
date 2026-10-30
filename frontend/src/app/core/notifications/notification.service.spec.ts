import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Subject, of } from 'rxjs';
import { Api } from '../api/generated/api';
import { NotificationDto } from '../api/generated/models';
import { TenantService } from '../auth/tenant.service';
import { RealtimeService } from '../realtime/realtime.service';
import { NotificationCreatedEvent, NotificationService } from './notification.service';

const dto = (id: string, tenantId: string, isRead = false): NotificationDto => ({
  id,
  tenantId,
  isRead,
  type: 'MessageReceived',
  ticketId: 't',
  subject: 's',
  detail: null,
  createdAtUtc: new Date().toISOString(),
});

describe('NotificationService', () => {
  const tenantId = signal<string | null>(null);
  const invoke = vi.fn();
  const live = new Subject<NotificationCreatedEvent>();
  const reconnected = new Subject<void>();
  let service: NotificationService;

  beforeEach(() => {
    tenantId.set(null);
    invoke.mockReset();
    invoke.mockImplementation((_fn: unknown, params: { page?: number }) =>
      params.page ? of({ items: [dto('n1', 'A')], page: 1, pageSize: 20, totalCount: 1 }) : of({ count: 3 }),
    );
    TestBed.configureTestingModule({
      providers: [
        { provide: Api, useValue: { invoke } },
        { provide: TenantService, useValue: { tenantId } },
        { provide: RealtimeService, useValue: { on: () => live.asObservable(), reconnected: reconnected.asObservable() } },
      ],
    });
    service = TestBed.inject(NotificationService);
  });

  it('loads count and history for the active tenant and resyncs on reconnect', () => {
    tenantId.set('A');
    TestBed.tick();
    expect(service.unread()).toBe(3);
    expect(service.items().map((n) => n.id)).toEqual(['n1']);
    invoke.mockClear();
    reconnected.next();
    expect(invoke).toHaveBeenCalledTimes(2);
  });

  it('prepends live events of the active tenant once and ignores other tenants', () => {
    tenantId.set('A');
    TestBed.tick();
    live.next({ notification: dto('n2', 'B') });
    expect(service.items()).toHaveLength(1);
    live.next({ notification: dto('n2', 'A') });
    live.next({ notification: dto('n2', 'A') }); // duplicate delivery
    expect(service.items().map((n) => n.id)).toEqual(['n2', 'n1']);
    expect(service.unread()).toBe(4);
  });

  it('clears state when the tenant is switched away', () => {
    tenantId.set('A');
    TestBed.tick();
    tenantId.set(null);
    TestBed.tick();
    expect(service.items()).toEqual([]);
    expect(service.unread()).toBe(0);
  });
});
