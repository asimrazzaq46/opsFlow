import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Subject, of, throwError } from 'rxjs';
import { Api } from '../api/generated/api';
import { NotificationDto } from '../api/generated/models';
import { NotificationsService, TicketsService } from '../api/generated/services';
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
  const notificationsApi = {
    listMyNotifications: vi.fn(),
    getMyUnreadNotificationCount: vi.fn(),
    markNotificationRead: vi.fn(),
    markAllMyNotificationsRead: vi.fn(),
  };
  const live = new Subject<NotificationCreatedEvent>();
  const reconnected = new Subject<void>();
  let service: NotificationService;

  beforeEach(() => {
    signedIn.set(false);
    Object.values(notificationsApi).forEach((m) => m.mockReset());
    notificationsApi.listMyNotifications.mockImplementation(() => of({ items: [dto('n1', 'A')], page: 1, pageSize: 20, totalCount: 1 }));
    notificationsApi.getMyUnreadNotificationCount.mockImplementation(() => of({ count: 3 }));
    TestBed.configureTestingModule({
      providers: [
        { provide: NotificationsService, useValue: notificationsApi },
        { provide: TicketsService, useValue: { getTicket: vi.fn() } },
        { provide: Api, useValue: { invoke: vi.fn() } }, // used by TenantService, which this service injects
        { provide: AuthService, useValue: { isAuthenticated: signedIn, user: signal({ id: 'u1' }) } },
        {
          provide: RealtimeService,
          useValue: { on: () => live.asObservable(), reconnected: reconnected.asObservable(), state: signal('connected') },
        },
      ],
    });
    service = TestBed.inject(NotificationService);
  });

  it('loads the global count and history after sign-in and resyncs on reconnect', () => {
    signedIn.set(true);
    TestBed.tick();
    expect(service.unread()).toBe(3);
    expect(service.items().map((n) => n.id)).toEqual(['n1']);
    notificationsApi.listMyNotifications.mockClear();
    notificationsApi.getMyUnreadNotificationCount.mockClear();
    reconnected.next();
    expect(notificationsApi.listMyNotifications).toHaveBeenCalledTimes(1);
    expect(notificationsApi.getMyUnreadNotificationCount).toHaveBeenCalledTimes(1);
  });

  it('prepends live events of any workspace once and ignores duplicate deliveries', () => {
    signedIn.set(true);
    TestBed.tick();
    live.next({ notification: dto('n2', 'B') });
    live.next({ notification: dto('n2', 'B') });
    expect(service.items().map((n) => n.id)).toEqual(['n2', 'n1']);
    expect(service.unread()).toBe(4);
  });

  it('settles a burst of live events with ONE server count, ignoring a count response that predates them', () => {
    vi.useFakeTimers();
    try {
      signedIn.set(true);
      TestBed.tick();
      const late = new Subject<{ count: number }>();
      notificationsApi.getMyUnreadNotificationCount.mockImplementationOnce(() => late);
      reconnected.next(); // count request in flight
      live.next({ notification: dto('n2', 'B') });
      live.next({ notification: dto('n3', 'B') });
      late.next({ count: 3 }); // requested before the events: must not overwrite
      expect(service.unread()).toBe(5);
      notificationsApi.getMyUnreadNotificationCount.mockClear();
      notificationsApi.getMyUnreadNotificationCount.mockImplementation(() => of({ count: 4 }));
      const changes = service.changes();
      vi.advanceTimersByTime(500);
      expect(notificationsApi.getMyUnreadNotificationCount).toHaveBeenCalledTimes(1);
      expect(service.unread()).toBe(4);
      expect(service.changes()).toBe(changes + 1);
    } finally {
      vi.useRealTimers();
    }
  });

  it('marks one notification read with its own tenant, once, and settles the badge from the server count', () => {
    signedIn.set(true);
    TestBed.tick();
    notificationsApi.markNotificationRead.mockImplementation(() => of(undefined));
    notificationsApi.getMyUnreadNotificationCount.mockImplementation(() => of({ count: 2 }));
    const n = service.items()[0];
    service.markRead(n);
    service.markRead(n);
    expect(notificationsApi.markNotificationRead).toHaveBeenCalledTimes(1);
    expect(notificationsApi.markNotificationRead).toHaveBeenCalledWith({ tenantId: 'A', notificationId: 'n1' });
    expect(service.items()[0].isRead).toBe(true);
    expect(service.unread()).toBe(2);
    expect(service.actionError()).toBeNull();
  });

  it('reports a failed read as an action error without touching the loaded list', () => {
    signedIn.set(true);
    TestBed.tick();
    notificationsApi.markNotificationRead.mockImplementation(() => throwError(() => new Error('x')));
    service.markRead(service.items()[0]);
    expect(service.actionError()).toBe('notifications.errors.read');
    expect(service.error()).toBe(false);
    expect(service.items().length).toBe(1);
    expect(service.unread()).toBe(3);
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
