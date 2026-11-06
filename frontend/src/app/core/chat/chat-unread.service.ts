import { Injectable, effect, inject, signal, untracked } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { Subject, catchError, debounceTime, filter, map, merge, of, switchMap } from 'rxjs';
import { AuthService } from '../auth/auth.service';
import { TenantService } from '../auth/tenant.service';
import { ChatUnreadSummaryDocument } from '../graphql/generated';
import { GraphqlClient } from '../graphql/graphql-client';
import { RealtimeService } from '../realtime/realtime.service';
import { activeWorkspaceEvents } from '../realtime/workspace-events';

/** Translation key of the accessible "N unread messages" text (EN/IT need singular and plural). */
export const unreadKey = (count: number): string => (count === 1 ? 'chat.unread.one' : 'chat.unread.other');

/** Badges stay small: larger counts are capped visually, the accessible text keeps the exact number. */
export const compactCount = (count: number): string => (count > 99 ? '99+' : String(count));

/** Coalesces bursts of messages and acknowledgements into one request. */
const REFRESH_DEBOUNCE_MS = 400;

/**
 * Unread public chat messages of the signed-in user in the ACTIVE workspace. The server is the only source of the number; this service
 * just decides when to ask again (workspace change, new message from someone else, own acknowledgement, reconnect).
 */
@Injectable({ providedIn: 'root' })
export class ChatUnreadService {
  private readonly gql = inject(GraphqlClient);
  private readonly tenant = inject(TenantService);
  private readonly auth = inject(AuthService);
  private readonly requests = new Subject<void>();

  /** Never shows a previous workspace's or user's count: it is reset before the new one loads. */
  readonly total = signal(0);

  constructor() {
    effect(() => {
      this.tenant.tenantId();
      this.auth.user()?.id;
      untracked(() => {
        this.total.set(0);
        this.refresh();
      });
    });

    const realtime = inject(RealtimeService);
    merge(
      activeWorkspaceEvents<{ tenantId: string; message: { senderUserId: string | null } }>('ticket.message.created').pipe(
        filter((e) => e.message.senderUserId !== this.auth.user()?.id), // own messages never change the caller's count
      ),
      realtime.reconnected,
      this.requests,
    )
      .pipe(
        debounceTime(REFRESH_DEBOUNCE_MS),
        map(() => this.tenant.tenantId()),
        filter((tenantId): tenantId is string => !!tenantId && !!this.auth.user()),
        switchMap((tenantId) =>
          this.gql.query(ChatUnreadSummaryDocument).pipe(
            map((r) => ({ tenantId, total: r.chatUnreadSummary.total })),
            catchError(() => of(null)), // keep the last known count; the next trigger asks again
          ),
        ),
        takeUntilDestroyed(),
      )
      .subscribe((r) => {
        if (r && r.tenantId === this.tenant.tenantId()) this.total.set(r.total);
      });
  }

  refresh(): void {
    this.requests.next();
  }
}
