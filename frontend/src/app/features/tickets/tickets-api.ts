import { Injectable, inject } from '@angular/core';
import { Observable, map } from 'rxjs';
import { Api } from '../../core/api/generated/api';
import {
  createTicketNote, downloadTicketAttachment, getTicket, listTicketAttachments, listTicketMessages, listTicketNotes, listTickets, sendTicketMessage,
  uploadTicketAttachment,
} from '../../core/api/generated/functions';
import {
  AttachmentDto, PagedResultOfTicketMessageDto, PagedResultOfTicketNoteDto, PagedResultOfTicketSummaryDto, TicketDetailDto, TicketMessageDto, TicketNoteDto,
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
}
