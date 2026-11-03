import { HttpErrorResponse } from '@angular/common/http';
import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideTranslateService } from '@ngx-translate/core';
import { of, throwError } from 'rxjs';
import { TicketDetailDto } from '../../core/api/generated/models';
import { TenantService } from '../../core/auth/tenant.service';
import { canEditTicket } from './ticket-detail';
import { TicketEdit, buildUpdate } from './ticket-edit';
import { TicketsApi } from './tickets-api';

const ticket = (version: number, over: Partial<TicketDetailDto['summary']> = {}, description = 'Printer is down'): TicketDetailDto =>
  ({
    description,
    assignees: [],
    createdByUserId: null,
    currentSla: null,
    summary: { id: 'T1', displayNumber: 'TKT-00001', title: 'Printer', priority: 'Medium', categoryId: 'C1', status: 'Open', version, ...over },
  }) as unknown as TicketDetailDto;

const conflict = new HttpErrorResponse({ status: 409, error: { code: null } });

describe('buildUpdate', () => {
  it('sends only changed fields with the base version', () => {
    const base = ticket(4);
    expect(buildUpdate(base, { title: 'Printer', description: 'Printer is down', priority: 'Medium', categoryId: 'C1' }, true)).toBeNull();
    expect(buildUpdate(base, { title: ' Printer jam ', description: 'Printer is down', priority: 'High', categoryId: 'C1' }, true)).toEqual({
      title: 'Printer jam', description: null, priority: 'High', categoryId: null, version: 4,
    });
  });

  it('never sends priority or category for customers', () => {
    expect(buildUpdate(ticket(1), { title: 'Printer', description: 'Now on fire', priority: 'Urgent', categoryId: 'C9' }, false)).toEqual({
      title: null, description: 'Now on fire', priority: null, categoryId: null, version: 1,
    });
  });

  it('mirrors who may open the editor', () => {
    expect(canEditTicket(ticket(1, { status: 'Closed' }), true)).toBe(false);
    expect(canEditTicket(ticket(1, { status: 'InProgress' }), false)).toBe(false);
    expect(canEditTicket(ticket(1, { status: 'Open' }), false)).toBe(true);
    expect(canEditTicket(ticket(1, { status: 'InProgress' }), true)).toBe(true);
  });
});

describe('TicketEdit', () => {
  let update: ReturnType<typeof vi.fn>;
  let get: ReturnType<typeof vi.fn>;

  function create() {
    TestBed.configureTestingModule({
      providers: [
        provideTranslateService(),
        { provide: TicketsApi, useValue: { update, get, categories: () => of([{ id: 'C1', name: 'General', isActive: true }]) } },
        { provide: TenantService, useValue: { isStaff: signal(true), tenantId: signal('W1') } },
      ],
    });
    const fixture = TestBed.createComponent(TicketEdit);
    fixture.componentRef.setInput('ticket', ticket(4));
    fixture.detectChanges();
    const cmp = fixture.componentInstance as unknown as {
      form: { controls: Record<string, { setValue(v: string): void }>; getRawValue(): { description: string } };
      save(): void;
      keepMine(): void;
      useServer(): void;
      conflict(): TicketDetailDto | null;
      base(): TicketDetailDto | null;
      diff(): { field: string; changedOnServer: boolean; changedByMe: boolean }[];
      error(): string | null;
    };
    return { fixture, cmp };
  }

  beforeEach(() => {
    update = vi.fn();
    get = vi.fn();
  });

  it('shows the server version on a 409 and never retries on its own', () => {
    update.mockReturnValue(throwError(() => conflict));
    get.mockReturnValue(of(ticket(5, { title: 'Printer (floor 2)' })));
    const { fixture, cmp } = create();
    const reloaded = vi.fn();
    fixture.componentInstance.reloaded.subscribe(reloaded);

    cmp.form.controls['description'].setValue('Printer is down since 9am');
    cmp.save();

    expect(update).toHaveBeenCalledTimes(1);
    expect(update.mock.calls[0][1]).toMatchObject({ description: 'Printer is down since 9am', title: null, version: 4 });
    expect(cmp.conflict()?.summary.version).toBe(5);
    expect(reloaded).toHaveBeenCalled();
    expect(cmp.diff()).toEqual([
      expect.objectContaining({ field: 'title', changedOnServer: true, changedByMe: false }),
      expect.objectContaining({ field: 'description', changedOnServer: false, changedByMe: true }),
    ]);

    cmp.save(); // blocked until the user decides
    expect(update).toHaveBeenCalledTimes(1);
  });

  it('keeps the edits on the newest version only after an explicit choice', () => {
    update.mockReturnValueOnce(throwError(() => conflict)).mockReturnValueOnce(of(ticket(6)));
    get.mockReturnValue(of(ticket(5, { title: 'Printer (floor 2)' })));
    const { fixture, cmp } = create();
    const saved = vi.fn();
    fixture.componentInstance.saved.subscribe(saved);

    cmp.form.controls['description'].setValue('Printer is down since 9am');
    cmp.save();
    cmp.keepMine();
    expect(cmp.base()?.summary.version).toBe(5);
    cmp.save();

    expect(update.mock.calls[1][1]).toEqual({ title: null, description: 'Printer is down since 9am', priority: null, categoryId: null, version: 5 });
    expect(saved).toHaveBeenCalled();
  });

  it('can discard the edits and continue from the server version', () => {
    update.mockReturnValue(throwError(() => conflict));
    get.mockReturnValue(of(ticket(5, {}, 'Fixed by IT')));
    const { cmp } = create();
    cmp.form.controls['description'].setValue('Mine');
    cmp.save();
    cmp.useServer();
    expect(cmp.conflict()).toBeNull();
    expect(cmp.form.getRawValue().description).toBe('Fixed by IT');
  });

  it('explains a 409 caused by a closed ticket instead of showing a diff', () => {
    update.mockReturnValue(throwError(() => conflict));
    get.mockReturnValue(of(ticket(5, { status: 'Closed' })));
    const { cmp } = create();
    cmp.form.controls['title'].setValue('Printer 2');
    cmp.save();
    expect(cmp.conflict()).toBeNull();
    expect(cmp.error()).toBe('tickets.edit.closed');
  });
});
