import { HttpErrorResponse } from '@angular/common/http';
import { signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ActivatedRoute } from '@angular/router';
import { provideTranslateService } from '@ngx-translate/core';
import { Subject, of, throwError } from 'rxjs';
import { TicketDetailDto, TicketTaskDto, TicketTaskListDto } from '../../core/api/generated/models';
import { TenantService } from '../../core/auth/tenant.service';
import { RealtimeService } from '../../core/realtime/realtime.service';
import { TicketTasks, initials, summarize, taskPercent } from './ticket-tasks';
import { TicketsApi } from './tickets-api';

const task = (id: string, over: Partial<TicketTaskDto> = {}): TicketTaskDto => ({
  id,
  ticketId: 'T1',
  title: `Task ${id}`,
  description: null,
  status: 'Todo',
  sortOrder: 1,
  assigneeMembershipId: 'A1',
  assigneeDisplayName: 'Ada Lovelace',
  isAssigneeEligible: true,
  createdAtUtc: '2026-11-01T10:00:00Z',
  updatedAtUtc: '2026-11-01T10:00:00Z',
  completedAtUtc: null,
  version: 1,
  canEdit: true,
  canChangeStatus: true,
  ...over,
});

const listOf = (tasks: TicketTaskDto[], over: Partial<TicketTaskListDto> = {}): TicketTaskListDto => ({
  tasks,
  summary: summarize(tasks),
  isReadOnly: false,
  canCreate: true,
  canManage: true,
  ...over,
});

const ticket = {
  summary: { id: 'T1', version: 3 },
  assignees: [
    { membershipId: 'A1', displayName: 'Ada Lovelace', isAgentActive: true },
    { membershipId: 'A2', displayName: 'Bob Stone', isAgentActive: true },
    { membershipId: 'A3', displayName: 'Gone Agent', isAgentActive: false },
  ],
} as unknown as TicketDetailDto;

describe('task helpers', () => {
  it('derives counts and progress from the actual statuses', () => {
    const s = summarize([task('1', { status: 'Done' }), task('2', { status: 'InProgress' }), task('3')]);
    expect(s).toEqual({ totalTasks: 3, todoTasks: 1, inProgressTasks: 1, completedTasks: 1 });
    expect(taskPercent(s)).toBe(33);
    expect(taskPercent(summarize([]))).toBe(0);
    expect(initials('  ada   lovelace ')).toBe('AL');
    expect(initials('Cher')).toBe('C');
  });
});

describe('TicketTasks', () => {
  const role = signal<string | null>('Manager');
  const live = new Subject<unknown>();
  let api: Record<string, ReturnType<typeof vi.fn>>;
  let fixture: ComponentFixture<TicketTasks>;

  const el = () => fixture.nativeElement as HTMLElement;
  const buttons = (text: string) => Array.from(el().querySelectorAll('button')).filter((b) => b.textContent?.trim().includes(text));

  function setup(list: TicketTaskListDto | Error): void {
    api = {
      tasks: vi.fn(() => (list instanceof Error ? throwError(() => list) : of(list))),
      createTask: vi.fn(() => of(task('new'))),
      updateTask: vi.fn(),
      changeTaskStatus: vi.fn((_t: string, id: string, status: string) => of(task(id, { status: status as 'Done', version: 2 }))),
      reassignTask: vi.fn(),
      reorderTasks: vi.fn(() => of(list)),
    };
    TestBed.configureTestingModule({
      providers: [
        provideTranslateService(),
        { provide: TicketsApi, useValue: api },
        { provide: TenantService, useValue: { role, tenantId: signal('ORG') } },
        { provide: RealtimeService, useValue: { on: () => live.asObservable(), reconnected: new Subject<void>() } },
        { provide: ActivatedRoute, useValue: { snapshot: { fragment: null } } },
      ],
    });
    fixture = TestBed.createComponent(TicketTasks);
    fixture.componentRef.setInput('ticket', ticket);
    fixture.detectChanges();
  }

  beforeEach(() => role.set('Manager'));
  afterEach(() => vi.useRealTimers());

  it('shows progress, manager reorder and reassign controls with only eligible agents', () => {
    setup(listOf([task('1', { status: 'Done' }), task('2')]));
    expect(el().textContent).toContain('tasks.progress');
    expect(el().querySelector('[role="progressbar"]')?.getAttribute('aria-valuenow')).toBe('50');
    expect(el().querySelectorAll('[aria-label^="tasks.moveUp"]').length).toBe(2);
    const options = Array.from(el().querySelectorAll('select option')).map((o) => o.textContent?.trim());
    expect(options).toContain('Bob Stone');
    expect(options).not.toContain('Gone Agent'); // ineligible agents are never offered
    expect(options).not.toContain('Ada Lovelace'); // already the assignee
  });

  it('hides manager-only controls for agents and creates self-assigned tasks', () => {
    role.set('Agent');
    setup(listOf([task('1', { canEdit: false, canChangeStatus: false })], { canManage: false }));
    expect(el().querySelectorAll('[aria-label^="tasks.moveUp"]').length).toBe(0);
    expect(el().querySelector('select')).toBeNull();
    expect(buttons('tasks.status.Done').length).toBe(0); // not their task

    buttons('tasks.add')[0].click();
    fixture.detectChanges();
    expect(el().querySelector('#task-assignee')).toBeNull();
    const input = el().querySelector<HTMLInputElement>('#task-title')!;
    input.value = 'Collect logs';
    input.dispatchEvent(new Event('input'));
    el().querySelector('form')!.dispatchEvent(new Event('submit'));
    expect(api['createTask']).toHaveBeenCalledWith('T1', 'Collect logs', null, null);
  });

  it('requires a responsible agent when a manager creates a task', () => {
    setup(listOf([]));
    expect(el().textContent).toContain('tasks.empty');
    buttons('tasks.add')[0].click();
    fixture.detectChanges();
    const input = el().querySelector<HTMLInputElement>('#task-title')!;
    input.value = 'Swap toner';
    input.dispatchEvent(new Event('input'));
    fixture.detectChanges();
    expect(el().querySelector<HTMLButtonElement>('form button[type="submit"]')!.disabled).toBe(true);

    const select = el().querySelector<HTMLSelectElement>('#task-assignee')!;
    select.value = 'A2';
    select.dispatchEvent(new Event('change'));
    el().querySelector('form')!.dispatchEvent(new Event('submit'));
    expect(api['createTask']).toHaveBeenCalledWith('T1', 'Swap toner', null, 'A2');
  });

  it('applies the authoritative status response to the list and the progress', () => {
    setup(listOf([task('1'), task('2')]));
    buttons('tasks.status.Done')[0].click();
    fixture.detectChanges();
    expect(api['changeTaskStatus']).toHaveBeenCalledWith('T1', '1', 'Done', 1);
    expect(el().querySelector('[role="progressbar"]')?.getAttribute('aria-valuenow')).toBe('50');
    expect(api['tasks']).toHaveBeenCalledTimes(1); // no redundant reload after success
  });

  it('reports a conflict and reloads the authoritative state', () => {
    setup(listOf([task('1')]));
    api['changeTaskStatus'].mockReturnValue(throwError(() => new HttpErrorResponse({ status: 409 })));
    buttons('tasks.status.Done')[0].click();
    fixture.detectChanges();
    expect(el().querySelector('[role="alert"]')?.textContent).toContain('tasks.errors.conflict');
    expect(api['tasks']).toHaveBeenCalledTimes(2);
  });

  it('reorders with the keyboard using the full list and versions', () => {
    setup(listOf([task('1'), task('2', { version: 4 })]));
    el().querySelector<HTMLButtonElement>('[aria-label="tasks.moveDown: Task 1"]')!.click();
    expect(api['reorderTasks']).toHaveBeenCalledWith('T1', [{ taskId: '2', version: 4 }, { taskId: '1', version: 1 }]);
  });

  it('shows read-only and error states', () => {
    setup(listOf([task('1', { canEdit: false, canChangeStatus: false })], { isReadOnly: true, canCreate: false, canManage: false }));
    expect(el().textContent).toContain('tasks.readOnly');
    expect(buttons('tasks.add').length).toBe(0);
    TestBed.resetTestingModule();
    setup(new Error('boom'));
    expect(el().textContent).toContain('tasks.loadError');
  });

  it('reloads on realtime changes of this ticket but ignores its own echo and other tickets', () => {
    vi.useFakeTimers();
    setup(listOf([task('1', { version: 2 })]));
    live.next({ tenantId: 'ORG', ticketId: 'T1', taskId: '1', change: 'TaskStatusChanged', version: 2 }); // echo
    live.next({ tenantId: 'ORG', ticketId: 'OTHER', taskId: null, change: 'TasksReordered', version: null });
    vi.advanceTimersByTime(500);
    expect(api['tasks']).toHaveBeenCalledTimes(1);
    live.next({ tenantId: 'ORG', ticketId: 'T1', taskId: null, change: 'TasksReordered', version: null });
    vi.advanceTimersByTime(500);
    expect(api['tasks']).toHaveBeenCalledTimes(2);
  });
});
