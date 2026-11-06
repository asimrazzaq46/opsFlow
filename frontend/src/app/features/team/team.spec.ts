import { computed, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideTranslateService } from '@ngx-translate/core';
import { Subject, of } from 'rxjs';
import { Api } from '../../core/api/generated/api';
import { createInvitation, listInvitations } from '../../core/api/generated/functions';
import { TenantService } from '../../core/auth/tenant.service';
import { GraphqlClient } from '../../core/graphql/graphql-client';
import { Team } from './team';

describe('Team', () => {
  const role = signal<string | null>('Agent');
  const tenantId = signal<string | null>('t1');
  const tenant = { role, tenantId, isStaff: computed(() => role() !== null && role() !== 'Customer') };
  const invoke = vi.fn();
  const query = vi.fn();
  let fixture: ComponentFixture<Team>;
  const el = () => fixture.nativeElement as HTMLElement;
  const select = () => el().querySelector<HTMLSelectElement>('select');
  const options = () => Array.from(select()?.options ?? []).map((o) => o.value);

  const create = async () => {
    fixture = TestBed.createComponent(Team);
    await fixture.whenStable();
  };

  beforeEach(() => {
    vi.clearAllMocks();
    role.set('Agent');
    tenantId.set('t1');
    query.mockReturnValue(of({ teamMembers: { items: [], page: 1, pageSize: 20, totalCount: 0 } }));
    invoke.mockImplementation((fn: unknown) => (fn === listInvitations ? of([]) : of(undefined)));
    TestBed.configureTestingModule({
      imports: [Team],
      providers: [
        provideTranslateService(),
        { provide: TenantService, useValue: tenant },
        { provide: Api, useValue: { invoke } },
        { provide: GraphqlClient, useValue: { query } },
      ],
    });
  });

  it('shows an Agent the team read-only: no invitation form, list, or invitation request', async () => {
    await create();
    expect(query).toHaveBeenCalled();
    expect(el().querySelector('form')).toBeNull();
    expect(el().textContent).not.toContain('invitations.title');
    expect(el().textContent).toContain('team.readOnly');
    expect(invoke).not.toHaveBeenCalled();
  });

  it('shows a Customer neither the directory nor any request', async () => {
    role.set('Customer');
    await create();
    expect(el().textContent).toContain('common.forbidden');
    expect(query).not.toHaveBeenCalled();
    expect(invoke).not.toHaveBeenCalled();
  });

  it('offers a Manager only the Agent and Customer roles', async () => {
    role.set('Manager');
    await create();
    expect(options()).toEqual(['Agent', 'Customer']);
    expect(invoke).toHaveBeenCalledWith(listInvitations, { tenantId: 't1' });
  });

  it('offers an Owner every role', async () => {
    role.set('TenantOwner');
    await create();
    expect(options()).toEqual(['Manager', 'Agent', 'Customer', 'TenantOwner']);
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
    expect(options()).toEqual(['Agent', 'Customer']);
  });

  it('disables the button and shows pending text while sending, then reports the result', async () => {
    role.set('Manager');
    const pending = new Subject<void>();
    invoke.mockImplementation((fn: unknown) => (fn === createInvitation ? pending : of([])));
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
