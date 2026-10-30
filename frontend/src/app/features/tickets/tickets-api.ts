import { Injectable, inject } from '@angular/core';
import { Observable, map } from 'rxjs';
import { Api } from '../../core/api/generated/api';
import {
  assignTicketAgents, changeTicketStatus, createTicket, createTicketNote, downloadTicketAttachment, getTicket, listMembers, listTicketAttachments,
  listTicketCategories, listTicketMessages, listTicketNotes, listTickets, reopenTicket, sendTicketMessage, unassignTicketAgent, uploadTicketAttachment,
} from '../../core/api/generated/functions';
import {
  AttachmentDto, CreateTicketRequest, MemberDto, PagedResultOfTicketMessageDto, PagedResultOfTicketNoteDto, PagedResultOfTicketSummaryDto, TicketCategoryDto,
  TicketDetailDto, TicketMessageDto, TicketNoteDto, TicketStatus,
} from '../../core/api/generated/models';
import { TenantService } from '../../core/auth/tenant.service';

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

  list(page: number, pageSize: number): Observable<PagedResultOfTicketSummaryDto> {
    return this.api.invoke(listTickets, { tenantId: this.tenantId, Page: page, PageSize: pageSize, SortBy: 'updatedAt', Descending: true });
  }

  get(ticketId: string): Observable<TicketDetailDto> {
    return this.api.invoke(getTicket, { tenantId: this.tenantId, ticketId });
  }

  messages(ticketId: string, opts: { page?: number; pageSize?: number; afterSequence?: number } = {}): Observable<PagedResultOfTicketMessageDto> {
    return this.api.invoke(listTicketMessages, { tenantId: this.tenantId, ticketId, ...opts });
  }

  sendMessage(ticketId: string, content: string, clientMessageId: string): Observable<TicketMessageDto> {
    return this.api.invoke(sendTicketMessage, { tenantId: this.tenantId, ticketId, body: { content, clientMessageId } });
  }

  notes(ticketId: string, page = 1, pageSize = 50): Observable<PagedResultOfTicketNoteDto> {
    return this.api.invoke(listTicketNotes, { tenantId: this.tenantId, ticketId, page, pageSize });
  }

  createNote(ticketId: string, content: string, clientNoteId: string): Observable<TicketNoteDto> {
    return this.api.invoke(createTicketNote, { tenantId: this.tenantId, ticketId, body: { content, clientNoteId } });
  }

  attachments(ticketId: string): Observable<AttachmentDto[]> {
    return this.api.invoke(listTicketAttachments, { tenantId: this.tenantId, ticketId });
  }

  upload(ticketId: string, file: File, internal: boolean): Observable<AttachmentDto> {
    return this.api.invoke(uploadTicketAttachment, { tenantId: this.tenantId, ticketId, body: { File: file, Internal: internal } });
  }

  /** The generated function asks for a blob response; its declared body type is only a placeholder. */
  download(ticketId: string, attachmentId: string): Observable<Blob> {
    return this.api
      .invoke$Response(downloadTicketAttachment, { tenantId: this.tenantId, ticketId, attachmentId })
      .pipe(map((r) => r.body as unknown as Blob));
  }

  // UPDATED: Milestone 5 - REST commands (server stays authoritative; version = optimistic concurrency token)
  create(body: CreateTicketRequest): Observable<TicketDetailDto> {
    return this.api.invoke(createTicket, { tenantId: this.tenantId, body });
  }

  categories(): Observable<TicketCategoryDto[]> {
    return this.api.invoke(listTicketCategories, { tenantId: this.tenantId });
  }

  changeStatus(ticketId: string, status: TicketStatus, version: number): Observable<TicketDetailDto> {
    return this.api.invoke(changeTicketStatus, { tenantId: this.tenantId, ticketId, body: { status, version } });
  }

  reopen(ticketId: string, version: number): Observable<TicketDetailDto> {
    return this.api.invoke(reopenTicket, { tenantId: this.tenantId, ticketId, body: { version } });
  }

  assign(ticketId: string, membershipIds: string[], version: number): Observable<TicketDetailDto> {
    return this.api.invoke(assignTicketAgents, { tenantId: this.tenantId, ticketId, body: { membershipIds, version } });
  }

  unassign(ticketId: string, membershipId: string, version: number): Observable<TicketDetailDto> {
    return this.api.invoke(unassignTicketAgent, { tenantId: this.tenantId, ticketId, membershipId, version });
  }

  /** Tenant members (Agents are the assignable ones). */
  members(): Observable<MemberDto[]> {
    return this.api.invoke(listMembers, { tenantId: this.tenantId, pageSize: 100 }).pipe(map((p) => p.items));
  }
}
