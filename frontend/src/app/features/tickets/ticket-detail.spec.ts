import { NO_ERRORS_SCHEMA, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { provideTranslateService } from '@ngx-translate/core';
import { Subject, of } from 'rxjs';
import { TenantService } from '../../core/auth/tenant.service';
import { RealtimeService } from '../../core/realtime/realtime.service';
import { TicketActions } from './ticket-actions';
import { TicketActivity } from './ticket-activity';
import { TicketAttachments } from './ticket-attachments';
import { TicketChat } from './ticket-chat';
import { TicketDetail } from './ticket-detail';
import { TicketEdit } from './ticket-edit';
import { TicketNotes } from './ticket-notes';
import { TicketTasks } from './ticket-tasks';
import { TicketsApi } from './tickets-api';

const detail = (assignedToMe: boolean | null) => ({
  summary: {
    id: 't1', number: 1, displayNumber: 'TKT-1', title: 'Printer', priority: 'Low', status: 'Open', categoryId: 'c', categoryName: 'General',
    customerId: null, customerName: null, createdAtUtc: '2026-11-01T10:00:00Z', updatedAtUtc: '2026-11-01T10:00:00Z', resolvedAtUtc: null, version: 1,
    assigneeCount: 1, eligibleAssigneeCount: 1, resolutionDueAtUtc: null, isOverdue: false, assignedToMe,
  },
  description: 'd', createdByUserId: null, assignees: [], currentSla: null,
});

describe('TicketDetail workspace read access', () => {
  const role = signal('Agent');
  const api = { get: vi.fn() };
  let fixture: ComponentFixture<TicketDetail>;
  const el = () => fixture.nativeElement as HTMLElement;

  const create = async (assignedToMe: boolean | null) => {
    api.get.mockReturnValue(of(detail(assignedToMe)));
    fixture = TestBed.createComponent(TicketDetail);
    fixture.componentRef.setInput('id', 't1');
    await fixture.whenStable();
    fixture.detectChanges();
  };

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        provideTranslateService(),
        { provide: TicketsApi, useValue: api },
        { provide: TenantService, useValue: { role, tenantId: signal('ORG'), current: signal(null), isStaff: signal(true) } },
        { provide: RealtimeService, useValue: { on: () => new Subject(), reconnected: new Subject<void>() } },
      ],
    }).overrideComponent(TicketDetail, {
      remove: { imports: [TicketChat, TicketNotes, TicketAttachments, TicketActions, TicketTasks, TicketActivity, TicketEdit] },
      add: { schemas: [NO_ERRORS_SCHEMA] },
    });
  });

  it('gives an unassigned Agent a read-only overview: notice, no edit, restricted sections but readable activity', async () => {
    role.set('Agent');
    await create(false);
    expect(el().textContent).toContain('Printer');
    expect(el().textContent).toContain('tickets.detail.notAssigned');
    expect(el().textContent).not.toContain('tickets.edit.open');
    expect(el().querySelector('app-ticket-chat')).toBeNull();
    expect(el().querySelector('app-ticket-notes')).toBeNull();
    expect(el().querySelector('app-ticket-attachments')).toBeNull();

    (el().querySelector('#tab-activity') as HTMLButtonElement).click();
    fixture.detectChanges();
    expect(el().querySelector('app-ticket-activity')).not.toBeNull();
  });

  it('leaves the full ticket to an assigned Agent and to managers who are not assigned', async () => {
    role.set('Agent');
    await create(true);
    expect(el().textContent).not.toContain('tickets.detail.notAssigned');
    expect(el().textContent).toContain('tickets.edit.open');
    expect(el().querySelector('app-ticket-chat')).not.toBeNull();
    TestBed.resetTestingModule();

    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        provideTranslateService(),
        { provide: TicketsApi, useValue: api },
        { provide: TenantService, useValue: { role: signal('Manager'), tenantId: signal('ORG'), current: signal(null), isStaff: signal(true) } },
        { provide: RealtimeService, useValue: { on: () => new Subject(), reconnected: new Subject<void>() } },
      ],
    }).overrideComponent(TicketDetail, {
      remove: { imports: [TicketChat, TicketNotes, TicketAttachments, TicketActions, TicketTasks, TicketActivity, TicketEdit] },
      add: { schemas: [NO_ERRORS_SCHEMA] },
    });
    await create(false);
    expect(el().textContent).not.toContain('tickets.detail.notAssigned');
    expect(el().textContent).toContain('tickets.edit.open');
  });
});
