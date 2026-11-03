import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { Api } from '../../core/api/generated/api';
import { acceptInvitation, createOrganization } from '../../core/api/generated/functions';
import { OrganizationDto } from '../../core/api/generated/models';

/** Workspace onboarding. Memberships and roles are created only by the server (owner on create, invited role on accept). */
@Injectable({ providedIn: 'root' })
export class WorkspaceApi {
  private readonly api = inject(Api);

  create(name: string): Observable<OrganizationDto> {
    return this.api.invoke(createOrganization, { body: { name } });
  }

  acceptInvitation(token: string): Observable<OrganizationDto> {
    return this.api.invoke(acceptInvitation, { body: { token } });
  }
}

/** Initials for a workspace tile, derived from the server-provided name. */
export function initials(name: string): string {
  const parts = name.trim().split(/\s+/).filter(Boolean);
  return ((parts[0]?.[0] ?? '') + (parts.length > 1 ? parts[parts.length - 1][0] : (parts[0]?.[1] ?? ''))).toUpperCase();
}
