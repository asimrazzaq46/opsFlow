import { computed, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { provideTranslateService } from '@ngx-translate/core';
import { Subject, of } from 'rxjs';
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
  let fixture: ComponentFixture<Team>;
  const el = () => fixture.nativeElement as HTMLElement;
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
    TestBed.configureTestingModule({
      imports: [Team],
      providers: [
        provideRouter([]),
        provideTranslateService(),
        { provide: TenantService, useValue: tenant },
        { provide: AuthService, useValue: { user: signal({ id: 'u1' }) } },
        { provide: TeamApi, useValue: { directory, skills } },
        { provide: InvitationsService, useValue: { listInvitations, createInvitation } },
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
    expect(listInvitations).toHaveBeenCalledWith({ tenantId: 't1' });
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
});
