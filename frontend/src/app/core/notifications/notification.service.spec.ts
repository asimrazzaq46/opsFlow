import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Subject, of } from 'rxjs';
import { Api } from '../api/generated/api';
import { NotificationDto } from '../api/generated/models';
import { AuthService } from '../auth/auth.service';
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
  const signedIn = signal(false);
  const invoke = vi.fn();
  const live = new Subject<NotificationCreatedEvent>();
  const reconnected = new Subject<void>();
  let service: NotificationService;

  beforeEach(() => {
    signedIn.set(false);
    invoke.mockReset();
    invoke.mockImplementation((_fn: unknown, params: { page?: number }) =>
      params.page ? of({ items: [dto('n1', 'A')], page: 1, pageSize: 20, totalCount: 1 }) : of({ count: 3 }),
    );
    TestBed.configureTestingModule({
      providers: [
        { provide: Api, useValue: { invoke } },
        { provide: AuthService, useValue: { isAuthenticated: signedIn } },
        { provide: RealtimeService, useValue: { on: () => live.asObservable(), reconnected: reconnected.asObservable() } },
      ],
    });
    service = TestBed.inject(NotificationService);
  });

  it('loads the global count and history after sign-in and resyncs on reconnect', () => {
    signedIn.set(true);
    TestBed.tick();
    expect(service.unread()).toBe(3);
    expect(service.items().map((n) => n.id)).toEqual(['n1']);
    invoke.mockClear();
    reconnected.next();
    expect(invoke).toHaveBeenCalledTimes(2);
  });

  it('prepends live events of any workspace once and ignores duplicate deliveries', () => {
    signedIn.set(true);
    TestBed.tick();
    live.next({ notification: dto('n2', 'B') });
    live.next({ notification: dto('n2', 'B') });
    expect(service.items().map((n) => n.id)).toEqual(['n2', 'n1']);
    expect(service.unread()).toBe(4);
  });

  it('clears state on sign-out', () => {
    signedIn.set(true);
    TestBed.tick();
    signedIn.set(false);
    TestBed.tick();
    expect(service.items()).toEqual([]);
    expect(service.unread()).toBe(0);
  });
});
