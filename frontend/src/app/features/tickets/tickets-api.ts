import { Injectable, inject } from '@angular/core';
import { Observable, map } from 'rxjs';
import { Api } from '../../core/api/generated/api';
import {
  assignTicketAgents,
  changeTicketStatus,
  changeTicketTaskStatus,
  createTicket,
  createTicketNote,
  createTicketTask,
  downloadTicketAttachment,
  getTicket,
  listMembers,
  listTicketActivity,
  listTicketAttachments,
  listTicketCategories,
  listTicketMessages,
  listTicketNotes,
  listTickets,
  listTicketTasks,
  reassignTicketTask,
  reopenTicket,
  reorderTicketTasks,
  sendTicketMessage,
  unassignTicketAgent,
  updateTicket,
  updateTicketMessageCursor,
  updateTicketTask,
  uploadTicketAttachment,
} from '../../core/api/generated/functions';
import {
  AttachmentDto,
  CreateTicketRequest,
  MemberDto,
  PagedResultOfTicketActivityDto,
  PagedResultOfTicketMessageDto,
  PagedResultOfTicketNoteDto,
  PagedResultOfTicketSummaryDto,
  TicketCategoryDto,
  TicketConversationCursorDto,
  TicketDetailDto,
  TicketMessageDto,
  TicketNoteDto,
  TicketPriority,
  TicketState,
  TicketStatus,
  TicketTaskDto,
  TicketTaskListDto,
  TicketTaskStatus,
  UpdateTicketRequest,
} from '../../core/api/generated/models';
import { TenantService } from '../../core/auth/tenant.service';

/** Sort fields the API whitelists (anything else is a 400). */
export type TicketSort = 'updatedAt' | 'createdAt' | 'number' | 'title' | 'priority' | 'status';

/** Server-side list query: every filter only narrows what the caller is already allowed to see. */
export interface TicketQuery {
  search?: string;
  status?: TicketStatus;
  priority?: TicketPriority;
  categoryId?: string;
  /** Staff only: narrows to the tickets of one customer contact. */
  customerId?: string;
  state?: NonNullable<TicketState>;
  /** "My tickets": only tickets the caller is assigned to. The server resolves the caller; no membership id is sent. */
  assignedToMe?: boolean;
  sortBy: TicketSort;
  descending: boolean;
  page: number;
  pageSize: number;
}

/** Tenant-aware wrapper around the generated ticket, chat, note and attachment operations. */
@Injectable({ providedIn: 'root' })
export class TicketsApi {
  private readonly api = inject(Api);
  private readonly tenant = inject(TenantService);

  private get tenantId(): string {
    const id = this.tenant.tenantId();
    if (!id) throw new Error('No active workspace');
    return id;
  }

  list(q: TicketQuery): Observable<PagedResultOfTicketSummaryDto> {
    return this.api.invoke(listTickets, {
      tenantId: this.tenantId,
      Search: q.search || undefined,
      Status: q.status ? [q.status] : undefined,
      Priority: q.priority,
      CategoryId: q.categoryId,
      CustomerId: q.customerId,
      State: q.state,
      AssignedToMe: q.assignedToMe || undefined,
      SortBy: q.sortBy,
      Descending: q.descending,
      Page: q.page,
      PageSize: q.pageSize,
    });
  }

  get(ticketId: string): Observable<TicketDetailDto> {
    return this.api.invoke(getTicket, { tenantId: this.tenantId, ticketId });
  }

  messages(
    ticketId: string,
    opts: { page?: number; pageSize?: number; afterSequence?: number } = {},
  ): Observable<PagedResultOfTicketMessageDto> {
    return this.api.invoke(listTicketMessages, { tenantId: this.tenantId, ticketId, ...opts });
  }

  sendMessage(
    ticketId: string,
    content: string,
    clientMessageId: string,
  ): Observable<TicketMessageDto> {
    return this.api.invoke(sendTicketMessage, {
      tenantId: this.tenantId,
      ticketId,
      body: { content, clientMessageId },
    });
  }

  notes(ticketId: string, page = 1, pageSize = 50): Observable<PagedResultOfTicketNoteDto> {
    return this.api.invoke(listTicketNotes, { tenantId: this.tenantId, ticketId, page, pageSize });
  }

  createNote(ticketId: string, content: string, clientNoteId: string): Observable<TicketNoteDto> {
    return this.api.invoke(createTicketNote, {
      tenantId: this.tenantId,
      ticketId,
      body: { content, clientNoteId },
    });
  }

  /** Moves the caller's own read cursor forward; the server accepts only sequences of stored chat messages. */
  markRead(ticketId: string, readSequence: number): Observable<TicketConversationCursorDto> {
    return this.api.invoke(updateTicketMessageCursor, {
      tenantId: this.tenantId,
      ticketId,
      body: { deliveredSequence: null, readSequence },
    });
  }

  attachments(ticketId: string): Observable<AttachmentDto[]> {
    return this.api.invoke(listTicketAttachments, { tenantId: this.tenantId, ticketId });
  }

  upload(ticketId: string, file: File, internal: boolean): Observable<AttachmentDto> {
    return this.api.invoke(uploadTicketAttachment, {
      tenantId: this.tenantId,
      ticketId,
      body: { File: file, Internal: internal },
    });
  }

  /** The generated function asks for a blob response; its declared body type is only a placeholder. */
  download(ticketId: string, attachmentId: string): Observable<Blob> {
    return this.api
      .invoke$Response(downloadTicketAttachment, {
        tenantId: this.tenantId,
        ticketId,
        attachmentId,
      })
      .pipe(map((r) => r.body as unknown as Blob));
  }

  // REST commands (server stays authoritative; version = optimistic concurrency token)
  create(body: CreateTicketRequest): Observable<TicketDetailDto> {
    return this.api.invoke(createTicket, { tenantId: this.tenantId, body });
  }

  /** Only title, description, priority and category are editable; `version` makes a concurrent change fail with 409 instead of being overwritten. */
  update(ticketId: string, body: UpdateTicketRequest): Observable<TicketDetailDto> {
    return this.api.invoke(updateTicket, { tenantId: this.tenantId, ticketId, body });
  }

  /** Append-only timeline in sequence order; the server filters out entries a customer may not see. */
  activity(ticketId: string, page = 1, pageSize = 100): Observable<PagedResultOfTicketActivityDto> {
    return this.api.invoke(listTicketActivity, {
      tenantId: this.tenantId,
      ticketId,
      page,
      pageSize,
    });
  }

  categories(): Observable<TicketCategoryDto[]> {
    return this.api.invoke(listTicketCategories, { tenantId: this.tenantId });
  }

  changeStatus(
    ticketId: string,
    status: TicketStatus,
    version: number,
  ): Observable<TicketDetailDto> {
    return this.api.invoke(changeTicketStatus, {
      tenantId: this.tenantId,
      ticketId,
      body: { status, version },
    });
  }

  reopen(ticketId: string, version: number): Observable<TicketDetailDto> {
    return this.api.invoke(reopenTicket, { tenantId: this.tenantId, ticketId, body: { version } });
  }

  assign(ticketId: string, membershipIds: string[], version: number): Observable<TicketDetailDto> {
    return this.api.invoke(assignTicketAgents, {
      tenantId: this.tenantId,
      ticketId,
      body: { membershipIds, version },
    });
  }

  unassign(ticketId: string, membershipId: string, version: number): Observable<TicketDetailDto> {
    return this.api.invoke(unassignTicketAgent, {
      tenantId: this.tenantId,
      ticketId,
      membershipId,
      version,
    });
  }

  /** Tenant members (Agents are the assignable ones). */
  members(): Observable<MemberDto[]> {
    return this.api
      .invoke(listMembers, { tenantId: this.tenantId, pageSize: 100 })
      .pipe(map((p) => p.items));
  }

  // Internal child tasks (staff only; server enforces ownership/manager rules and versions)
  tasks(ticketId: string): Observable<TicketTaskListDto> {
    return this.api.invoke(listTicketTasks, { tenantId: this.tenantId, ticketId });
  }

  createTask(
    ticketId: string,
    title: string,
    description: string | null,
    assigneeMembershipId: string | null,
  ): Observable<TicketTaskDto> {
    return this.api.invoke(createTicketTask, {
      tenantId: this.tenantId,
      ticketId,
      body: { title, description, assigneeMembershipId },
    });
  }

  updateTask(
    ticketId: string,
    taskId: string,
    title: string,
    description: string | null,
    version: number,
  ): Observable<TicketTaskDto> {
    return this.api.invoke(updateTicketTask, {
      tenantId: this.tenantId,
      ticketId,
      taskId,
      body: { title, description, version },
    });
  }

  changeTaskStatus(
    ticketId: string,
    taskId: string,
    status: TicketTaskStatus,
    version: number,
  ): Observable<TicketTaskDto> {
    return this.api.invoke(changeTicketTaskStatus, {
      tenantId: this.tenantId,
      ticketId,
      taskId,
      body: { status, version },
    });
  }

  reassignTask(
    ticketId: string,
    taskId: string,
    assigneeMembershipId: string,
    version: number,
  ): Observable<TicketTaskDto> {
    return this.api.invoke(reassignTicketTask, {
      tenantId: this.tenantId,
      ticketId,
      taskId,
      body: { assigneeMembershipId, version },
    });
  }

  reorderTasks(
    ticketId: string,
    tasks: { taskId: string; version: number }[],
  ): Observable<TicketTaskListDto> {
    return this.api.invoke(reorderTicketTasks, {
      tenantId: this.tenantId,
      ticketId,
      body: { tasks },
    });
  }
}
