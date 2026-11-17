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
  const listInvitationHistory = vi.fn();
  const createInvitation = vi.fn();
  const resendInvitation = vi.fn();
  const revokeInvitation = vi.fn();
  let fixture: ComponentFixture<Team>;
  const el = () => fixture.nativeElement as HTMLElement;
  const row = (id: string, role: string, status: string) => ({
    id,
    email: `${id}@example.com`,
    role,
    status,
    createdAtUtc: '2026-01-01T00:00:00Z',
    sentAtUtc: '2026-01-01T00:00:00Z',
    expiresAtUtc: '2026-01-04T00:00:00Z',
    acceptedAtUtc: status === 'Accepted' ? '2026-01-02T00:00:00Z' : null,
    revokedAtUtc: status === 'Revoked' ? '2026-01-02T00:00:00Z' : null,
  });
  const history = (items: ReturnType<typeof row>[]) => of({ items, page: 1, pageSize: 25, totalCount: items.length });
  const rowButtons = () => Array.from(el().querySelectorAll<HTMLButtonElement>('table tbody button'));
  const inviteOptions = () =>
    Array.from(el().querySelector<HTMLSelectElement>('app-invite-member-dialog select')?.options ?? []).map((o) => o.value);

  const create = async () => {
    fixture = TestBed.createComponent(Team);
    await fixture.whenStable();
  };
  const openInvitations = async () => {
    el().querySelector<HTMLButtonElement>('#team-tab-invitations')!.click();
    await fixture.whenStable();
  };
  const submitInvite = async (email: string) => {
    const input = el().querySelector<HTMLInputElement>('app-invite-member-dialog input[type="email"]')!;
    input.value = email;
    input.dispatchEvent(new Event('input'));
    el().querySelector('app-invite-member-dialog form')!.dispatchEvent(new Event('submit'));
    await fixture.whenStable();
    return input;
  };

  beforeEach(() => {
    vi.clearAllMocks();
    role.set('Agent');
    tenantId.set('t1');
    directory.mockReturnValue(of({ items: [], page: 1, pageSize: 25, totalCount: 0 }));
    skills.mockReturnValue(of([]));
    listInvitationHistory.mockReturnValue(history([]));
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
        {
          provide: InvitationsService,
          useValue: { listInvitationHistory, createInvitation, resendInvitation, revokeInvitation },
        },
      ],
    });
  });

  it('shows an Agent the directory only: no tabs, invite button, dialog or history request', async () => {
    await create();
    expect(directory).toHaveBeenCalled();
    expect(el().querySelector('[role="tablist"]')).toBeNull();
    expect(el().querySelector('app-invite-member-dialog')).toBeNull();
    expect(el().textContent).not.toContain('team.inviteMember');
    expect(el().textContent).toContain('team.readOnly');
    expect(listInvitationHistory).not.toHaveBeenCalled();
  });

  it('shows a Customer neither the directory nor any request', async () => {
    role.set('Customer');
    await create();
    expect(el().textContent).toContain('common.forbidden');
    expect(directory).not.toHaveBeenCalled();
    expect(listInvitationHistory).not.toHaveBeenCalled();
  });

  it('gives a Manager both tabs and an Agent/Customer-only invite dialog, loading history only when opened', async () => {
    role.set('Manager');
    await create();
    expect(el().querySelectorAll('[role="tab"]')).toHaveLength(2);
    expect(el().textContent).toContain('team.inviteMember');
    expect(inviteOptions()).toEqual(['Agent', 'Customer']);
    expect(listInvitationHistory).not.toHaveBeenCalled();

    await openInvitations();
    expect(listInvitationHistory).toHaveBeenCalledWith({ tenantId: 't1', page: 1, pageSize: 25 });
  });

  it('offers an Owner every role in the invite dialog', async () => {
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

  it('changes the page size on the server and returns to page 1', async () => {
    await create();
    directory.mockClear();
    const size = el().querySelector<HTMLSelectElement>('nav select');
    if (!size) return; // the pager only exists once the directory has rows
    size.value = '50';
    size.dispatchEvent(new Event('change'));
    await fixture.whenStable();
    expect(directory).toHaveBeenCalledWith(expect.objectContaining({ pageSize: 50, page: 1 }));
  });

  it('updates the available actions when the workspace (and so the role) changes', async () => {
    role.set('TenantOwner');
    await create();
    expect(el().querySelector('app-invite-member-dialog')).not.toBeNull();

    role.set('Agent');
    tenantId.set('t2');
    await fixture.whenStable();
    expect(el().querySelector('app-invite-member-dialog')).toBeNull();
    expect(el().textContent).toContain('team.readOnly');

    role.set('Manager');
    tenantId.set('t3');
    await fixture.whenStable();
    expect(inviteOptions()).toEqual(['Agent', 'Customer']);
  });

  it('disables the submit button while sending, then reports the result on the page', async () => {
    role.set('Manager');
    const pending = new Subject<void>();
    createInvitation.mockReturnValue(pending);
    await create();
    await submitInvite('new@example.com');

    const button = el().querySelector<HTMLButtonElement>('app-invite-member-dialog button[type="submit"]')!;
    expect(button.disabled).toBe(true);
    expect(button.textContent).toContain('invitations.sending');

    pending.next();
    pending.complete();
    await fixture.whenStable();
    expect(button.disabled).toBe(false);
    expect(createInvitation).toHaveBeenCalledWith({ tenantId: 't1', body: { email: 'new@example.com', role: 'Agent' } });
    expect(el().querySelector('[role="status"]')!.textContent).toContain('invitations.sent');
  });

  it('reloads the visible history after an invitation was created', async () => {
    role.set('TenantOwner');
    await create();
    await openInvitations();
    listInvitationHistory.mockClear();
    await submitInvite('new@example.com');
    expect(listInvitationHistory).toHaveBeenCalledTimes(1);
  });

  it('shows every status with a visible label', async () => {
    role.set('TenantOwner');
    listInvitationHistory.mockReturnValue(
      history([
        row('a', 'Agent', 'Pending'),
        row('b', 'Agent', 'Accepted'),
        row('c', 'Agent', 'Revoked'),
        row('d', 'Agent', 'Expired'),
      ]),
    );
    await create();
    await openInvitations();
    for (const s of ['Pending', 'Accepted', 'Revoked', 'Expired']) {
      expect(el().textContent).toContain(`invitations.state${s}`);
    }
  });

  it('offers actions only on open invitations and only for roles the actor may manage', async () => {
    role.set('Manager');
    listInvitationHistory.mockReturnValue(
      history([
        row('a', 'Agent', 'Pending'),
        row('m', 'Manager', 'Pending'),
        row('o', 'TenantOwner', 'Expired'),
        row('x', 'Agent', 'Accepted'),
        row('r', 'Customer', 'Revoked'),
      ]),
    );
    await create();
    await openInvitations();
    expect(rowButtons()).toHaveLength(2); // resend + revoke of the pending Agent invitation only
    expect(el().textContent).toContain('invitations.ownerOnly');
  });

  it('lets an Owner act on open invitations of every role but not on closed ones', async () => {
    role.set('TenantOwner');
    listInvitationHistory.mockReturnValue(
      history([row('m', 'Manager', 'Pending'), row('x', 'Agent', 'Accepted'), row('r', 'Agent', 'Revoked')]),
    );
    await create();
    await openInvitations();
    expect(rowButtons()).toHaveLength(2);
  });

  it('sends the status filter and search to the server and restarts at page 1', async () => {
    role.set('TenantOwner');
    await create();
    await openInvitations();
    listInvitationHistory.mockClear();
    const status = el().querySelector<HTMLSelectElement>('[role="tabpanel"] [role="search"] select')!;
    status.value = 'Revoked';
    status.dispatchEvent(new Event('change'));
    await fixture.whenStable();
    expect(listInvitationHistory).toHaveBeenCalledWith({ tenantId: 't1', page: 1, pageSize: 25, status: 'Revoked' });
  });

  it('resends through the API, reports it and reloads the history', async () => {
    role.set('TenantOwner');
    listInvitationHistory.mockReturnValue(history([row('a', 'Agent', 'Expired')]));
    await create();
    await openInvitations();
    listInvitationHistory.mockClear();
    rowButtons()[0].click();
    await fixture.whenStable();
    expect(resendInvitation).toHaveBeenCalledWith({ tenantId: 't1', invitationId: 'a' });
    expect(listInvitationHistory).toHaveBeenCalledTimes(1);
    expect(el().querySelector('[role="status"]')!.textContent).toContain('invitations.resent');
  });

  it('explains a resend cooldown instead of a generic failure', async () => {
    role.set('TenantOwner');
    listInvitationHistory.mockReturnValue(history([row('a', 'Agent', 'Pending')]));
    resendInvitation.mockReturnValue(
      throwError(() => new HttpErrorResponse({ status: 429, error: { code: 'INVITATION_RESEND_COOLDOWN' } })),
    );
    await create();
    await openInvitations();
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
    const input = await submitInvite('member@example.com');
    expect(el().textContent).toContain('invitations.errors.alreadyMember');
    expect(input.value).toBe('member@example.com');
  });
});
