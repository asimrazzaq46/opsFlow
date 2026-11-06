import { HttpErrorResponse } from '@angular/common/http';
import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  Injector,
  afterNextRender,
  computed,
  effect,
  inject,
  input,
  output,
  signal,
  untracked,
  viewChild,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormControl, FormsModule, ReactiveFormsModule, Validators } from '@angular/forms';
import { TranslatePipe } from '@ngx-translate/core';
import { Subscription, filter, fromEvent, timeout } from 'rxjs';
import { TicketMessageDto } from '../../core/api/generated/models';
import { AuthService } from '../../core/auth/auth.service';
import { TenantService } from '../../core/auth/tenant.service';
import { ChatUnreadService } from '../../core/chat/chat-unread.service';
import { RealtimeService } from '../../core/realtime/realtime.service';
import { LanguageService } from '../../i18n/language.service';
import { mergeBySequence } from './merge';
import { TicketsApi } from './tickets-api';

interface MessageEvent {
  tenantId: string;
  ticketId: string;
  message: TicketMessageDto;
}

/** The user's own message that is not stored yet. It keeps its idempotency key, so a retry can never create a duplicate. */
interface PendingMessage {
  clientId: string;
  content: string;
  state: 'sending' | 'failed';
  errorKey: string;
  retryable: boolean;
}

interface MessageRow {
  message: TicketMessageDto;
  mine: boolean;
  tone: 'mine' | 'staff' | 'customer';
  /** First message of a run by the same sender within a few minutes: shows the sender line. */
  header: boolean;
  label: string | null;
  labelKey: string | null;
  roleKey: string | null;
  time: string;
}

/** Highest sequence of a REST page (pages are complete up to their newest item). */
const maxSequence = (items: readonly TicketMessageDto[], floor: number): number =>
  items.reduce((max, m) => Math.max(max, Number(m.sequence)), floor);

const PAGE_SIZE = 30;
const SYNC_PAGE_SIZE = 100;
const SEND_TIMEOUT_MS = 20_000;
const GROUP_GAP_MS = 5 * 60_000;
const NEAR_BOTTOM_PX = 80;

@Component({
  selector: 'app-ticket-chat',
  // FormsModule provides NgForm, whose ngSubmit prevents the native submit (a page reload).
  imports: [FormsModule, ReactiveFormsModule, TranslatePipe],
  templateUrl: './ticket-chat.html',
  styleUrl: './ticket-chat.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class TicketChat {
  readonly ticketId = input.required<string>();
  /** Closed tickets reject new messages on the server; the composer reflects it instead of failing on send. */
  readonly closed = input(false);
  /** The Conversation tab is the selected one; messages count as read only while it is shown. */
  readonly active = input(true);
  /** The server reported a conflict or refusal: the parent reloads the ticket so status and permissions are current. */
  readonly stale = output<void>();

  private readonly api = inject(TicketsApi);
  private readonly tenant = inject(TenantService);
  private readonly auth = inject(AuthService);
  private readonly realtime = inject(RealtimeService);
  private readonly language = inject(LanguageService);
  private readonly chatUnread = inject(ChatUnreadService);
  private readonly injector = inject(Injector);
  private readonly log = viewChild<ElementRef<HTMLElement>>('log');

  protected readonly staffView = this.tenant.isStaff;
  protected readonly messages = signal<TicketMessageDto[]>([]);
  protected readonly loading = signal(false);
  protected readonly loadingOlder = signal(false);
  protected readonly error = signal(false);
  protected readonly syncError = signal(false);
  protected readonly olderError = signal(false);
  protected readonly hasOlder = signal(false);
  protected readonly pending = signal<PendingMessage | null>(null);
  protected readonly unseen = signal(false);
  /** The one message whose send time is shown; timestamps stay hidden otherwise to keep the conversation readable. */
  protected readonly selectedId = signal<string | null>(null);
  protected readonly text = new FormControl('', {
    nonNullable: true,
    validators: [Validators.required, Validators.maxLength(4000), Validators.pattern(/\S/)],
  });

  private readonly timeFormat = computed(() => ({
    today: new Intl.DateTimeFormat(this.language.current(), { timeStyle: 'short' }),
    older: new Intl.DateTimeFormat(this.language.current(), { dateStyle: 'medium', timeStyle: 'short' }),
  }));

  protected readonly rows = computed<MessageRow[]>(() => {
    const me = this.auth.user()?.id;
    const staffView = this.staffView();
    const format = this.timeFormat();
    let previous: TicketMessageDto | undefined;
    return this.messages().map((m) => {
      const mine = m.senderUserId !== null && m.senderUserId === me;
      const sameSender =
        previous !== undefined &&
        previous.senderUserId === m.senderUserId &&
        previous.senderIsStaff === m.senderIsStaff &&
        Date.parse(m.createdAtUtc) - Date.parse(previous.createdAtUtc) < GROUP_GAP_MS;
      previous = m;
      // Customers never receive staff identities, so every staff message is shown as the support team.
      const label = mine || !staffView ? null : m.senderDisplayName;
      return {
        message: m,
        mine,
        tone: mine ? 'mine' : m.senderIsStaff ? 'staff' : 'customer',
        header: !sameSender,
        label,
        labelKey: mine
          ? 'chat.you'
          : label
            ? null
            : m.senderIsStaff
              ? 'chat.supportTeam'
              : 'chat.customer',
        roleKey: staffView && !mine ? (m.senderIsStaff ? 'chat.roleStaff' : 'chat.roleCustomer') : null,
        time: this.formatTime(format, new Date(m.createdAtUtc)),
      };
    });
  });

  protected readonly canCompose = computed(() => !this.closed() && !this.pending());

  private olderPage = 1;
  private loaded = false;
  /** Newest sequence up to which the history is known to be complete (REST pages only; live events can arrive out of order). */
  private completeThrough = 0;
  private lastRead = 0;
  private marking = false;
  private syncRequested = false;
  private syncing = false;
  private generation = 0;
  private initRequest?: Subscription;

  constructor() {
    this.realtime
      .on<MessageEvent>('ticket.message.created')
      .pipe(takeUntilDestroyed())
      .subscribe((e) => {
        if (e.tenantId !== this.tenant.tenantId() || e.ticketId !== this.ticketId()) return;
        this.merge([e.message]);
        // While the conversation is shown, confirm the history through the server before marking anything read.
        if (this.presented() && Number(e.message.sequence) > this.completeThrough) this.requestSync();
      });
    fromEvent(document, 'visibilitychange')
      .pipe(
        filter(() => this.presented()),
        takeUntilDestroyed(),
      )
      .subscribe(() => this.requestSync());
    let lastActive = this.active();
    effect(() => {
      const active = this.active();
      if (active && !lastActive) untracked(() => this.requestSync());
      lastActive = active;
    });
    // Catch up after every (re)connect; this also covers messages sent between the history request and the first connect.
    this.realtime.reconnected.pipe(takeUntilDestroyed()).subscribe(() => this.requestSync());
    let lastState = this.realtime.state();
    effect(() => {
      const state = this.realtime.state();
      if (state === 'connected' && lastState !== 'connected') untracked(() => this.requestSync());
      lastState = state;
    });
    effect(() => {
      this.ticketId();
      untracked(() => this.init());
    });
  }

  protected init(): void {
    const generation = ++this.generation;
    this.initRequest?.unsubscribe();
    this.messages.set([]);
    this.olderPage = 1;
    this.loaded = false;
    this.completeThrough = 0;
    this.lastRead = 0;
    this.selectedId.set(null);
    this.unseen.set(false);
    this.loading.set(true);
    this.error.set(false);
    this.syncError.set(false);
    this.initRequest = this.api.messages(this.ticketId(), { page: 1, pageSize: PAGE_SIZE }).subscribe({
      next: (r) => {
        if (generation !== this.generation) return;
        this.messages.update((list) => mergeBySequence(list, r.items));
        this.completeThrough = maxSequence(r.items, 0);
        this.hasOlder.set(r.items.length < Number(r.totalCount));
        this.loading.set(false);
        this.loaded = true;
        this.scrollToBottom();
        if (this.syncRequested) this.sync();
        else this.markRead();
      },
      error: () => {
        if (generation !== this.generation) return;
        this.error.set(true);
        this.loading.set(false);
      },
    });
  }

  protected loadOlder(): void {
    if (this.loadingOlder()) return;
    const generation = this.generation;
    this.loadingOlder.set(true);
    this.olderError.set(false);
    this.api
      .messages(this.ticketId(), { page: this.olderPage + 1, pageSize: PAGE_SIZE })
      .subscribe({
        next: (r) => {
          if (generation !== this.generation) return;
          this.olderPage++;
          const el = this.log()?.nativeElement;
          const fromBottom = el ? el.scrollHeight - el.scrollTop : 0;
          this.messages.update((list) => mergeBySequence(list, r.items));
          this.hasOlder.set(this.olderPage * PAGE_SIZE < Number(r.totalCount));
          this.loadingOlder.set(false);
          // Keep the reader's position while older messages are inserted above it.
          afterNextRender(() => el && (el.scrollTop = el.scrollHeight - fromBottom), { injector: this.injector });
        },
        error: () => {
          if (generation !== this.generation) return;
          this.loadingOlder.set(false);
          this.olderError.set(true);
        },
      });
  }

  protected requestSync(): void {
    this.syncRequested = true;
    if (this.loaded) this.sync();
  }

  /**
   * Fetches every message after the last complete REST position, page by page, and merges by id so live events never duplicate.
   * Starting from that position (not the newest message held) also fills gaps left by live events that arrived out of order.
   */
  private sync(): void {
    if (this.syncing) return; // the running pass re-checks syncRequested when it finishes
    this.syncing = true;
    this.syncRequested = false;
    this.syncError.set(false);
    const generation = this.generation;
    const step = (): void => {
      this.api.messages(this.ticketId(), { afterSequence: this.completeThrough, pageSize: SYNC_PAGE_SIZE }).subscribe({
        next: (r) => {
          if (generation !== this.generation) return this.finishSync(false);
          this.merge(r.items);
          this.completeThrough = maxSequence(r.items, this.completeThrough);
          if (r.items.length === SYNC_PAGE_SIZE) step();
          else this.finishSync(false);
        },
        error: () => this.finishSync(generation === this.generation),
      });
    };
    step();
  }

  private finishSync(failed: boolean): void {
    this.syncing = false;
    if (failed) this.syncError.set(true);
    else if (this.syncRequested && this.loaded) this.sync();
    else this.markRead();
  }

  /** Shown to the user right now: history loaded, Conversation tab selected and the page visible. */
  private presented(): boolean {
    return this.loaded && this.active() && document.visibilityState === 'visible';
  }

  /**
   * Marks the complete history as read on the server. Only a sequence obtained through REST is sent, so a note's sequence or an unseen
   * message is never acknowledged. A failure keeps the unread state; the next trigger retries.
   */
  private markRead(): void {
    const sequence = this.completeThrough;
    if (!this.presented() || this.marking || sequence <= this.lastRead) return;
    const generation = this.generation;
    this.marking = true;
    this.api.markRead(this.ticketId(), sequence).subscribe({
      next: (cursor) => {
        this.marking = false;
        if (generation !== this.generation) return;
        this.lastRead = Math.max(this.lastRead, Number(cursor.readSequence));
        this.chatUnread.refresh();
        this.markRead(); // newer history may have completed meanwhile
      },
      error: () => (this.marking = false),
    });
  }

  protected toggleTime(id: string): void {
    // Selecting text inside a bubble must not toggle its timestamp.
    if (window.getSelection()?.toString()) return;
    this.selectedId.update((current) => (current === id ? null : id));
    // Reveal the timestamp without jumping: scroll only as far as needed when it would open below the visible area.
    afterNextRender(
      () => this.log()?.nativeElement.querySelector(`#chat-time-${CSS.escape(id)}`)?.scrollIntoView({ block: 'nearest' }),
      { injector: this.injector },
    );
  }

  private formatTime(format: { today: Intl.DateTimeFormat; older: Intl.DateTimeFormat }, at: Date): string {
    return at.toDateString() === new Date().toDateString() ? format.today.format(at) : format.older.format(at);
  }

  protected send(): void {
    if (this.text.invalid || !this.canCompose()) return;
    const pending: PendingMessage = {
      clientId: crypto.randomUUID(),
      content: this.text.value.trim(),
      state: 'sending',
      errorKey: '',
      retryable: false,
    };
    this.text.reset('');
    this.post(pending);
  }

  protected retry(): void {
    const p = this.pending();
    if (p?.state === 'failed' && p.retryable && !this.closed()) this.post(p);
  }

  /** Gives the unsent text back to the composer so nothing typed is lost. */
  protected discard(): void {
    const p = this.pending();
    if (!p || p.state === 'sending') return;
    if (!this.text.value.trim()) this.text.setValue(p.content);
    this.pending.set(null);
  }

  protected onKeydown(event: KeyboardEvent): void {
    if (event.key === 'Enter' && (event.ctrlKey || event.metaKey)) {
      event.preventDefault();
      this.send();
    }
  }

  protected onScroll(): void {
    if (this.nearBottom()) this.unseen.set(false);
  }

  protected jumpToLatest(): void {
    this.unseen.set(false);
    this.scrollToBottom();
  }

  private post(p: PendingMessage): void {
    const ticketId = this.ticketId();
    const generation = this.generation;
    this.pending.set({ ...p, state: 'sending', errorKey: '', retryable: false });
    this.scrollToBottom();
    this.api
      .sendMessage(ticketId, p.content, p.clientId)
      .pipe(timeout(SEND_TIMEOUT_MS))
      .subscribe({
        next: (m) => {
          if (generation !== this.generation) return;
          this.pending.set(null);
          this.messages.update((list) => mergeBySequence(list, [m]));
          this.scrollToBottom();
        },
        error: (e: unknown) => {
          if (generation !== this.generation) return;
          const failure = this.describe(e);
          this.pending.set({ ...p, state: 'failed', ...failure });
          this.scrollToBottom(); // keep the failed message and its Retry/Discard actions in view
          if (e instanceof HttpErrorResponse && [403, 404, 409].includes(e.status)) this.stale.emit();
        },
      });
  }

  private describe(e: unknown): Pick<PendingMessage, 'errorKey' | 'retryable'> {
    const status = e instanceof HttpErrorResponse ? e.status : 0;
    switch (status) {
      case 400:
        return { errorKey: 'chat.errors.invalid', retryable: false };
      case 403:
      case 404:
        return { errorKey: 'chat.errors.forbidden', retryable: false };
      case 409:
        // Either the ticket was closed or a concurrent write won; the ticket reload decides whether retrying is still possible.
        return { errorKey: 'chat.errors.conflict', retryable: true };
      default:
        return { errorKey: 'chat.sendError', retryable: true };
    }
  }

  private merge(incoming: TicketMessageDto[]): void {
    const known = new Set(this.messages().map((m) => m.id));
    const fresh = incoming.filter((m) => !known.has(m.id));
    if (!fresh.length) return;
    const stick = this.nearBottom();
    this.messages.update((list) => mergeBySequence(list, fresh));
    if (stick) this.scrollToBottom();
    else if (fresh.some((m) => m.senderUserId !== this.auth.user()?.id)) this.unseen.set(true);
  }

  private nearBottom(): boolean {
    const el = this.log()?.nativeElement;
    return !el || el.scrollHeight - el.scrollTop - el.clientHeight < NEAR_BOTTOM_PX;
  }

  // Instant on purpose: a smooth scroll of the container was observed not to move at all, leaving new messages out of view.
  private scrollToBottom(): void {
    afterNextRender(
      () => {
        const el = this.log()?.nativeElement;
        if (el) el.scrollTop = el.scrollHeight;
      },
      { injector: this.injector },
    );
  }
}
