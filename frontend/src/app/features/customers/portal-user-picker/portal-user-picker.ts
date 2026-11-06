import { ChangeDetectionStrategy, Component, computed, inject, output, signal } from '@angular/core';
import { rxResource, toSignal } from '@angular/core/rxjs-interop';
import { FormControl, ReactiveFormsModule } from '@angular/forms';
import { TranslatePipe } from '@ngx-translate/core';
import { debounceTime, distinctUntilChanged, map, startWith } from 'rxjs';
import { TenantService } from '../../../core/auth/tenant.service';
import { CustomersApi } from '../customers-api';

let nextId = 0;

/**
 * Searchable list of the workspace's Customer members that have no contact yet. The server restricts the candidates (current tenant,
 * active Customer membership and user, not already linked); this component only displays and reports the chosen user id.
 */
@Component({
  selector: 'app-portal-user-picker',
  imports: [ReactiveFormsModule, TranslatePipe],
  templateUrl: './portal-user-picker.html',
  styleUrl: './portal-user-picker.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class PortalUserPicker {
  private readonly tenant = inject(TenantService);
  private readonly api = inject(CustomersApi);

  /** The chosen user id, or null when "do not link" is selected. */
  readonly selected = output<string | null>();

  protected readonly uid = `portal-picker-${nextId++}`;
  protected readonly search = new FormControl('', { nonNullable: true });
  protected readonly choice = signal<string | null>(null);

  private readonly term = toSignal(
    this.search.valueChanges.pipe(
      debounceTime(300),
      map((v) => v.trim()),
      distinctUntilChanged(),
      startWith(''),
    ),
    { initialValue: '' },
  );

  /** Results are tagged with their workspace so a response for a previous workspace is never listed. */
  protected readonly linkable = rxResource({
    params: () => {
      const tenantId = this.tenant.tenantId();
      return tenantId ? { tenantId, search: this.term() } : undefined;
    },
    stream: ({ params }) =>
      this.api
        .linkableUsers(params.search)
        .pipe(map((users) => ({ tenantId: params.tenantId, users }))),
  });
  protected readonly users = computed(() => {
    const r = this.linkable.hasValue() ? this.linkable.value() : undefined;
    return r && r.tenantId === this.tenant.tenantId() ? r.users : null;
  });

  /** Refreshes the candidates, e.g. after the server rejected the chosen user. */
  reload(): void {
    this.choice.set(null);
    this.selected.emit(null);
    this.linkable.reload();
  }

  protected pick(userId: string | null): void {
    this.choice.set(userId);
    this.selected.emit(userId);
  }
}
