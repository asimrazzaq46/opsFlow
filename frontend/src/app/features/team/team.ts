import { HttpErrorResponse } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, effect, inject, signal, untracked } from '@angular/core';
import { FormControl, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';
import { TranslatePipe } from '@ngx-translate/core';
import { Api } from '../../core/api/generated/api';
import { createInvitation, listInvitations, revokeInvitation } from '../../core/api/generated/functions';
import { InvitationDto, TenantRole } from '../../core/api/generated/models';
import { TenantService } from '../../core/auth/tenant.service';
import { GraphqlClient } from '../../core/graphql/graphql-client';
import { TeamMembersDocument, TeamMembersQuery } from '../../core/graphql/generated';
import { pascal } from '../../shared/format';

const PAGE_SIZE = 20;

@Component({
  selector: 'app-team',
  imports: [ReactiveFormsModule, TranslatePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <h1 class="text-2xl font-bold tracking-tight">{{ 'team.title' | translate }}</h1>

    <div class="mt-6 overflow-x-auto" aria-live="polite">
      @if (error()) {
        <p role="alert" class="text-danger">{{ 'team.loadError' | translate }} <button type="button" class="underline" (click)="load()">{{ 'common.retry' | translate }}</button></p>
      } @else if (data(); as d) {
        <table class="w-full min-w-[30rem] text-left text-sm">
          <thead class="text-muted"><tr><th class="py-2">{{ 'team.name' | translate }}</th><th>{{ 'customers.email' | translate }}</th><th>{{ 'team.role' | translate }}</th><th>{{ 'common.status' | translate }}</th></tr></thead>
          <tbody>
            @for (m of d.items; track m.id) {
              <tr class="border-t border-border"><td class="py-2 font-medium">{{ m.displayName }}</td><td>{{ m.email }}</td><td>{{ 'roles.' + pascal(m.role) | translate }}</td>
                <td>{{ (m.isActive ? 'common.active' : 'common.inactive') | translate }}</td></tr>
            }
          </tbody>
        </table>
        <nav class="mt-4 flex items-center justify-between text-sm">
          <button type="button" class="rounded-lg border border-border px-3 py-1 disabled:opacity-50" [disabled]="d.page <= 1" (click)="go(d.page - 1)">‹ {{ 'common.previous' | translate }}</button>
          <span>{{ d.page }} / {{ pages(d) }}</span>
          <button type="button" class="rounded-lg border border-border px-3 py-1 disabled:opacity-50" [disabled]="d.page >= pages(d)" (click)="go(d.page + 1)">{{ 'common.next' | translate }} ›</button>
        </nav>
      } @else { <p class="text-muted">{{ 'common.loading' | translate }}</p> }
    </div>

    <h2 class="mt-10 text-xl font-bold tracking-tight">{{ 'invitations.title' | translate }}</h2>
    <form [formGroup]="invite" (ngSubmit)="send()" class="mt-4 flex flex-wrap items-end gap-3 text-sm" novalidate>
      <label class="grid gap-1"><span>{{ 'customers.email' | translate }}</span>
        <input type="email" formControlName="email" class="rounded-lg border border-border bg-card px-2 py-1" [attr.aria-invalid]="invite.controls.email.invalid && invite.controls.email.touched" /></label>
      <label class="grid gap-1"><span>{{ 'team.role' | translate }}</span>
        <select formControlName="role" class="rounded-lg border border-border bg-card px-2 py-1">
          @for (r of roles(); track r) { <option [value]="r">{{ 'roles.' + r | translate }}</option> }
        </select></label>
      <button type="submit" [disabled]="busy()" class="rounded-lg bg-brand px-3 py-1.5 font-semibold text-brand-fg focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-brand disabled:opacity-60">{{ 'invitations.send' | translate }}</button>
    </form>
    @if (message(); as m) { <p role="status" class="mt-2 text-sm">{{ m | translate }}</p> }
    <ul class="mt-4 divide-y divide-border rounded-xl border border-border bg-card text-sm">
      @for (i of invitations(); track i.id) {
        <li class="flex flex-wrap items-center justify-between gap-2 px-4 py-2">
          <span>{{ i.email }} · {{ 'roles.' + i.role | translate }}</span>
          <button type="button" class="underline" (click)="revoke(i.id)">{{ 'invitations.revoke' | translate }}</button>
        </li>
      } @empty { <li class="px-4 py-3 text-muted">{{ 'invitations.empty' | translate }}</li> }
    </ul>
  `,
})
export class Team {
  private readonly gql = inject(GraphqlClient);
  private readonly api = inject(Api);
  protected readonly tenant = inject(TenantService);
  protected readonly pascal = pascal;

  protected readonly data = signal<TeamMembersQuery['teamMembers'] | null>(null);
  protected readonly error = signal(false);
  protected readonly invitations = signal<InvitationDto[]>([]);
  protected readonly busy = signal(false);
  protected readonly message = signal<string | null>(null);
  protected readonly invite = new FormGroup({
    email: new FormControl('', { nonNullable: true, validators: [Validators.required, Validators.email] }),
    role: new FormControl<TenantRole>('Agent', { nonNullable: true }),
  });
  private page = 1;

  constructor() {
    effect(() => {
      if (!this.tenant.tenantId()) return this.data.set(null);
      untracked(() => {
        this.go(1);
        this.loadInvitations();
      });
    });
  }

  // Owners may invite any staff role + customers; Managers only Agents/Customers (the server re-checks).
  protected roles = (): TenantRole[] => (this.tenant.role() === 'TenantOwner' ? ['Manager', 'Agent', 'Customer', 'TenantOwner'] : ['Agent', 'Customer']);
  protected pages = (d: NonNullable<TeamMembersQuery['teamMembers']>) => Math.max(1, Math.ceil(d.totalCount / PAGE_SIZE));

  protected go(page: number): void {
    this.page = page;
    this.load();
  }

  protected load(): void {
    this.error.set(false);
    this.gql.query(TeamMembersDocument, { page: this.page, pageSize: PAGE_SIZE }).subscribe({ next: (r) => this.data.set(r.teamMembers), error: () => this.error.set(true) });
  }

  private loadInvitations(): void {
    const tenantId = this.tenant.tenantId();
    if (tenantId) this.api.invoke(listInvitations, { tenantId }).subscribe({ next: (l) => this.invitations.set(l), error: () => this.invitations.set([]) });
  }

  protected send(): void {
    this.invite.markAllAsTouched();
    const tenantId = this.tenant.tenantId();
    if (this.invite.invalid || !tenantId || this.busy()) return;
    this.busy.set(true);
    this.message.set(null);
    this.api.invoke(createInvitation, { tenantId, body: this.invite.getRawValue() }).subscribe({
      next: () => {
        this.busy.set(false);
        this.invite.controls.email.reset();
        this.message.set('invitations.sent');
        this.loadInvitations();
      },
      error: (e: unknown) => {
        this.busy.set(false);
        this.message.set(e instanceof HttpErrorResponse && e.status === 403 ? 'common.forbidden' : e instanceof HttpErrorResponse && e.status === 409 ? 'invitations.duplicate' : 'invitations.error');
      },
    });
  }

  protected revoke(invitationId: string): void {
    const tenantId = this.tenant.tenantId();
    if (tenantId) this.api.invoke(revokeInvitation, { tenantId, invitationId }).subscribe({ next: () => this.loadInvitations(), error: () => this.message.set('invitations.error') });
  }
}
