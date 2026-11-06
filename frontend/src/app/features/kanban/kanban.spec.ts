import { CdkDropList, CdkDropListGroup } from '@angular/cdk/drag-drop';
import { HttpErrorResponse } from '@angular/common/http';
import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import { ActivatedRoute, convertToParamMap, provideRouter } from '@angular/router';
import { provideTranslateService } from '@ngx-translate/core';
import { Subject, of, throwError } from 'rxjs';
import { AuthService } from '../../core/auth/auth.service';
import { TenantService } from '../../core/auth/tenant.service';
import { GraphqlClient } from '../../core/graphql/graphql-client';
import { KanbanBoardDocument, TeamMembersDocument, TicketTaskProgressDocument } from '../../core/graphql/generated';
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
        : doc === TeamMembersDocument
          ? of({ teamMembers: { items: [] } })
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
        { provide: AuthService, useValue: { user: signal({ id: 'u-me', email: 'me@example.com', displayName: 'Me' }) } },
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
    expect(el.querySelector('button[aria-controls="tasks-a"]')).not.toBeNull(); // expands in place, no navigation
  });

  it('never requests or shows task data for customers', () => {
    const el = render('Customer');
    expect(query.mock.calls.some(([doc]) => doc === TicketTaskProgressDocument)).toBe(false);
    expect(el.querySelector('a[href$="#child-tasks"]')).toBeNull();
    expect(el.textContent).not.toContain('tasks.');
  });
});

const MEMBERS = [
  { id: 'm-me', userId: 'u-me', email: 'me@example.com', displayName: 'Me Agent', role: 'AGENT', isActive: true },
  { id: 'm-mate', userId: 'u-mate', email: 'mate@example.com', displayName: 'Mate Agent', role: 'AGENT', isActive: true },
  { id: 'm-off', userId: 'u-off', email: 'off@example.com', displayName: 'Suspended Agent', role: 'AGENT', isActive: false },
  { id: 'm-boss', userId: 'u-boss', email: 'boss@example.com', displayName: 'Boss', role: 'MANAGER', isActive: true },
];

describe('Kanban moves', () => {
  const query = vi.fn();
  const rest = { changeStatus: vi.fn(), reopen: vi.fn(), assign: vi.fn(), tasks: vi.fn(), createTask: vi.fn(), changeTaskStatus: vi.fn() };
  const ticket = (id: string, status: string, assignedToMe: boolean | null) => ({ ...card(id), status, assignedToMe });

  function render(role: string, tickets: ReturnType<typeof ticket>[]) {
    query.mockReset();
    query.mockImplementation((doc: unknown) =>
      doc === KanbanBoardDocument
        ? of({ kanbanBoard: ['ASSIGNED', 'IN_PROGRESS', 'ON_HOLD', 'RESOLVED', 'CLOSED'].map((status) => ({ status, total: 0, tickets: tickets.filter((t) => t.status === status) })) })
        : doc === TeamMembersDocument
          ? of({ teamMembers: { items: MEMBERS } })
          : of({ ticketTaskProgress: [] }),
    );
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        provideTranslateService(),
        { provide: GraphqlClient, useValue: { query } },
        { provide: TicketsApi, useValue: rest },
        { provide: TenantService, useValue: { tenantId: signal('ORG'), role: signal(role), isStaff: signal(role !== 'Customer') } },
        { provide: AuthService, useValue: { user: signal({ id: 'u-me', email: 'me@example.com', displayName: 'Me' }) } },
        { provide: RealtimeService, useValue: { on: () => new Subject(), reconnected: new Subject<void>() } },
        { provide: ActivatedRoute, useValue: { snapshot: { queryParamMap: convertToParamMap({}) } } },
      ],
    });
    const fixture = TestBed.createComponent(Kanban);
    fixture.detectChanges();
    TestBed.tick();
    fixture.detectChanges();
    return fixture;
  }

  const options = (el: HTMLElement) => [...el.querySelectorAll('article select option')].map((o) => (o as HTMLOptionElement).value).filter(Boolean);
  const reopenButton = (el: HTMLElement) => [...el.querySelectorAll('article button')].find((b) => b.textContent?.includes('tickets.actions.reopen'));

  beforeEach(() => vi.clearAllMocks());

  it('connects all status columns into one drag group', () => {
    const fixture = render('Manager', [ticket('a', 'ASSIGNED', false)]);
    expect(fixture.debugElement.query(By.directive(CdkDropListGroup))).not.toBeNull();
    expect(fixture.debugElement.queryAll(By.directive(CdkDropList)).length).toBe(5);
  });

  it('never offers Closed to an Agent and offers nothing on a ticket not assigned to them', () => {
    expect(options(render('Agent', [ticket('a', 'IN_PROGRESS', true)]).nativeElement)).toEqual(['ON_HOLD', 'RESOLVED']);
    TestBed.resetTestingModule();
    const resolved = render('Agent', [ticket('r', 'RESOLVED', true)]).nativeElement as HTMLElement;
    expect(options(resolved)).toEqual([]);
    expect(reopenButton(resolved)).toBeDefined();
    TestBed.resetTestingModule();
    const other = render('Agent', [ticket('o', 'IN_PROGRESS', false), ticket('p', 'RESOLVED', false)]).nativeElement as HTMLElement;
    expect(options(other)).toEqual([]);
    expect(reopenButton(other)).toBeUndefined();
  });

  it('gives Owners and Managers Closed and Reopen on a resolved ticket', () => {
    const el = render('Manager', [ticket('r', 'RESOLVED', false)]).nativeElement as HTMLElement;
    expect(options(el)).toEqual(['CLOSED']);
    expect(reopenButton(el)).toBeDefined();
  });

  it('sends a valid drop with the card version, ignores an invalid one and refreshes after a conflict', () => {
    rest.changeStatus.mockReturnValue(throwError(() => new HttpErrorResponse({ status: 409 })));
    const fixture = render('Agent', [ticket('a', 'ASSIGNED', true)]);
    const kanban = fixture.componentInstance as unknown as { drop(e: unknown): void; message(): { key: string } | null };
    const data = ticket('a', 'ASSIGNED', true);
    const loadsBefore = query.mock.calls.filter(([doc]) => doc === KanbanBoardDocument).length;

    kanban.drop({ item: { data }, previousContainer: { data: { status: 'ASSIGNED' } }, container: { data: { status: 'CLOSED' } } });
    expect(rest.changeStatus).not.toHaveBeenCalled();

    kanban.drop({ item: { data }, previousContainer: { data: { status: 'ASSIGNED' } }, container: { data: { status: 'IN_PROGRESS' } } });
    expect(rest.changeStatus).toHaveBeenCalledWith('a', 'InProgress', 1);
    expect(kanban.message()?.key).toBe('kanban.errors.conflict');
    expect(query.mock.calls.filter(([doc]) => doc === KanbanBoardDocument).length).toBe(loadsBefore + 1);
  });

  it('reopens through the dedicated command and reports it', () => {
    rest.reopen.mockReturnValue(of({}));
    const fixture = render('Manager', [ticket('r', 'CLOSED', false)]);
    (reopenButton(fixture.nativeElement) as HTMLButtonElement).click();
    expect(rest.reopen).toHaveBeenCalledWith('r', 1);
    expect(rest.changeStatus).not.toHaveBeenCalled();
    fixture.detectChanges();
    expect((fixture.nativeElement as HTMLElement).querySelector('[role="status"]')?.textContent).toContain('kanban.reopened');
  });

  it('marks workspace tickets that are not assigned to the Agent and sends the My Tickets scope to the server', () => {
    const fixture = render('Agent', [ticket('a', 'IN_PROGRESS', false), ticket('b', 'IN_PROGRESS', true)]);
    const el = fixture.nativeElement as HTMLElement;
    expect(el.textContent?.match(/kanban\.notAssignedToYou/g)?.length).toBe(1);

    const mine = el.querySelector<HTMLInputElement>('input[formControlName="mine"]')!;
    mine.click();
    el.querySelector('form')!.dispatchEvent(new Event('submit'));
    const board = query.mock.calls.filter(([doc]) => doc === KanbanBoardDocument).at(-1)!;
    expect((board[1] as { filter: { assignedToMe: boolean } }).filter.assignedToMe).toBe(true);
  });

  it('offers only active Agents of the workspace in the assignee list and sends a teammate assignment to the server', () => {
    rest.assign.mockReturnValue(of({}));
    const fixture = render('Agent', [ticket('a', 'IN_PROGRESS', false)]);
    const el = fixture.nativeElement as HTMLElement;
    el.querySelector<HTMLButtonElement>('app-agent-picker button.picker-trigger')!.click();
    fixture.detectChanges();
    const options = [...el.querySelectorAll('app-agent-picker button.picker-option')].map((b) => b.textContent?.trim() ?? '');
    expect(options.some((o) => o.includes('Me Agent'))).toBe(true);
    expect(options.some((o) => o.includes('Mate Agent'))).toBe(true);
    expect(options.some((o) => o.includes('Suspended Agent') || o.includes('Boss'))).toBe(false);

    [...el.querySelectorAll<HTMLButtonElement>('app-agent-picker button.picker-option')].find((b) => b.textContent?.includes('Mate Agent'))!.click();
    expect(rest.assign).toHaveBeenCalledWith('a', ['m-mate'], 1);
  });

  it('opens the inline task editor without navigating and requires the Agent to claim the ticket first', () => {
    rest.assign.mockReturnValue(of({}));
    const fixture = render('Agent', [ticket('a', 'IN_PROGRESS', false)]);
    const el = fixture.nativeElement as HTMLElement;
    el.querySelector<HTMLButtonElement>('app-card-tasks button.tasks-add')!.click();
    fixture.detectChanges();

    const input = el.querySelector<HTMLInputElement>('app-card-tasks input[type="text"]')!;
    input.value = 'Check the printer';
    input.dispatchEvent(new Event('input'));
    fixture.detectChanges();
    expect(el.textContent).toContain('kanban.tasks.claimFirst');
    expect(rest.createTask).not.toHaveBeenCalled();
    const save = [...el.querySelectorAll<HTMLButtonElement>('app-card-tasks button')].find((b) => b.textContent?.includes('kanban.tasks.save'))!;
    expect(save.disabled).toBe(true); // nothing is created before the claim

    [...el.querySelectorAll<HTMLButtonElement>('app-card-tasks button')].find((b) => b.textContent?.includes('kanban.tasks.claim'))!.click();
    expect(rest.assign).toHaveBeenCalledWith('a', ['m-me'], 1);
    expect(el.querySelector<HTMLInputElement>('app-card-tasks input[type="text"]')!.value).toBe('Check the printer'); // typed text survives
  });

  it('creates a child task for an assigned Agent and toggles completion from the expanded list', () => {
    rest.createTask.mockReturnValue(of({}));
    rest.changeTaskStatus.mockReturnValue(of({}));
    rest.tasks.mockReturnValue(of({ tasks: [{ id: 't1', title: 'Restart service', status: 'Todo', version: 2, canChangeStatus: true, assigneeDisplayName: 'Me Agent' }] }));
    const fixture = render('Agent', [ticket('a', 'IN_PROGRESS', true)]);
    const el = fixture.nativeElement as HTMLElement;
    el.querySelector<HTMLButtonElement>('app-card-tasks button.tasks-add')!.click();
    fixture.detectChanges();
    const input = el.querySelector<HTMLInputElement>('app-card-tasks input[type="text"]')!;
    input.value = 'Verify network';
    input.dispatchEvent(new Event('input'));
    input.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter' }));
    expect(rest.createTask).toHaveBeenCalledWith('a', 'Verify network', null, null);

    fixture.detectChanges();
    const box = el.querySelector<HTMLInputElement>('app-card-tasks li input[type="checkbox"]')!;
    box.checked = true;
    box.dispatchEvent(new Event('change'));
    expect(rest.changeTaskStatus).toHaveBeenCalledWith('a', 't1', 'Done', 2);
  });
});
