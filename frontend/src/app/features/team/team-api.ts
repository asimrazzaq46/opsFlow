import { Injectable, inject } from '@angular/core';
import { Observable, map, timeout } from 'rxjs';
import { SkillDto, TenantRole } from '../../core/api/generated/models';
import { TeamService as GeneratedTeamService } from '../../core/api/generated/services/team.service';
import { TenantsService } from '../../core/api/generated/services/tenants.service';
import { TenantService } from '../../core/auth/tenant.service';
import {
  SkillsDocument,
  SkillsQuery,
  TeamDirectoryDocument,
  TeamDirectoryQuery,
  TeamMemberDocument,
  TeamMemberQuery,
  TenantRole as GqlTenantRole,
} from '../../core/graphql/generated';
import { GraphqlClient } from '../../core/graphql/graphql-client';
import { pascal } from '../../shared/format';

export type TeamPage = TeamDirectoryQuery['teamDirectory'];
export type TeamRow = TeamPage['items'][number];
export type TeamMemberDetail = TeamMemberQuery['teamMember'];
export type Skill = SkillsQuery['skills'][number];
export type TeamSort = 'name' | 'role' | 'status' | 'joined';

export interface TeamQuery {
  search: string;
  role: GqlTenantRole | null;
  isActive: boolean | null;
  skillId: string | null;
  sortBy: TeamSort;
  descending: boolean;
  page: number;
  pageSize: number;
}

/** Fields of a member the role/status actions need; both the list row and the detail provide them. */
export interface ManagedMember {
  membershipId: string;
  displayName: string;
  role: GqlTenantRole;
  isActive: boolean;
}

const WRITE_TIMEOUT_MS = 20_000;

/** GraphQL names roles in SCREAMING_CASE, REST in PascalCase; both describe the same server enum. */
export const restRole = (role: GqlTenantRole): TenantRole => pascal(role) as TenantRole;

/** Reads go through GraphQL, writes through the generated REST commands; the workspace comes from the active tenant. */
@Injectable({ providedIn: 'root' })
export class TeamApi {
  private readonly gql = inject(GraphqlClient);
  private readonly tenants = inject(TenantsService);
  private readonly skillCatalog = inject(GeneratedTeamService);
  private readonly tenant = inject(TenantService);

  private get tenantId(): string {
    const id = this.tenant.tenantId();
    if (!id) throw new Error('No active workspace');
    return id;
  }

  directory(q: TeamQuery): Observable<TeamPage> {
    return this.gql
      .query(TeamDirectoryDocument, {
        search: q.search || null,
        role: q.role,
        isActive: q.isActive,
        skillId: q.skillId,
        sortBy: q.sortBy,
        descending: q.descending,
        page: q.page,
        pageSize: q.pageSize,
      })
      .pipe(map((r) => r.teamDirectory));
  }

  member(membershipId: string): Observable<TeamMemberDetail> {
    return this.gql.query(TeamMemberDocument, { membershipId }).pipe(map((r) => r.teamMember));
  }

  skills(includeInactive = false): Observable<Skill[]> {
    return this.gql.query(SkillsDocument, { includeInactive }).pipe(map((r) => r.skills));
  }

  changeRole(m: ManagedMember, role: TenantRole): Observable<void> {
    return this.tenants
      .changeMemberRole({
        tenantId: this.tenantId,
        membershipId: m.membershipId,
        body: { role, expectedRole: restRole(m.role) },
      })
      .pipe(timeout(WRITE_TIMEOUT_MS));
  }

  setStatus(m: ManagedMember, isActive: boolean, acknowledgeOpenWork: boolean): Observable<void> {
    return this.tenants
      .setMemberStatus({
        tenantId: this.tenantId,
        membershipId: m.membershipId,
        body: { isActive, expectedRole: restRole(m.role), acknowledgeOpenWork },
      })
      .pipe(timeout(WRITE_TIMEOUT_MS));
  }

  /** expectedUpdatedAtUtc is the profile version the editor saw (null: there was none), so a concurrent edit is refused. */
  updateProfile(
    membershipId: string,
    profile: { jobTitle: string; department: string; phone: string },
    expectedUpdatedAtUtc: string | null,
  ): Observable<void> {
    return this.tenants
      .updateMemberProfile({
        tenantId: this.tenantId,
        membershipId,
        body: { ...profile, checkVersion: true, expectedUpdatedAtUtc },
      })
      .pipe(timeout(WRITE_TIMEOUT_MS));
  }

  /** Replaces the member's skills; expectedSkillIds is the set the editor saw. */
  setSkills(membershipId: string, skillIds: string[], expectedSkillIds: string[]): Observable<void> {
    return this.tenants
      .setMemberSkills({ tenantId: this.tenantId, membershipId, body: { skillIds, expectedSkillIds } })
      .pipe(timeout(WRITE_TIMEOUT_MS));
  }

  createSkill(name: string): Observable<SkillDto> {
    return this.skillCatalog.createSkill({ tenantId: this.tenantId, body: { name } }).pipe(timeout(WRITE_TIMEOUT_MS));
  }
}
