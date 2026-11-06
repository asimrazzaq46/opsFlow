/** Internal type. DO NOT USE DIRECTLY. */
type Exact<T extends { [key: string]: unknown }> = { [K in keyof T]: T[K] };
/** Internal type. DO NOT USE DIRECTLY. */
export type Incremental<T> = T | { [P in keyof T]?: P extends ' $fragmentName' | '__typename' ? T[P] : never };
import type { TypedDocumentNode as DocumentNode } from '@graphql-typed-document-node/core';
export type AnalyticsFilterInput = {
  agentMembershipId?: string | null | undefined;
  categoryId?: string | null | undefined;
  customerId?: string | null | undefined;
  from?: string | null | undefined;
  priority?: TicketPriority | null | undefined;
  status?: TicketStatus | null | undefined;
  to?: string | null | undefined;
};

export type AuditAction =
  | 'MEMBER_PROFILE_UPDATED'
  | 'MEMBER_ROLE_CHANGED'
  | 'MEMBER_SKILLS_CHANGED'
  | 'MEMBER_STATUS_CHANGED'
  | 'SKILL_CREATED'
  | 'SKILL_UPDATED';

export type KanbanFilterInput = {
  assignedMembershipId?: string | null | undefined;
  assignedToMe?: boolean;
  categoryId?: string | null | undefined;
  includeClosed?: boolean;
  overdue?: boolean | null | undefined;
  page?: number | null | undefined;
  priority?: TicketPriority | null | undefined;
  search?: string | null | undefined;
};

export type SlaCycleReason =
  | 'INITIAL'
  | 'REOPENED';

export type SlaState =
  | 'MET'
  | 'MISSED'
  | 'NOT_APPLICABLE'
  | 'OVERDUE'
  | 'RUNNING';

export type TenantRole =
  | 'AGENT'
  | 'CUSTOMER'
  | 'MANAGER'
  | 'TENANT_OWNER';

export type TicketActivityType =
  | 'AGENT_ASSIGNED'
  | 'AGENT_UNASSIGNED'
  | 'ATTACHMENT_ADDED'
  | 'CATEGORY_CHANGED'
  | 'CREATED'
  | 'DETAILS_UPDATED'
  | 'PRIORITY_CHANGED'
  | 'REOPENED'
  | 'RESOLVED'
  | 'SLA_CYCLE_COMPLETED'
  | 'SLA_CYCLE_STARTED'
  | 'STATUS_CHANGED'
  | 'TASKS_REORDERED'
  | 'TASK_ASSIGNED'
  | 'TASK_CREATED'
  | 'TASK_DETAILS_UPDATED'
  | 'TASK_STATUS_CHANGED';

export type TicketPriority =
  | 'HIGH'
  | 'LOW'
  | 'MEDIUM'
  | 'URGENT';

export type TicketStatus =
  | 'ASSIGNED'
  | 'CLOSED'
  | 'IN_PROGRESS'
  | 'ON_HOLD'
  | 'OPEN'
  | 'RESOLVED';

export type ChatUnreadSummaryQueryVariables = Exact<{ [key: string]: never; }>;


export type ChatUnreadSummaryQuery = { chatUnreadSummary: { total: number } };

export type DashboardQueryVariables = Exact<{
  filter?: AnalyticsFilterInput | null | undefined;
}>;


export type DashboardQuery = { dashboard: { generatedAtUtc: string, timeZone: string, fromUtc: string, toUtc: string, grouping: string, createdInPeriod: number, resolvedInPeriod: number, avgFirstResponseMinutes: number | null, firstResponseSample: number, current: { openTickets: number, unassignedTickets: number, overdueTickets: number, resolvedToday: number, assignedToMe: number | null }, sla: { eligibleCycles: number, met: number, breached: number, compliancePercent: number | null } | null, trend: Array<{ periodStartUtc: string, created: number, resolved: number, avgResolutionMinutes: number | null }>, openByStatus: Array<{ key: string, count: number }>, byPriority: Array<{ key: string, count: number }>, workload: Array<{ membershipId: string, displayName: string, activeTickets: number }> | null } };

export type CustomersQueryVariables = Exact<{
  search?: string | null | undefined;
  isActive?: boolean | null | undefined;
  sortBy?: string | null | undefined;
  descending?: boolean | null | undefined;
  page?: number | null | undefined;
  pageSize?: number | null | undefined;
}>;


export type CustomersQuery = { customers: { page: number, pageSize: number, totalCount: number, items: Array<{ id: string, name: string, email: string, phone: string | null, isActive: boolean, linkedUserId: string | null, linkedUserEmail: string | null, linkedUserName: string | null, createdAtUtc: string, updatedAtUtc: string }> } };

export type CustomerQueryVariables = Exact<{
  id: string;
}>;


export type CustomerQuery = { customer: { id: string, name: string, email: string, phone: string | null, isActive: boolean, linkedUserId: string | null, linkedUserEmail: string | null, linkedUserName: string | null, createdAtUtc: string, updatedAtUtc: string } };

export type LinkableCustomerUsersQueryVariables = Exact<{
  search?: string | null | undefined;
}>;


export type LinkableCustomerUsersQuery = { linkableCustomerUsers: Array<{ userId: string, email: string, displayName: string }> };

export type TeamMembersQueryVariables = Exact<{
  page?: number | null | undefined;
  pageSize?: number | null | undefined;
}>;


export type TeamMembersQuery = { teamMembers: { page: number, pageSize: number, totalCount: number, items: Array<{ id: string, userId: string, email: string | null, displayName: string, role: TenantRole, isActive: boolean, createdAtUtc: string }> } };

export type TicketSlaHistoryQueryVariables = Exact<{
  ticketId: string;
}>;


export type TicketSlaHistoryQuery = { ticketSla: { cycles: Array<{ id: string, cycleNumber: number, reason: SlaCycleReason, startedAtUtc: string, responseDueAtUtc: string | null, resolutionDueAtUtc: string, firstResponseAtUtc: string | null, resolvedAtUtc: string | null, responseState: SlaState, resolutionState: SlaState }> } };

export type TicketCreationEligibilityQueryVariables = Exact<{ [key: string]: never; }>;


export type TicketCreationEligibilityQuery = { ticketCreationEligibility: { canCreate: boolean, reason: string | null } };

export type BoardTicketFragment = { id: string, displayNumber: string, title: string, priority: TicketPriority, status: TicketStatus, assignedToMe: boolean | null, categoryName: string, customerName: string | null, updatedAtUtc: string, version: number, assigneeCount: number | null, resolutionDueAtUtc: string | null, isOverdue: boolean | null, assignees: Array<{ membershipId: string, userId: string, displayName: string, isAgentActive: boolean }> | null };

export type KanbanBoardQueryVariables = Exact<{
  filter?: KanbanFilterInput | null | undefined;
  perColumn?: number | null | undefined;
}>;


export type KanbanBoardQuery = { kanbanBoard: Array<{ status: TicketStatus, total: number, tickets: Array<{ id: string, displayNumber: string, title: string, priority: TicketPriority, status: TicketStatus, assignedToMe: boolean | null, categoryName: string, customerName: string | null, updatedAtUtc: string, version: number, assigneeCount: number | null, resolutionDueAtUtc: string | null, isOverdue: boolean | null, assignees: Array<{ membershipId: string, userId: string, displayName: string, isAgentActive: boolean }> | null }> }> };

export type TicketTaskProgressQueryVariables = Exact<{
  ticketIds: Array<string> | string;
}>;


export type TicketTaskProgressQuery = { ticketTaskProgress: Array<{ ticketId: string, totalTasks: number, completedTasks: number }> };

export type ReportQueryVariables = Exact<{
  filter?: AnalyticsFilterInput | null | undefined;
}>;


export type ReportQuery = { report: { generatedAtUtc: string, timeZone: string, fromUtc: string, toUtc: string, grouping: string, ticketsCreated: number, openOverdueTickets: number, trend: Array<{ periodStartUtc: string, created: number, resolved: number, avgResolutionMinutes: number | null }>, byStatus: Array<{ key: string, count: number }>, byPriority: Array<{ key: string, count: number }>, resolution: { completedCycles: number, avgMinutes: number | null, medianMinutes: number | null }, sla: { eligibleCycles: number, met: number, breached: number, compliancePercent: number | null }, reopen: { resolvedCycles: number, reopenedCycles: number, reopenRatePercent: number | null, reopenEventsInPeriod: number }, agents: Array<{ membershipId: string, displayName: string, currentWorkload: number, resolvedInPeriod: number }>, categories: Array<{ categoryId: string, name: string, count: number, sharePercent: number }>, customers: Array<{ customerId: string, name: string, tickets: number, open: number, resolved: number, overdueOpen: number }> } };

export type TeamDirectoryQueryVariables = Exact<{
  search?: string | null | undefined;
  role?: TenantRole | null | undefined;
  isActive?: boolean | null | undefined;
  skillId?: string | null | undefined;
  sortBy?: string | null | undefined;
  descending: boolean;
  page: number;
  pageSize: number;
}>;


export type TeamDirectoryQuery = { teamDirectory: { page: number, pageSize: number, totalCount: number, items: Array<{ membershipId: string, userId: string, displayName: string, email: string | null, role: TenantRole, isActive: boolean, jobTitle: string | null, department: string | null, openAssignmentCount: number, joinedAtUtc: string, skills: Array<{ id: string, name: string, isActive: boolean }> }> } };

export type TeamMemberQueryVariables = Exact<{
  membershipId: string;
}>;


export type TeamMemberQuery = { teamMember: { phone: string | null, profileUpdatedAtUtc: string | null, canEditProfileAndSkills: boolean, canChangeRoleAndStatus: boolean, member: { membershipId: string, userId: string, displayName: string, email: string | null, role: TenantRole, isActive: boolean, jobTitle: string | null, department: string | null, openAssignmentCount: number, joinedAtUtc: string, skills: Array<{ id: string, name: string, isActive: boolean }> }, stats: { openAssignmentCount: number, openTaskCount: number, resolveActions: number, resolvedTicketsDistinct: number }, assignedTickets: Array<{ ticketId: string, displayNumber: string, title: string, status: TicketStatus, priority: TicketPriority }>, recentActivity: Array<{ ticketId: string, displayNumber: string, type: TicketActivityType, occurredAtUtc: string }>, adminHistory: Array<{ action: AuditAction, actorDisplayName: string | null, detail: string | null, occurredAtUtc: string }> | null } };

export type SkillsQueryVariables = Exact<{
  includeInactive: boolean;
}>;


export type SkillsQuery = { skills: Array<{ id: string, name: string, isActive: boolean }> };

export const BoardTicketFragmentDoc = {"kind":"Document","definitions":[{"kind":"FragmentDefinition","name":{"kind":"Name","value":"BoardTicket"},"typeCondition":{"kind":"NamedType","name":{"kind":"Name","value":"TicketSummaryDto"}},"selectionSet":{"kind":"SelectionSet","selections":[{"kind":"Field","name":{"kind":"Name","value":"id"}},{"kind":"Field","name":{"kind":"Name","value":"displayNumber"}},{"kind":"Field","name":{"kind":"Name","value":"title"}},{"kind":"Field","name":{"kind":"Name","value":"priority"}},{"kind":"Field","name":{"kind":"Name","value":"status"}},{"kind":"Field","name":{"kind":"Name","value":"assignedToMe"}},{"kind":"Field","name":{"kind":"Name","value":"categoryName"}},{"kind":"Field","name":{"kind":"Name","value":"customerName"}},{"kind":"Field","name":{"kind":"Name","value":"updatedAtUtc"}},{"kind":"Field","name":{"kind":"Name","value":"version"}},{"kind":"Field","name":{"kind":"Name","value":"assigneeCount"}},{"kind":"Field","name":{"kind":"Name","value":"resolutionDueAtUtc"}},{"kind":"Field","name":{"kind":"Name","value":"isOverdue"}},{"kind":"Field","name":{"kind":"Name","value":"assignees"},"selectionSet":{"kind":"SelectionSet","selections":[{"kind":"Field","name":{"kind":"Name","value":"membershipId"}},{"kind":"Field","name":{"kind":"Name","value":"userId"}},{"kind":"Field","name":{"kind":"Name","value":"displayName"}},{"kind":"Field","name":{"kind":"Name","value":"isAgentActive"}}]}}]}}]} as unknown as DocumentNode<BoardTicketFragment, unknown>;
export const ChatUnreadSummaryDocument = {"kind":"Document","definitions":[{"kind":"OperationDefinition","operation":"query","name":{"kind":"Name","value":"ChatUnreadSummary"},"selectionSet":{"kind":"SelectionSet","selections":[{"kind":"Field","name":{"kind":"Name","value":"chatUnreadSummary"},"selectionSet":{"kind":"SelectionSet","selections":[{"kind":"Field","name":{"kind":"Name","value":"total"}}]}}]}}]} as unknown as DocumentNode<ChatUnreadSummaryQuery, ChatUnreadSummaryQueryVariables>;
export const DashboardDocument = {"kind":"Document","definitions":[{"kind":"OperationDefinition","operation":"query","name":{"kind":"Name","value":"Dashboard"},"variableDefinitions":[{"kind":"VariableDefinition","variable":{"kind":"Variable","name":{"kind":"Name","value":"filter"}},"type":{"kind":"NamedType","name":{"kind":"Name","value":"AnalyticsFilterInput"}}}],"selectionSet":{"kind":"SelectionSet","selections":[{"kind":"Field","name":{"kind":"Name","value":"dashboard"},"arguments":[{"kind":"Argument","name":{"kind":"Name","value":"filter"},"value":{"kind":"Variable","name":{"kind":"Name","value":"filter"}}}],"selectionSet":{"kind":"SelectionSet","selections":[{"kind":"Field","name":{"kind":"Name","value":"generatedAtUtc"}},{"kind":"Field","name":{"kind":"Name","value":"timeZone"}},{"kind":"Field","name":{"kind":"Name","value":"fromUtc"}},{"kind":"Field","name":{"kind":"Name","value":"toUtc"}},{"kind":"Field","name":{"kind":"Name","value":"grouping"}},{"kind":"Field","name":{"kind":"Name","value":"current"},"selectionSet":{"kind":"SelectionSet","selections":[{"kind":"Field","name":{"kind":"Name","value":"openTickets"}},{"kind":"Field","name":{"kind":"Name","value":"unassignedTickets"}},{"kind":"Field","name":{"kind":"Name","value":"overdueTickets"}},{"kind":"Field","name":{"kind":"Name","value":"resolvedToday"}},{"kind":"Field","name":{"kind":"Name","value":"assignedToMe"}}]}},{"kind":"Field","name":{"kind":"Name","value":"createdInPeriod"}},{"kind":"Field","name":{"kind":"Name","value":"resolvedInPeriod"}},{"kind":"Field","name":{"kind":"Name","value":"avgFirstResponseMinutes"}},{"kind":"Field","name":{"kind":"Name","value":"firstResponseSample"}},{"kind":"Field","name":{"kind":"Name","value":"sla"},"selectionSet":{"kind":"SelectionSet","selections":[{"kind":"Field","name":{"kind":"Name","value":"eligibleCycles"}},{"kind":"Field","name":{"kind":"Name","value":"met"}},{"kind":"Field","name":{"kind":"Name","value":"breached"}},{"kind":"Field","name":{"kind":"Name","value":"compliancePercent"}}]}},{"kind":"Field","name":{"kind":"Name","value":"trend"},"selectionSet":{"kind":"SelectionSet","selections":[{"kind":"Field","name":{"kind":"Name","value":"periodStartUtc"}},{"kind":"Field","name":{"kind":"Name","value":"created"}},{"kind":"Field","name":{"kind":"Name","value":"resolved"}},{"kind":"Field","name":{"kind":"Name","value":"avgResolutionMinutes"}}]}},{"kind":"Field","name":{"kind":"Name","value":"openByStatus"},"selectionSet":{"kind":"SelectionSet","selections":[{"kind":"Field","name":{"kind":"Name","value":"key"}},{"kind":"Field","name":{"kind":"Name","value":"count"}}]}},{"kind":"Field","name":{"kind":"Name","value":"byPriority"},"selectionSet":{"kind":"SelectionSet","selections":[{"kind":"Field","name":{"kind":"Name","value":"key"}},{"kind":"Field","name":{"kind":"Name","value":"count"}}]}},{"kind":"Field","name":{"kind":"Name","value":"workload"},"selectionSet":{"kind":"SelectionSet","selections":[{"kind":"Field","name":{"kind":"Name","value":"membershipId"}},{"kind":"Field","name":{"kind":"Name","value":"displayName"}},{"kind":"Field","name":{"kind":"Name","value":"activeTickets"}}]}}]}}]}}]} as unknown as DocumentNode<DashboardQuery, DashboardQueryVariables>;
export const CustomersDocument = {"kind":"Document","definitions":[{"kind":"OperationDefinition","operation":"query","name":{"kind":"Name","value":"Customers"},"variableDefinitions":[{"kind":"VariableDefinition","variable":{"kind":"Variable","name":{"kind":"Name","value":"search"}},"type":{"kind":"NamedType","name":{"kind":"Name","value":"String"}}},{"kind":"VariableDefinition","variable":{"kind":"Variable","name":{"kind":"Name","value":"isActive"}},"type":{"kind":"NamedType","name":{"kind":"Name","value":"Boolean"}}},{"kind":"VariableDefinition","variable":{"kind":"Variable","name":{"kind":"Name","value":"sortBy"}},"type":{"kind":"NamedType","name":{"kind":"Name","value":"String"}}},{"kind":"VariableDefinition","variable":{"kind":"Variable","name":{"kind":"Name","value":"descending"}},"type":{"kind":"NamedType","name":{"kind":"Name","value":"Boolean"}}},{"kind":"VariableDefinition","variable":{"kind":"Variable","name":{"kind":"Name","value":"page"}},"type":{"kind":"NamedType","name":{"kind":"Name","value":"Int"}}},{"kind":"VariableDefinition","variable":{"kind":"Variable","name":{"kind":"Name","value":"pageSize"}},"type":{"kind":"NamedType","name":{"kind":"Name","value":"Int"}}}],"selectionSet":{"kind":"SelectionSet","selections":[{"kind":"Field","name":{"kind":"Name","value":"customers"},"arguments":[{"kind":"Argument","name":{"kind":"Name","value":"search"},"value":{"kind":"Variable","name":{"kind":"Name","value":"search"}}},{"kind":"Argument","name":{"kind":"Name","value":"isActive"},"value":{"kind":"Variable","name":{"kind":"Name","value":"isActive"}}},{"kind":"Argument","name":{"kind":"Name","value":"sortBy"},"value":{"kind":"Variable","name":{"kind":"Name","value":"sortBy"}}},{"kind":"Argument","name":{"kind":"Name","value":"descending"},"value":{"kind":"Variable","name":{"kind":"Name","value":"descending"}}},{"kind":"Argument","name":{"kind":"Name","value":"page"},"value":{"kind":"Variable","name":{"kind":"Name","value":"page"}}},{"kind":"Argument","name":{"kind":"Name","value":"pageSize"},"value":{"kind":"Variable","name":{"kind":"Name","value":"pageSize"}}}],"selectionSet":{"kind":"SelectionSet","selections":[{"kind":"Field","name":{"kind":"Name","value":"page"}},{"kind":"Field","name":{"kind":"Name","value":"pageSize"}},{"kind":"Field","name":{"kind":"Name","value":"totalCount"}},{"kind":"Field","name":{"kind":"Name","value":"items"},"selectionSet":{"kind":"SelectionSet","selections":[{"kind":"Field","name":{"kind":"Name","value":"id"}},{"kind":"Field","name":{"kind":"Name","value":"name"}},{"kind":"Field","name":{"kind":"Name","value":"email"}},{"kind":"Field","name":{"kind":"Name","value":"phone"}},{"kind":"Field","name":{"kind":"Name","value":"isActive"}},{"kind":"Field","name":{"kind":"Name","value":"linkedUserId"}},{"kind":"Field","name":{"kind":"Name","value":"linkedUserEmail"}},{"kind":"Field","name":{"kind":"Name","value":"linkedUserName"}},{"kind":"Field","name":{"kind":"Name","value":"createdAtUtc"}},{"kind":"Field","name":{"kind":"Name","value":"updatedAtUtc"}}]}}]}}]}}]} as unknown as DocumentNode<CustomersQuery, CustomersQueryVariables>;
export const CustomerDocument = {"kind":"Document","definitions":[{"kind":"OperationDefinition","operation":"query","name":{"kind":"Name","value":"Customer"},"variableDefinitions":[{"kind":"VariableDefinition","variable":{"kind":"Variable","name":{"kind":"Name","value":"id"}},"type":{"kind":"NonNullType","type":{"kind":"NamedType","name":{"kind":"Name","value":"UUID"}}}}],"selectionSet":{"kind":"SelectionSet","selections":[{"kind":"Field","name":{"kind":"Name","value":"customer"},"arguments":[{"kind":"Argument","name":{"kind":"Name","value":"id"},"value":{"kind":"Variable","name":{"kind":"Name","value":"id"}}}],"selectionSet":{"kind":"SelectionSet","selections":[{"kind":"Field","name":{"kind":"Name","value":"id"}},{"kind":"Field","name":{"kind":"Name","value":"name"}},{"kind":"Field","name":{"kind":"Name","value":"email"}},{"kind":"Field","name":{"kind":"Name","value":"phone"}},{"kind":"Field","name":{"kind":"Name","value":"isActive"}},{"kind":"Field","name":{"kind":"Name","value":"linkedUserId"}},{"kind":"Field","name":{"kind":"Name","value":"linkedUserEmail"}},{"kind":"Field","name":{"kind":"Name","value":"linkedUserName"}},{"kind":"Field","name":{"kind":"Name","value":"createdAtUtc"}},{"kind":"Field","name":{"kind":"Name","value":"updatedAtUtc"}}]}}]}}]} as unknown as DocumentNode<CustomerQuery, CustomerQueryVariables>;
export const LinkableCustomerUsersDocument = {"kind":"Document","definitions":[{"kind":"OperationDefinition","operation":"query","name":{"kind":"Name","value":"LinkableCustomerUsers"},"variableDefinitions":[{"kind":"VariableDefinition","variable":{"kind":"Variable","name":{"kind":"Name","value":"search"}},"type":{"kind":"NamedType","name":{"kind":"Name","value":"String"}}}],"selectionSet":{"kind":"SelectionSet","selections":[{"kind":"Field","name":{"kind":"Name","value":"linkableCustomerUsers"},"arguments":[{"kind":"Argument","name":{"kind":"Name","value":"search"},"value":{"kind":"Variable","name":{"kind":"Name","value":"search"}}}],"selectionSet":{"kind":"SelectionSet","selections":[{"kind":"Field","name":{"kind":"Name","value":"userId"}},{"kind":"Field","name":{"kind":"Name","value":"email"}},{"kind":"Field","name":{"kind":"Name","value":"displayName"}}]}}]}}]} as unknown as DocumentNode<LinkableCustomerUsersQuery, LinkableCustomerUsersQueryVariables>;
export const TeamMembersDocument = {"kind":"Document","definitions":[{"kind":"OperationDefinition","operation":"query","name":{"kind":"Name","value":"TeamMembers"},"variableDefinitions":[{"kind":"VariableDefinition","variable":{"kind":"Variable","name":{"kind":"Name","value":"page"}},"type":{"kind":"NamedType","name":{"kind":"Name","value":"Int"}}},{"kind":"VariableDefinition","variable":{"kind":"Variable","name":{"kind":"Name","value":"pageSize"}},"type":{"kind":"NamedType","name":{"kind":"Name","value":"Int"}}}],"selectionSet":{"kind":"SelectionSet","selections":[{"kind":"Field","name":{"kind":"Name","value":"teamMembers"},"arguments":[{"kind":"Argument","name":{"kind":"Name","value":"page"},"value":{"kind":"Variable","name":{"kind":"Name","value":"page"}}},{"kind":"Argument","name":{"kind":"Name","value":"pageSize"},"value":{"kind":"Variable","name":{"kind":"Name","value":"pageSize"}}}],"selectionSet":{"kind":"SelectionSet","selections":[{"kind":"Field","name":{"kind":"Name","value":"page"}},{"kind":"Field","name":{"kind":"Name","value":"pageSize"}},{"kind":"Field","name":{"kind":"Name","value":"totalCount"}},{"kind":"Field","name":{"kind":"Name","value":"items"},"selectionSet":{"kind":"SelectionSet","selections":[{"kind":"Field","name":{"kind":"Name","value":"id"}},{"kind":"Field","name":{"kind":"Name","value":"userId"}},{"kind":"Field","name":{"kind":"Name","value":"email"}},{"kind":"Field","name":{"kind":"Name","value":"displayName"}},{"kind":"Field","name":{"kind":"Name","value":"role"}},{"kind":"Field","name":{"kind":"Name","value":"isActive"}},{"kind":"Field","name":{"kind":"Name","value":"createdAtUtc"}}]}}]}}]}}]} as unknown as DocumentNode<TeamMembersQuery, TeamMembersQueryVariables>;
export const TicketSlaHistoryDocument = {"kind":"Document","definitions":[{"kind":"OperationDefinition","operation":"query","name":{"kind":"Name","value":"TicketSlaHistory"},"variableDefinitions":[{"kind":"VariableDefinition","variable":{"kind":"Variable","name":{"kind":"Name","value":"ticketId"}},"type":{"kind":"NonNullType","type":{"kind":"NamedType","name":{"kind":"Name","value":"UUID"}}}}],"selectionSet":{"kind":"SelectionSet","selections":[{"kind":"Field","name":{"kind":"Name","value":"ticketSla"},"arguments":[{"kind":"Argument","name":{"kind":"Name","value":"ticketId"},"value":{"kind":"Variable","name":{"kind":"Name","value":"ticketId"}}}],"selectionSet":{"kind":"SelectionSet","selections":[{"kind":"Field","name":{"kind":"Name","value":"cycles"},"selectionSet":{"kind":"SelectionSet","selections":[{"kind":"Field","name":{"kind":"Name","value":"id"}},{"kind":"Field","name":{"kind":"Name","value":"cycleNumber"}},{"kind":"Field","name":{"kind":"Name","value":"reason"}},{"kind":"Field","name":{"kind":"Name","value":"startedAtUtc"}},{"kind":"Field","name":{"kind":"Name","value":"responseDueAtUtc"}},{"kind":"Field","name":{"kind":"Name","value":"resolutionDueAtUtc"}},{"kind":"Field","name":{"kind":"Name","value":"firstResponseAtUtc"}},{"kind":"Field","name":{"kind":"Name","value":"resolvedAtUtc"}},{"kind":"Field","name":{"kind":"Name","value":"responseState"}},{"kind":"Field","name":{"kind":"Name","value":"resolutionState"}}]}}]}}]}}]} as unknown as DocumentNode<TicketSlaHistoryQuery, TicketSlaHistoryQueryVariables>;
export const TicketCreationEligibilityDocument = {"kind":"Document","definitions":[{"kind":"OperationDefinition","operation":"query","name":{"kind":"Name","value":"TicketCreationEligibility"},"selectionSet":{"kind":"SelectionSet","selections":[{"kind":"Field","name":{"kind":"Name","value":"ticketCreationEligibility"},"selectionSet":{"kind":"SelectionSet","selections":[{"kind":"Field","name":{"kind":"Name","value":"canCreate"}},{"kind":"Field","name":{"kind":"Name","value":"reason"}}]}}]}}]} as unknown as DocumentNode<TicketCreationEligibilityQuery, TicketCreationEligibilityQueryVariables>;
export const KanbanBoardDocument = {"kind":"Document","definitions":[{"kind":"OperationDefinition","operation":"query","name":{"kind":"Name","value":"KanbanBoard"},"variableDefinitions":[{"kind":"VariableDefinition","variable":{"kind":"Variable","name":{"kind":"Name","value":"filter"}},"type":{"kind":"NamedType","name":{"kind":"Name","value":"KanbanFilterInput"}}},{"kind":"VariableDefinition","variable":{"kind":"Variable","name":{"kind":"Name","value":"perColumn"}},"type":{"kind":"NamedType","name":{"kind":"Name","value":"Int"}}}],"selectionSet":{"kind":"SelectionSet","selections":[{"kind":"Field","name":{"kind":"Name","value":"kanbanBoard"},"arguments":[{"kind":"Argument","name":{"kind":"Name","value":"filter"},"value":{"kind":"Variable","name":{"kind":"Name","value":"filter"}}},{"kind":"Argument","name":{"kind":"Name","value":"perColumn"},"value":{"kind":"Variable","name":{"kind":"Name","value":"perColumn"}}}],"selectionSet":{"kind":"SelectionSet","selections":[{"kind":"Field","name":{"kind":"Name","value":"status"}},{"kind":"Field","name":{"kind":"Name","value":"total"}},{"kind":"Field","name":{"kind":"Name","value":"tickets"},"selectionSet":{"kind":"SelectionSet","selections":[{"kind":"FragmentSpread","name":{"kind":"Name","value":"BoardTicket"}}]}}]}}]}},{"kind":"FragmentDefinition","name":{"kind":"Name","value":"BoardTicket"},"typeCondition":{"kind":"NamedType","name":{"kind":"Name","value":"TicketSummaryDto"}},"selectionSet":{"kind":"SelectionSet","selections":[{"kind":"Field","name":{"kind":"Name","value":"id"}},{"kind":"Field","name":{"kind":"Name","value":"displayNumber"}},{"kind":"Field","name":{"kind":"Name","value":"title"}},{"kind":"Field","name":{"kind":"Name","value":"priority"}},{"kind":"Field","name":{"kind":"Name","value":"status"}},{"kind":"Field","name":{"kind":"Name","value":"assignedToMe"}},{"kind":"Field","name":{"kind":"Name","value":"categoryName"}},{"kind":"Field","name":{"kind":"Name","value":"customerName"}},{"kind":"Field","name":{"kind":"Name","value":"updatedAtUtc"}},{"kind":"Field","name":{"kind":"Name","value":"version"}},{"kind":"Field","name":{"kind":"Name","value":"assigneeCount"}},{"kind":"Field","name":{"kind":"Name","value":"resolutionDueAtUtc"}},{"kind":"Field","name":{"kind":"Name","value":"isOverdue"}},{"kind":"Field","name":{"kind":"Name","value":"assignees"},"selectionSet":{"kind":"SelectionSet","selections":[{"kind":"Field","name":{"kind":"Name","value":"membershipId"}},{"kind":"Field","name":{"kind":"Name","value":"userId"}},{"kind":"Field","name":{"kind":"Name","value":"displayName"}},{"kind":"Field","name":{"kind":"Name","value":"isAgentActive"}}]}}]}}]} as unknown as DocumentNode<KanbanBoardQuery, KanbanBoardQueryVariables>;
export const TicketTaskProgressDocument = {"kind":"Document","definitions":[{"kind":"OperationDefinition","operation":"query","name":{"kind":"Name","value":"TicketTaskProgress"},"variableDefinitions":[{"kind":"VariableDefinition","variable":{"kind":"Variable","name":{"kind":"Name","value":"ticketIds"}},"type":{"kind":"NonNullType","type":{"kind":"ListType","type":{"kind":"NonNullType","type":{"kind":"NamedType","name":{"kind":"Name","value":"UUID"}}}}}}],"selectionSet":{"kind":"SelectionSet","selections":[{"kind":"Field","name":{"kind":"Name","value":"ticketTaskProgress"},"arguments":[{"kind":"Argument","name":{"kind":"Name","value":"ticketIds"},"value":{"kind":"Variable","name":{"kind":"Name","value":"ticketIds"}}}],"selectionSet":{"kind":"SelectionSet","selections":[{"kind":"Field","name":{"kind":"Name","value":"ticketId"}},{"kind":"Field","name":{"kind":"Name","value":"totalTasks"}},{"kind":"Field","name":{"kind":"Name","value":"completedTasks"}}]}}]}}]} as unknown as DocumentNode<TicketTaskProgressQuery, TicketTaskProgressQueryVariables>;
export const ReportDocument = {"kind":"Document","definitions":[{"kind":"OperationDefinition","operation":"query","name":{"kind":"Name","value":"Report"},"variableDefinitions":[{"kind":"VariableDefinition","variable":{"kind":"Variable","name":{"kind":"Name","value":"filter"}},"type":{"kind":"NamedType","name":{"kind":"Name","value":"AnalyticsFilterInput"}}}],"selectionSet":{"kind":"SelectionSet","selections":[{"kind":"Field","name":{"kind":"Name","value":"report"},"arguments":[{"kind":"Argument","name":{"kind":"Name","value":"filter"},"value":{"kind":"Variable","name":{"kind":"Name","value":"filter"}}}],"selectionSet":{"kind":"SelectionSet","selections":[{"kind":"Field","name":{"kind":"Name","value":"generatedAtUtc"}},{"kind":"Field","name":{"kind":"Name","value":"timeZone"}},{"kind":"Field","name":{"kind":"Name","value":"fromUtc"}},{"kind":"Field","name":{"kind":"Name","value":"toUtc"}},{"kind":"Field","name":{"kind":"Name","value":"grouping"}},{"kind":"Field","name":{"kind":"Name","value":"ticketsCreated"}},{"kind":"Field","name":{"kind":"Name","value":"trend"},"selectionSet":{"kind":"SelectionSet","selections":[{"kind":"Field","name":{"kind":"Name","value":"periodStartUtc"}},{"kind":"Field","name":{"kind":"Name","value":"created"}},{"kind":"Field","name":{"kind":"Name","value":"resolved"}},{"kind":"Field","name":{"kind":"Name","value":"avgResolutionMinutes"}}]}},{"kind":"Field","name":{"kind":"Name","value":"byStatus"},"selectionSet":{"kind":"SelectionSet","selections":[{"kind":"Field","name":{"kind":"Name","value":"key"}},{"kind":"Field","name":{"kind":"Name","value":"count"}}]}},{"kind":"Field","name":{"kind":"Name","value":"byPriority"},"selectionSet":{"kind":"SelectionSet","selections":[{"kind":"Field","name":{"kind":"Name","value":"key"}},{"kind":"Field","name":{"kind":"Name","value":"count"}}]}},{"kind":"Field","name":{"kind":"Name","value":"resolution"},"selectionSet":{"kind":"SelectionSet","selections":[{"kind":"Field","name":{"kind":"Name","value":"completedCycles"}},{"kind":"Field","name":{"kind":"Name","value":"avgMinutes"}},{"kind":"Field","name":{"kind":"Name","value":"medianMinutes"}}]}},{"kind":"Field","name":{"kind":"Name","value":"sla"},"selectionSet":{"kind":"SelectionSet","selections":[{"kind":"Field","name":{"kind":"Name","value":"eligibleCycles"}},{"kind":"Field","name":{"kind":"Name","value":"met"}},{"kind":"Field","name":{"kind":"Name","value":"breached"}},{"kind":"Field","name":{"kind":"Name","value":"compliancePercent"}}]}},{"kind":"Field","name":{"kind":"Name","value":"openOverdueTickets"}},{"kind":"Field","name":{"kind":"Name","value":"reopen"},"selectionSet":{"kind":"SelectionSet","selections":[{"kind":"Field","name":{"kind":"Name","value":"resolvedCycles"}},{"kind":"Field","name":{"kind":"Name","value":"reopenedCycles"}},{"kind":"Field","name":{"kind":"Name","value":"reopenRatePercent"}},{"kind":"Field","name":{"kind":"Name","value":"reopenEventsInPeriod"}}]}},{"kind":"Field","name":{"kind":"Name","value":"agents"},"selectionSet":{"kind":"SelectionSet","selections":[{"kind":"Field","name":{"kind":"Name","value":"membershipId"}},{"kind":"Field","name":{"kind":"Name","value":"displayName"}},{"kind":"Field","name":{"kind":"Name","value":"currentWorkload"}},{"kind":"Field","name":{"kind":"Name","value":"resolvedInPeriod"}}]}},{"kind":"Field","name":{"kind":"Name","value":"categories"},"selectionSet":{"kind":"SelectionSet","selections":[{"kind":"Field","name":{"kind":"Name","value":"categoryId"}},{"kind":"Field","name":{"kind":"Name","value":"name"}},{"kind":"Field","name":{"kind":"Name","value":"count"}},{"kind":"Field","name":{"kind":"Name","value":"sharePercent"}}]}},{"kind":"Field","name":{"kind":"Name","value":"customers"},"selectionSet":{"kind":"SelectionSet","selections":[{"kind":"Field","name":{"kind":"Name","value":"customerId"}},{"kind":"Field","name":{"kind":"Name","value":"name"}},{"kind":"Field","name":{"kind":"Name","value":"tickets"}},{"kind":"Field","name":{"kind":"Name","value":"open"}},{"kind":"Field","name":{"kind":"Name","value":"resolved"}},{"kind":"Field","name":{"kind":"Name","value":"overdueOpen"}}]}}]}}]}}]} as unknown as DocumentNode<ReportQuery, ReportQueryVariables>;
export const TeamDirectoryDocument = {"kind":"Document","definitions":[{"kind":"OperationDefinition","operation":"query","name":{"kind":"Name","value":"TeamDirectory"},"variableDefinitions":[{"kind":"VariableDefinition","variable":{"kind":"Variable","name":{"kind":"Name","value":"search"}},"type":{"kind":"NamedType","name":{"kind":"Name","value":"String"}}},{"kind":"VariableDefinition","variable":{"kind":"Variable","name":{"kind":"Name","value":"role"}},"type":{"kind":"NamedType","name":{"kind":"Name","value":"TenantRole"}}},{"kind":"VariableDefinition","variable":{"kind":"Variable","name":{"kind":"Name","value":"isActive"}},"type":{"kind":"NamedType","name":{"kind":"Name","value":"Boolean"}}},{"kind":"VariableDefinition","variable":{"kind":"Variable","name":{"kind":"Name","value":"skillId"}},"type":{"kind":"NamedType","name":{"kind":"Name","value":"UUID"}}},{"kind":"VariableDefinition","variable":{"kind":"Variable","name":{"kind":"Name","value":"sortBy"}},"type":{"kind":"NamedType","name":{"kind":"Name","value":"String"}}},{"kind":"VariableDefinition","variable":{"kind":"Variable","name":{"kind":"Name","value":"descending"}},"type":{"kind":"NonNullType","type":{"kind":"NamedType","name":{"kind":"Name","value":"Boolean"}}}},{"kind":"VariableDefinition","variable":{"kind":"Variable","name":{"kind":"Name","value":"page"}},"type":{"kind":"NonNullType","type":{"kind":"NamedType","name":{"kind":"Name","value":"Int"}}}},{"kind":"VariableDefinition","variable":{"kind":"Variable","name":{"kind":"Name","value":"pageSize"}},"type":{"kind":"NonNullType","type":{"kind":"NamedType","name":{"kind":"Name","value":"Int"}}}}],"selectionSet":{"kind":"SelectionSet","selections":[{"kind":"Field","name":{"kind":"Name","value":"teamDirectory"},"arguments":[{"kind":"Argument","name":{"kind":"Name","value":"search"},"value":{"kind":"Variable","name":{"kind":"Name","value":"search"}}},{"kind":"Argument","name":{"kind":"Name","value":"role"},"value":{"kind":"Variable","name":{"kind":"Name","value":"role"}}},{"kind":"Argument","name":{"kind":"Name","value":"isActive"},"value":{"kind":"Variable","name":{"kind":"Name","value":"isActive"}}},{"kind":"Argument","name":{"kind":"Name","value":"skillId"},"value":{"kind":"Variable","name":{"kind":"Name","value":"skillId"}}},{"kind":"Argument","name":{"kind":"Name","value":"sortBy"},"value":{"kind":"Variable","name":{"kind":"Name","value":"sortBy"}}},{"kind":"Argument","name":{"kind":"Name","value":"descending"},"value":{"kind":"Variable","name":{"kind":"Name","value":"descending"}}},{"kind":"Argument","name":{"kind":"Name","value":"page"},"value":{"kind":"Variable","name":{"kind":"Name","value":"page"}}},{"kind":"Argument","name":{"kind":"Name","value":"pageSize"},"value":{"kind":"Variable","name":{"kind":"Name","value":"pageSize"}}}],"selectionSet":{"kind":"SelectionSet","selections":[{"kind":"Field","name":{"kind":"Name","value":"page"}},{"kind":"Field","name":{"kind":"Name","value":"pageSize"}},{"kind":"Field","name":{"kind":"Name","value":"totalCount"}},{"kind":"Field","name":{"kind":"Name","value":"items"},"selectionSet":{"kind":"SelectionSet","selections":[{"kind":"Field","name":{"kind":"Name","value":"membershipId"}},{"kind":"Field","name":{"kind":"Name","value":"userId"}},{"kind":"Field","name":{"kind":"Name","value":"displayName"}},{"kind":"Field","name":{"kind":"Name","value":"email"}},{"kind":"Field","name":{"kind":"Name","value":"role"}},{"kind":"Field","name":{"kind":"Name","value":"isActive"}},{"kind":"Field","name":{"kind":"Name","value":"jobTitle"}},{"kind":"Field","name":{"kind":"Name","value":"department"}},{"kind":"Field","name":{"kind":"Name","value":"openAssignmentCount"}},{"kind":"Field","name":{"kind":"Name","value":"joinedAtUtc"}},{"kind":"Field","name":{"kind":"Name","value":"skills"},"selectionSet":{"kind":"SelectionSet","selections":[{"kind":"Field","name":{"kind":"Name","value":"id"}},{"kind":"Field","name":{"kind":"Name","value":"name"}},{"kind":"Field","name":{"kind":"Name","value":"isActive"}}]}}]}}]}}]}}]} as unknown as DocumentNode<TeamDirectoryQuery, TeamDirectoryQueryVariables>;
export const TeamMemberDocument = {"kind":"Document","definitions":[{"kind":"OperationDefinition","operation":"query","name":{"kind":"Name","value":"TeamMember"},"variableDefinitions":[{"kind":"VariableDefinition","variable":{"kind":"Variable","name":{"kind":"Name","value":"membershipId"}},"type":{"kind":"NonNullType","type":{"kind":"NamedType","name":{"kind":"Name","value":"UUID"}}}}],"selectionSet":{"kind":"SelectionSet","selections":[{"kind":"Field","name":{"kind":"Name","value":"teamMember"},"arguments":[{"kind":"Argument","name":{"kind":"Name","value":"membershipId"},"value":{"kind":"Variable","name":{"kind":"Name","value":"membershipId"}}}],"selectionSet":{"kind":"SelectionSet","selections":[{"kind":"Field","name":{"kind":"Name","value":"phone"}},{"kind":"Field","name":{"kind":"Name","value":"profileUpdatedAtUtc"}},{"kind":"Field","name":{"kind":"Name","value":"canEditProfileAndSkills"}},{"kind":"Field","name":{"kind":"Name","value":"canChangeRoleAndStatus"}},{"kind":"Field","name":{"kind":"Name","value":"member"},"selectionSet":{"kind":"SelectionSet","selections":[{"kind":"Field","name":{"kind":"Name","value":"membershipId"}},{"kind":"Field","name":{"kind":"Name","value":"userId"}},{"kind":"Field","name":{"kind":"Name","value":"displayName"}},{"kind":"Field","name":{"kind":"Name","value":"email"}},{"kind":"Field","name":{"kind":"Name","value":"role"}},{"kind":"Field","name":{"kind":"Name","value":"isActive"}},{"kind":"Field","name":{"kind":"Name","value":"jobTitle"}},{"kind":"Field","name":{"kind":"Name","value":"department"}},{"kind":"Field","name":{"kind":"Name","value":"openAssignmentCount"}},{"kind":"Field","name":{"kind":"Name","value":"joinedAtUtc"}},{"kind":"Field","name":{"kind":"Name","value":"skills"},"selectionSet":{"kind":"SelectionSet","selections":[{"kind":"Field","name":{"kind":"Name","value":"id"}},{"kind":"Field","name":{"kind":"Name","value":"name"}},{"kind":"Field","name":{"kind":"Name","value":"isActive"}}]}}]}},{"kind":"Field","name":{"kind":"Name","value":"stats"},"selectionSet":{"kind":"SelectionSet","selections":[{"kind":"Field","name":{"kind":"Name","value":"openAssignmentCount"}},{"kind":"Field","name":{"kind":"Name","value":"openTaskCount"}},{"kind":"Field","name":{"kind":"Name","value":"resolveActions"}},{"kind":"Field","name":{"kind":"Name","value":"resolvedTicketsDistinct"}}]}},{"kind":"Field","name":{"kind":"Name","value":"assignedTickets"},"selectionSet":{"kind":"SelectionSet","selections":[{"kind":"Field","name":{"kind":"Name","value":"ticketId"}},{"kind":"Field","name":{"kind":"Name","value":"displayNumber"}},{"kind":"Field","name":{"kind":"Name","value":"title"}},{"kind":"Field","name":{"kind":"Name","value":"status"}},{"kind":"Field","name":{"kind":"Name","value":"priority"}}]}},{"kind":"Field","name":{"kind":"Name","value":"recentActivity"},"selectionSet":{"kind":"SelectionSet","selections":[{"kind":"Field","name":{"kind":"Name","value":"ticketId"}},{"kind":"Field","name":{"kind":"Name","value":"displayNumber"}},{"kind":"Field","name":{"kind":"Name","value":"type"}},{"kind":"Field","name":{"kind":"Name","value":"occurredAtUtc"}}]}},{"kind":"Field","name":{"kind":"Name","value":"adminHistory"},"selectionSet":{"kind":"SelectionSet","selections":[{"kind":"Field","name":{"kind":"Name","value":"action"}},{"kind":"Field","name":{"kind":"Name","value":"actorDisplayName"}},{"kind":"Field","name":{"kind":"Name","value":"detail"}},{"kind":"Field","name":{"kind":"Name","value":"occurredAtUtc"}}]}}]}}]}}]} as unknown as DocumentNode<TeamMemberQuery, TeamMemberQueryVariables>;
export const SkillsDocument = {"kind":"Document","definitions":[{"kind":"OperationDefinition","operation":"query","name":{"kind":"Name","value":"Skills"},"variableDefinitions":[{"kind":"VariableDefinition","variable":{"kind":"Variable","name":{"kind":"Name","value":"includeInactive"}},"type":{"kind":"NonNullType","type":{"kind":"NamedType","name":{"kind":"Name","value":"Boolean"}}}}],"selectionSet":{"kind":"SelectionSet","selections":[{"kind":"Field","name":{"kind":"Name","value":"skills"},"arguments":[{"kind":"Argument","name":{"kind":"Name","value":"includeInactive"},"value":{"kind":"Variable","name":{"kind":"Name","value":"includeInactive"}}}],"selectionSet":{"kind":"SelectionSet","selections":[{"kind":"Field","name":{"kind":"Name","value":"id"}},{"kind":"Field","name":{"kind":"Name","value":"name"}},{"kind":"Field","name":{"kind":"Name","value":"isActive"}}]}}]}}]} as unknown as DocumentNode<SkillsQuery, SkillsQueryVariables>;