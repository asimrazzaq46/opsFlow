import { DatePipe } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  ElementRef,
  effect,
  inject,
  input,
  output,
  signal,
  untracked,
  viewChild,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { TranslatePipe } from '@ngx-translate/core';
import { Subscription, debounceTime, filter, merge } from 'rxjs';
import { AttachmentDto } from '../../core/api/generated/models';
import { TenantService } from '../../core/auth/tenant.service';
import { RealtimeService } from '../../core/realtime/realtime.service';
import { activeWorkspaceEvents } from '../../core/realtime/workspace-events';
import { TicketsApi } from './tickets-api';

export const MAX_ATTACHMENT_BYTES = 10 * 1024 * 1024;
export const ALLOWED_EXTENSIONS = ['png', 'jpg', 'jpeg', 'gif', 'pdf', 'txt', 'docx', 'xlsx'];

export type AttachmentProblem =
  | 'badType'
  | 'tooLarge'
  | 'invalid'
  | 'forbidden'
  | 'notFound'
  | 'conflict'
  | 'network'
  | 'uploadError'
  | 'downloadError';

/** Client-side pre-check for UX only; the server validates size, extension AND file signature. */
export function attachmentProblem(file: {
  name: string;
  size: number;
}): 'badType' | 'tooLarge' | null {
  const ext = file.name.split('.').pop()?.toLowerCase() ?? '';
  if (!file.name.includes('.') || !ALLOWED_EXTENSIONS.includes(ext)) return 'badType';
  return file.size > MAX_ATTACHMENT_BYTES ? 'tooLarge' : null;
}

/** Maps a failed request to a message key: the server's stable file codes first, then the HTTP status. */
export function attachmentErrorKey(
  e: unknown,
  fallback: 'uploadError' | 'downloadError',
): AttachmentProblem {
  if (!(e instanceof HttpErrorResponse)) return fallback;
  const code = (e.error as { code?: string } | null)?.code;
  if (code === 'ATTACHMENT_TOO_LARGE' || e.status === 413) return 'tooLarge';
  if (code === 'ATTACHMENT_TYPE_NOT_ALLOWED') return 'badType';
  switch (e.status) {
    case 0:
      return 'network';
    case 400:
      return 'invalid';
    case 403:
      return 'forbidden';
    case 404:
      return 'notFound';
    case 409:
      return 'conflict';
    default:
      return fallback;
  }
}

@Component({
  selector: 'app-ticket-attachments',
  imports: [TranslatePipe, DatePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './ticket-attachments.html',
  styleUrl: './ticket-attachments.css',
})
export class TicketAttachments {
  readonly ticketId = input.required<string>();
  readonly staff = input(false);
  /** A closed ticket accepts no files (the server answers 409); reopening makes it writable again. */
  readonly closed = input(false);
  /** A file of this ticket was stored; the parent refreshes the activity timeline. */
  readonly uploaded = output<void>();
  /** The server reported a conflict (e.g. the ticket was closed meanwhile); the parent reloads the ticket. */
  readonly stale = output<void>();

  private readonly api = inject(TicketsApi);
  private readonly tenant = inject(TenantService);

  protected readonly accept = ALLOWED_EXTENSIONS.map((e) => '.' + e).join(',');
  protected readonly items = signal<AttachmentDto[]>([]);
  protected readonly loading = signal(false);
  protected readonly error = signal(false);
  /** Indeterminate: the app's Fetch-based HttpClient cannot report upload progress. */
  protected readonly uploading = signal(false);
  protected readonly actionError = signal<AttachmentProblem | null>(null);
  protected readonly internal = signal(false);

  private readonly fileInput = viewChild<ElementRef<HTMLInputElement>>('fileInput');

  /** Only the newest list request may write state, so a slow answer for a previous ticket or workspace is dropped. */
  private seq = 0;
  private upload?: Subscription;

  constructor() {
    effect(() => {
      this.ticketId();
      this.tenant.tenantId();
      untracked(() => {
        this.cancelUpload();
        this.items.set([]);
        this.actionError.set(null);
        this.load();
      });
    });
    inject(DestroyRef).onDestroy(() => this.upload?.unsubscribe());
    // Content-free signal: the server targets only people allowed to list this ticket's files; internal ones never reach customers.
    merge(
      activeWorkspaceEvents<{ tenantId: string; ticketId: string }>('ticket.attachment.added').pipe(
        filter((e) => e.ticketId === this.ticketId()),
      ),
      inject(RealtimeService).reconnected,
    )
      .pipe(debounceTime(300), takeUntilDestroyed())
      .subscribe(() => this.load(true));
  }

  protected size(a: AttachmentDto): string {
    const bytes = Number(a.sizeBytes);
    return bytes >= 1024 * 1024
      ? `${(bytes / 1024 / 1024).toFixed(1)} MB`
      : `${Math.max(1, Math.round(bytes / 1024))} KB`;
  }

  /** `quiet` keeps the current list on screen while refreshing after an upload or a live event. */
  protected load(quiet = false): void {
    const id = ++this.seq;
    if (!quiet || !this.items().length) this.loading.set(true);
    this.error.set(false);
    this.api.attachments(this.ticketId()).subscribe({
      next: (r) => {
        if (id !== this.seq) return;
        this.items.set(r);
        this.loading.set(false);
      },
      error: () => {
        if (id !== this.seq) return;
        this.error.set(true);
        this.loading.set(false);
      },
    });
  }

  protected pick(event: Event): void {
    const input = event.target as HTMLInputElement;
    const file = input.files?.[0];
    this.actionError.set(null);
    if (!file || this.uploading() || this.closed()) return;
    const problem = attachmentProblem(file);
    if (problem) {
      this.actionError.set(problem);
      input.value = '';
      return;
    }
    this.uploading.set(true);
    // Leaving the ticket or workspace unsubscribes (see cancelUpload), so these callbacks only ever touch the ticket that started it.
    this.upload = this.api.upload(this.ticketId(), file, this.staff() && this.internal()).subscribe({
      next: () => {
        this.finishUpload();
        this.uploaded.emit();
        this.load(true); // authoritative metadata (server-sanitized name, detected type)
      },
      error: (e: unknown) => {
        this.finishUpload();
        this.actionError.set(attachmentErrorKey(e, 'uploadError'));
        if (e instanceof HttpErrorResponse && e.status === 409) this.stale.emit();
      },
    });
  }

  /**
   * Stops waiting for an upload when the ticket or workspace changes. The server may already have stored the file; it then appears
   * through the reloaded list or the live signal of that ticket, never in the newly shown one.
   */
  private cancelUpload(): void {
    this.upload?.unsubscribe();
    this.finishUpload();
  }

  private finishUpload(): void {
    this.upload = undefined;
    this.uploading.set(false);
    const input = this.fileInput()?.nativeElement;
    if (input) input.value = '';
  }

  protected download(a: AttachmentDto): void {
    this.actionError.set(null);
    this.api.download(this.ticketId(), a.id).subscribe({
      next: (blob) => {
        const url = URL.createObjectURL(blob);
        const link = document.createElement('a');
        link.href = url;
        link.download = a.fileName;
        link.click();
        setTimeout(() => URL.revokeObjectURL(url));
      },
      error: (e: unknown) => this.actionError.set(attachmentErrorKey(e, 'downloadError')),
    });
  }
}
