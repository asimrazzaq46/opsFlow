import { Injectable, computed, inject } from '@angular/core';
import { rxResource, takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { filter, map, merge } from 'rxjs';
import { TenantService } from '../../core/auth/tenant.service';
import { TicketCreationEligibilityDocument } from '../../core/graphql/generated';
import { GraphqlClient } from '../../core/graphql/graphql-client';
import { RealtimeService } from '../../core/realtime/realtime.service';

/** Why a Customer cannot open tickets; staff are never restricted. */
export type EligibilityReason = 'NotLinked' | 'Inactive';

interface EligibilityEvent {
  tenantId: string;
}

/**
 * Whether the signed-in user may open new tickets in the active workspace. The answer always comes from the server; the realtime
 * event only says "re-read", and a reconnect re-reads too, so a missed event cannot leave a stale answer. The server still enforces the
 * rule on every ticket creation, so this state is display-only.
 */
@Injectable({ providedIn: 'root' })
export class TicketEligibility {
  private readonly tenant = inject(TenantService);
  private readonly gql = inject(GraphqlClient);
  private readonly realtime = inject(RealtimeService);

  /** Tagged with its workspace so a response for a previous workspace never answers for the current one. */
  private readonly resource = rxResource({
    params: () => {
      const tenantId = this.tenant.tenantId();
      return tenantId && this.tenant.role() === 'Customer' ? { tenantId } : undefined;
    },
    stream: ({ params }) =>
      this.gql.query(TicketCreationEligibilityDocument).pipe(
        map((r) => ({
          tenantId: params.tenantId,
          canCreate: r.ticketCreationEligibility.canCreate,
          reason: r.ticketCreationEligibility.reason as EligibilityReason | null,
        })),
      ),
  });

  private readonly answer = computed(() => {
    const r = this.resource.hasValue() ? this.resource.value() : undefined;
    return r && r.tenantId === this.tenant.tenantId() ? r : null;
  });

  /** Staff may always start a ticket; a Customer only once the server confirmed an active linked contact. */
  readonly canCreate = computed(() => {
    const role = this.tenant.role();
    if (role === null) return false;
    return role !== 'Customer' || (this.answer()?.canCreate ?? false);
  });
  readonly reason = computed<EligibilityReason | null>(() => this.answer()?.reason ?? null);
  /** loading: no answer yet; error: the read failed (retry); ready: `canCreate` is authoritative. */
  readonly state = computed<'loading' | 'error' | 'ready'>(() => {
    if (this.tenant.role() !== 'Customer') return 'ready';
    if (this.answer()) return 'ready';
    return this.resource.status() === 'error' ? 'error' : 'loading';
  });

  constructor() {
    merge(
      this.realtime
        .on<EligibilityEvent>('customer.eligibility.changed')
        .pipe(filter((e) => e?.tenantId === this.tenant.tenantId())),
      this.realtime.reconnected,
    )
      .pipe(takeUntilDestroyed())
      .subscribe(() => this.reload());
  }

  reload(): void {
    this.resource.reload();
  }
}
