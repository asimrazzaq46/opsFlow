import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { Api } from '../../core/api/generated/api';
import {
  acceptInvitation,
  createOrganization,
  previewInvitation,
} from '../../core/api/generated/functions';
import { InvitationPreviewDto, OrganizationDto } from '../../core/api/generated/models';

/** Workspace onboarding. Memberships and roles are created only by the server (owner on create, invited role on accept). */
@Injectable({ providedIn: 'root' })
export class WorkspaceApi {
  private readonly api = inject(Api);

  create(name: string): Observable<OrganizationDto> {
    return this.api.invoke(createOrganization, { body: { name } });
  }

  /** Anonymous and read-only: what the token holder may see before signing in. The token is sent in the body, never in a URL. */
  previewInvitation(token: string): Observable<InvitationPreviewDto> {
    return this.api.invoke(previewInvitation, { body: { token } });
  }

  acceptInvitation(token: string): Observable<OrganizationDto> {
    return this.api.invoke(acceptInvitation, { body: { token } });
  }
}

/** Initials for a workspace tile, derived from the server-provided name. */
export function initials(name: string): string {
  const parts = name.trim().split(/\s+/).filter(Boolean);
  return (
    (parts[0]?.[0] ?? '') + (parts.length > 1 ? parts[parts.length - 1][0] : (parts[0]?.[1] ?? ''))
  ).toUpperCase();
}
