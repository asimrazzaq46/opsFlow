import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap, provideRouter } from '@angular/router';
import { provideTranslateService } from '@ngx-translate/core';
import { Subject, of } from 'rxjs';
import { TenantService } from '../../core/auth/tenant.service';
import { GraphqlClient } from '../../core/graphql/graphql-client';
import { KanbanBoardDocument, TicketTaskProgressDocument } from '../../core/graphql/generated';
import { RealtimeService } from '../../core/realtime/realtime.service';
import { TicketsApi } from '../tickets/tickets-api';
import { Kanban } from './kanban';

const card = (id: string) => ({
  id, displayNumber: id, title: `Ticket ${id}`, priority: 'LOW', status: 'ASSIGNED', categoryName: null, customerName: null,
  updatedAtUtc: '2026-11-01T10:00:00Z', version: 1, assigneeCount: 1, resolutionDueAtUtc: null, isOverdue: false,
});

describe('Kanban child-task progress', () => {
  const query = vi.fn();

  function render(role: string): HTMLElement {
    query.mockReset();
    query.mockImplementation((doc: unknown) =>
      doc === KanbanBoardDocument
        ? of({ kanbanBoard: [{ status: 'ASSIGNED', total: 2, tickets: [card('a'), card('b')] }] })
        : of({ ticketTaskProgress: [{ ticketId: 'a', totalTasks: 4, completedTasks: 1 }] }),
    );
    const staff = role !== 'Customer';
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        provideTranslateService(),
        { provide: GraphqlClient, useValue: { query } },
        { provide: TicketsApi, useValue: {} },
        { provide: TenantService, useValue: { tenantId: signal('ORG'), role: signal(role), isStaff: signal(staff) } },
        { provide: RealtimeService, useValue: { on: () => new Subject(), reconnected: new Subject<void>() } },
        { provide: ActivatedRoute, useValue: { snapshot: { queryParamMap: convertToParamMap({}) } } },
      ],
    });
    const fixture = TestBed.createComponent(Kanban);
    fixture.detectChanges();
    TestBed.tick();
    fixture.detectChanges();
    return fixture.nativeElement as HTMLElement;
  }

  it('loads progress for the whole board in one internal query and links to the tasks section', () => {
    const el = render('Agent');
    const progressCalls = query.mock.calls.filter(([doc]) => doc === TicketTaskProgressDocument);
    expect(progressCalls).toEqual([[TicketTaskProgressDocument, { ticketIds: ['a', 'b'] }]]);
    expect(el.textContent).toContain('1/4');
    expect(el.querySelector('a[href="/tickets/a#child-tasks"]')).not.toBeNull();
  });

  it('never requests or shows task data for customers', () => {
    const el = render('Customer');
    expect(query.mock.calls.some(([doc]) => doc === TicketTaskProgressDocument)).toBe(false);
    expect(el.querySelector('a[href$="#child-tasks"]')).toBeNull();
    expect(el.textContent).not.toContain('tasks.');
  });
});
