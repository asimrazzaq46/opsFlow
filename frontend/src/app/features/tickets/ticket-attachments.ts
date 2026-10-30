import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, effect, inject, input, signal, untracked } from '@angular/core';
import { TranslatePipe } from '@ngx-translate/core';
import { AttachmentDto } from '../../core/api/generated/models';
import { TenantService } from '../../core/auth/tenant.service';
import { TicketsApi } from './tickets-api';

export const MAX_ATTACHMENT_BYTES = 10 * 1024 * 1024;
export const ALLOWED_EXTENSIONS = ['png', 'jpg', 'jpeg', 'gif', 'pdf', 'txt', 'docx', 'xlsx'];

/** Client-side pre-check for UX only; the server validates size, extension AND file signature. */
export function attachmentProblem(file: { name: string; size: number }): 'badType' | 'tooLarge' | null {
  const ext = file.name.split('.').pop()?.toLowerCase() ?? '';
  if (!file.name.includes('.') || !ALLOWED_EXTENSIONS.includes(ext)) return 'badType';
  return file.size > MAX_ATTACHMENT_BYTES ? 'tooLarge' : null;
}

@Component({
  selector: 'app-ticket-attachments',
  imports: [TranslatePipe, DatePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="rounded-xl border border-border bg-card p-4" aria-labelledby="att-title">
      <h2 id="att-title" class="text-lg font-semibold">{{ 'attachments.title' | translate }}</h2>
      <div aria-live="polite" class="mt-3">
        @if (loading()) {
          <p class="text-muted">{{ 'common.loading' | translate }}</p>
        } @else if (error()) {
          <p role="alert" class="text-danger">{{ 'attachments.error' | translate }}
            <button type="button" class="ml-2 underline" (click)="load()">{{ 'common.retry' | translate }}</button></p>
        } @else if (!items().length) {
          <p class="text-muted">{{ 'attachments.empty' | translate }}</p>
        } @else {
          <ul class="divide-y divide-border">
            @for (a of items(); track a.id) {
              <li class="flex flex-wrap items-center justify-between gap-2 py-2">
                <span class="break-all">{{ a.fileName }}
                  @if (a.isInternal) {
                    <span class="ml-1 rounded bg-amber-500/20 px-1.5 py-0.5 text-xs">{{ 'attachments.internalBadge' | translate }}</span>
                  }
                  <span class="ml-2 text-xs text-muted">{{ size(a) }} · {{ a.createdAtUtc | date: 'short' }}</span>
                </span>
                <button type="button" class="rounded-lg border border-border px-3 py-1 text-sm focus-visible:outline-2 focus-visible:outline-brand" (click)="download(a)">
                  {{ 'attachments.download' | translate }}
                </button>
              </li>
            }
          </ul>
        }
      </div>
      @if (actionError()) {
        <p role="alert" class="mt-2 text-sm text-danger">{{ 'attachments.' + actionError() | translate }}</p>
      }

      <div class="mt-4">
        <label for="att-file" class="block text-sm font-medium">{{ 'attachments.upload' | translate }}</label>
        <input id="att-file" type="file" [accept]="accept" [disabled]="uploading()" (change)="pick($event)"
          class="mt-1 block w-full text-sm focus-visible:outline-2 focus-visible:outline-brand" />
        <p class="mt-1 text-xs text-muted">{{ 'attachments.allowed' | translate }}</p>
        @if (staff()) {
          <label class="mt-2 flex items-center gap-2 text-sm">
            <input type="checkbox" [checked]="internal()" (change)="internal.set(!internal())" />
            {{ 'attachments.internal' | translate }}
          </label>
        }
      </div>
    </section>
  `,
})
export class TicketAttachments {
  readonly ticketId = input.required<string>();
  readonly staff = input(false);

  private readonly api = inject(TicketsApi);
  private readonly tenant = inject(TenantService);

  protected readonly accept = ALLOWED_EXTENSIONS.map((e) => '.' + e).join(',');
  protected readonly items = signal<AttachmentDto[]>([]);
  protected readonly loading = signal(false);
  protected readonly error = signal(false);
  protected readonly uploading = signal(false);
  protected readonly actionError = signal<'badType' | 'tooLarge' | 'uploadError' | 'downloadError' | null>(null);
  protected readonly internal = signal(false);

  constructor() {
    effect(() => {
      this.ticketId();
      untracked(() => this.load());
    });
  }

  protected size(a: AttachmentDto): string {
    const bytes = Number(a.sizeBytes);
    return bytes >= 1024 * 1024 ? `${(bytes / 1024 / 1024).toFixed(1)} MB` : `${Math.max(1, Math.round(bytes / 1024))} KB`;
  }

  protected load(): void {
    const tenantId = this.tenant.tenantId();
    this.loading.set(true);
    this.error.set(false);
    this.api.attachments(this.ticketId()).subscribe({
      next: (r) => {
        if (this.tenant.tenantId() !== tenantId) return;
        this.items.set(r);
        this.loading.set(false);
      },
      error: () => {
        this.error.set(true);
        this.loading.set(false);
      },
    });
  }

  protected pick(event: Event): void {
    const input = event.target as HTMLInputElement;
    const file = input.files?.[0];
    this.actionError.set(null);
    if (!file) return;
    const problem = attachmentProblem(file);
    if (problem) {
      this.actionError.set(problem);
      input.value = '';
      return;
    }
    this.uploading.set(true);
    this.api.upload(this.ticketId(), file, this.staff() && this.internal()).subscribe({
      next: (a) => {
        this.items.update((list) => [...list, a]);
        this.uploading.set(false);
        input.value = '';
      },
      error: () => {
        this.actionError.set('uploadError');
        this.uploading.set(false);
        input.value = '';
      },
    });
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
      error: () => this.actionError.set('downloadError'),
    });
  }
}
