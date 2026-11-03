import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { Router, RouterLink } from '@angular/router';
import { TranslatePipe } from '@ngx-translate/core';
import { OrganizationDto } from '../../../core/api/generated/models';
import { AuthService } from '../../../core/auth/auth.service';
import { TenantService } from '../../../core/auth/tenant.service';
import { initials } from '../workspace-api';

/**
 * Lists only the memberships returned by `GET /organizations` (always reloaded here, never cached across sessions). Choosing one
 * goes through `TenantService.select`, the same path as the header switcher, so tenant-scoped views reset and reload.
 */
@Component({
  selector: 'app-workspace-select',
  imports: [RouterLink, TranslatePipe],
  templateUrl: './workspace-select.html',
  styleUrl: './workspace-select.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class WorkspaceSelect {
  protected readonly auth = inject(AuthService);
  protected readonly tenant = inject(TenantService);
  private readonly router = inject(Router);

  protected readonly initials = initials;
  protected readonly query = signal('');
  protected readonly filtered = computed(() => {
    const q = this.query().trim().toLowerCase();
    const orgs = this.tenant.organizations();
    return q ? orgs.filter((o) => o.name.toLowerCase().includes(q) || o.slug.toLowerCase().includes(q)) : orgs;
  });

  constructor() {
    this.reload();
  }

  protected reload(): void {
    this.tenant.load().subscribe({ error: () => undefined }); // the error state is rendered from tenant.error()
  }

  protected open(org: OrganizationDto): void {
    if (this.tenant.select(org.id)) void this.router.navigateByUrl('/dashboard');
  }

  protected search(event: Event): void {
    this.query.set((event.target as HTMLInputElement).value);
  }
}
