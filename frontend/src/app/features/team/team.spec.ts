import { HttpErrorResponse } from '@angular/common/http';
import { computed, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { provideTranslateService } from '@ngx-translate/core';
import { Subject, of, throwError } from 'rxjs';
import { InvitationsService } from '../../core/api/generated/services/invitations.service';
import { AuthService } from '../../core/auth/auth.service';
import { TenantService } from '../../core/auth/tenant.service';
import { Team } from './team';
import { TeamApi } from './team-api';

describe('Team', () => {
  const role = signal<string | null>('Agent');
  const tenantId = signal<string | null>('t1');
  const tenant = {
    role,
    tenantId,
    isStaff: computed(() => role() !== null && role() !== 'Customer'),
    isManager: computed(() => role() === 'TenantOwner' || role() === 'Manager'),
    isOwner: computed(() => role() === 'TenantOwner'),
  };
  const directory = vi.fn();
  const skills = vi.fn();
  const listInvitations = vi.fn();
  const createInvitation = vi.fn();
  const resendInvitation = vi.fn();
  const revokeInvitation = vi.fn();
  let fixture: ComponentFixture<Team>;
  const el = () => fixture.nativeElement as HTMLElement;
  const invitation = (id: string, role: string, expiresAtUtc: string) => ({
    id,
    email: `${id}@example.com`,
    role,
    createdAtUtc: '2026-01-01T00:00:00Z',
    sentAtUtc: '2026-01-01T00:00:00Z',
    expiresAtUtc,
  });
  const rowButtons = () => Array.from(el().querySelectorAll<HTMLButtonElement>('section[aria-label] ul button'));
  const inviteOptions = () =>
    Array.from(el().querySelector<HTMLSelectElement>('form select')?.options ?? []).map((o) => o.value);

  const create = async () => {
    fixture = TestBed.createComponent(Team);
    await fixture.whenStable();
  };

  beforeEach(() => {
    vi.clearAllMocks();
    role.set('Agent');
    tenantId.set('t1');
    directory.mockReturnValue(of({ items: [], page: 1, pageSize: 20, totalCount: 0 }));
    skills.mockReturnValue(of([]));
    listInvitations.mockReturnValue(of([]));
    createInvitation.mockReturnValue(of(undefined));
    resendInvitation.mockReturnValue(of(undefined));
    revokeInvitation.mockReturnValue(of(undefined));
    TestBed.configureTestingModule({
      imports: [Team],
      providers: [
        provideRouter([]),
        provideTranslateService(),
        { provide: TenantService, useValue: tenant },
        { provide: AuthService, useValue: { user: signal({ id: 'u1' }) } },
        { provide: TeamApi, useValue: { directory, skills } },
        { provide: InvitationsService, useValue: { listInvitations, createInvitation, resendInvitation, revokeInvitation } },
      ],
    });
  });

  it('shows an Agent the directory read-only: no invitation form or invitation request', async () => {
    await create();
    expect(directory).toHaveBeenCalled();
    expect(el().querySelector('form')).toBeNull();
    expect(el().textContent).not.toContain('invitations.title');
    expect(el().textContent).toContain('team.readOnly');
    expect(listInvitations).not.toHaveBeenCalled();
  });

  it('shows a Customer neither the directory nor any request', async () => {
    role.set('Customer');
    await create();
    expect(el().textContent).toContain('common.forbidden');
    expect(directory).not.toHaveBeenCalled();
    expect(listInvitations).not.toHaveBeenCalled();
  });

  it('offers a Manager only the Agent and Customer roles', async () => {
    role.set('Manager');
    await create();
    expect(inviteOptions()).toEqual(['Agent', 'Customer']);
    expect(listInvitations).toHaveBeenCalledWith({ tenantId: 't1', includeExpired: true });
  });

  it('offers an Owner every role', async () => {
    role.set('TenantOwner');
    await create();
    expect(inviteOptions()).toEqual(['Manager', 'Agent', 'Customer', 'TenantOwner']);
  });

  it('starts again on page 1 of the server directory when a filter changes', async () => {
    await create();
    directory.mockClear();
    const statusFilter = el().querySelectorAll<HTMLSelectElement>('[role="search"] select')[1];
    statusFilter.value = 'inactive';
    statusFilter.dispatchEvent(new Event('change'));
    await fixture.whenStable();
    expect(directory).toHaveBeenCalledWith(expect.objectContaining({ isActive: false, page: 1 }));
  });

  it('updates the available actions when the workspace (and so the role) changes', async () => {
    role.set('TenantOwner');
    await create();
    expect(el().querySelector('form')).not.toBeNull();

    role.set('Agent');
    tenantId.set('t2');
    await fixture.whenStable();
    expect(el().querySelector('form')).toBeNull();
    expect(el().textContent).toContain('team.readOnly');

    role.set('Manager');
    tenantId.set('t3');
    await fixture.whenStable();
    expect(inviteOptions()).toEqual(['Agent', 'Customer']);
  });

  it('disables the button and shows pending text while sending, then reports the result', async () => {
    role.set('Manager');
    const pending = new Subject<void>();
    createInvitation.mockReturnValue(pending);
    await create();
    const input = el().querySelector<HTMLInputElement>('input[type="email"]')!;
    input.value = 'new@example.com';
    input.dispatchEvent(new Event('input'));
    el().querySelector('form')!.dispatchEvent(new Event('submit'));
    await fixture.whenStable();

    const button = el().querySelector<HTMLButtonElement>('button[type="submit"]')!;
    expect(button.disabled).toBe(true);
    expect(button.textContent).toContain('invitations.sending');

    pending.next();
    pending.complete();
    await fixture.whenStable();
    expect(button.disabled).toBe(false);
    expect(el().querySelector('[role="status"]')!.textContent).toContain('invitations.sent');
  });

  it('shows pending and expired invitations with a visible state', async () => {
    role.set('TenantOwner');
    listInvitations.mockReturnValue(
      of([invitation('a', 'Agent', '2999-01-01T00:00:00Z'), invitation('b', 'Agent', '2000-01-01T00:00:00Z')]),
    );
    await create();
    expect(el().textContent).toContain('invitations.statePending');
    expect(el().textContent).toContain('invitations.stateExpired');
  });

  it('lets a Manager resend and revoke Agent invitations only and marks the others as Owner-managed', async () => {
    role.set('Manager');
    listInvitations.mockReturnValue(
      of([invitation('a', 'Agent', '2999-01-01T00:00:00Z'), invitation('m', 'Manager', '2999-01-01T00:00:00Z')]),
    );
    await create();
    expect(rowButtons()).toHaveLength(2); // resend + revoke of the Agent invitation
    expect(el().textContent).toContain('invitations.ownerOnly');
  });

  it('resends through the API, reports it and reloads the list', async () => {
    role.set('TenantOwner');
    listInvitations.mockReturnValue(of([invitation('a', 'Agent', '2000-01-01T00:00:00Z')]));
    await create();
    listInvitations.mockClear();
    rowButtons()[0].click();
    await fixture.whenStable();
    expect(resendInvitation).toHaveBeenCalledWith({ tenantId: 't1', invitationId: 'a' });
    expect(listInvitations).toHaveBeenCalledTimes(1);
    expect(el().querySelector('[role="status"]')!.textContent).toContain('invitations.resent');
  });

  it('explains a resend cooldown instead of a generic failure', async () => {
    role.set('TenantOwner');
    listInvitations.mockReturnValue(of([invitation('a', 'Agent', '2999-01-01T00:00:00Z')]));
    resendInvitation.mockReturnValue(
      throwError(() => new HttpErrorResponse({ status: 429, error: { code: 'INVITATION_RESEND_COOLDOWN' } })),
    );
    await create();
    rowButtons()[0].click();
    await fixture.whenStable();
    expect(el().querySelector('[role="alert"]')!.textContent).toContain('invitations.errors.cooldown');
  });

  it('reports an already-member conflict on create and keeps the typed address', async () => {
    role.set('Manager');
    createInvitation.mockReturnValue(
      throwError(() => new HttpErrorResponse({ status: 409, error: { code: 'INVITATION_ALREADY_MEMBER' } })),
    );
    await create();
    const input = el().querySelector<HTMLInputElement>('input[type="email"]')!;
    input.value = 'member@example.com';
    input.dispatchEvent(new Event('input'));
    el().querySelector('form')!.dispatchEvent(new Event('submit'));
    await fixture.whenStable();
    expect(el().textContent).toContain('invitations.errors.alreadyMember');
    expect(input.value).toBe('member@example.com');
  });
});
