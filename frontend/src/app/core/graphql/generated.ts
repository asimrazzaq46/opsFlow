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

export type KanbanFilterInput = {
  assignedMembershipId?: string | null | undefined;
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

export type DashboardQueryVariables = Exact<{
  filter?: AnalyticsFilterInput | null | undefined;
}>;


export type DashboardQuery = { dashboard: { generatedAtUtc: string, timeZone: string, fromUtc: string, toUtc: string, grouping: string, createdInPeriod: number, resolvedInPeriod: number, avgFirstResponseMinutes: number | null, firstResponseSample: number, current: { openTickets: number, unassignedTickets: number, overdueTickets: number, resolvedToday: number }, sla: { eligibleCycles: number, met: number, breached: number, compliancePercent: number | null } | null, trend: Array<{ periodStartUtc: string, created: number, resolved: number, avgResolutionMinutes: number | null }>, openByStatus: Array<{ key: string, count: number }>, byPriority: Array<{ key: string, count: number }>, workload: Array<{ membershipId: string, displayName: string, activeTickets: number }> | null } };

export type CustomersQueryVariables = Exact<{
  search?: string | null | undefined;
  isActive?: boolean | null | undefined;
  page?: number | null | undefined;
  pageSize?: number | null | undefined;
}>;


export type CustomersQuery = { customers: { page: number, pageSize: number, totalCount: number, items: Array<{ id: string, name: string, email: string, phone: string | null, isActive: boolean, createdAtUtc: string }> } };

export type TeamMembersQueryVariables = Exact<{
  page?: number | null | undefined;
  pageSize?: number | null | undefined;
}>;


export type TeamMembersQuery = { teamMembers: { page: number, pageSize: number, totalCount: number, items: Array<{ id: string, userId: string, email: string, displayName: string, role: TenantRole, isActive: boolean, createdAtUtc: string }> } };

export type TicketSlaHistoryQueryVariables = Exact<{
  ticketId: string;
}>;


export type TicketSlaHistoryQuery = { ticketSla: { cycles: Array<{ id: string, cycleNumber: number, reason: SlaCycleReason, startedAtUtc: string, responseDueAtUtc: string | null, resolutionDueAtUtc: string, firstResponseAtUtc: string | null, resolvedAtUtc: string | null, responseState: SlaState, resolutionState: SlaState }> } };

export type BoardTicketFragment = { id: string, displayNumber: string, title: string, priority: TicketPriority, status: TicketStatus, categoryName: string, customerName: string | null, updatedAtUtc: string, version: number, assigneeCount: number | null, resolutionDueAtUtc: string | null, isOverdue: boolean | null };

export type KanbanBoardQueryVariables = Exact<{
  filter?: KanbanFilterInput | null | undefined;
  perColumn?: number | null | undefined;
}>;


export type KanbanBoardQuery = { kanbanBoard: Array<{ status: TicketStatus, total: number, tickets: Array<{ id: string, displayNumber: string, title: string, priority: TicketPriority, status: TicketStatus, categoryName: string, customerName: string | null, updatedAtUtc: string, version: number, assigneeCount: number | null, resolutionDueAtUtc: string | null, isOverdue: boolean | null }> }> };

export type ReportQueryVariables = Exact<{
  filter?: AnalyticsFilterInput | null | undefined;
}>;


export type ReportQuery = { report: { generatedAtUtc: string, timeZone: string, fromUtc: string, toUtc: string, grouping: string, ticketsCreated: number, openOverdueTickets: number, trend: Array<{ periodStartUtc: string, created: number, resolved: number, avgResolutionMinutes: number | null }>, byStatus: Array<{ key: string, count: number }>, byPriority: Array<{ key: string, count: number }>, resolution: { completedCycles: number, avgMinutes: number | null, medianMinutes: number | null }, sla: { eligibleCycles: number, met: number, breached: number, compliancePercent: number | null }, reopen: { resolvedCycles: number, reopenedCycles: number, reopenRatePercent: number | null, reopenEventsInPeriod: number }, agents: Array<{ membershipId: string, displayName: string, currentWorkload: number, resolvedInPeriod: number }>, categories: Array<{ categoryId: string, name: string, count: number, sharePercent: number }>, customers: Array<{ customerId: string, name: string, tickets: number, open: number, resolved: number, overdueOpen: number }> } };

export const BoardTicketFragmentDoc = {"kind":"Document","definitions":[{"kind":"FragmentDefinition","name":{"kind":"Name","value":"BoardTicket"},"typeCondition":{"kind":"NamedType","name":{"kind":"Name","value":"TicketSummaryDto"}},"selectionSet":{"kind":"SelectionSet","selections":[{"kind":"Field","name":{"kind":"Name","value":"id"}},{"kind":"Field","name":{"kind":"Name","value":"displayNumber"}},{"kind":"Field","name":{"kind":"Name","value":"title"}},{"kind":"Field","name":{"kind":"Name","value":"priority"}},{"kind":"Field","name":{"kind":"Name","value":"status"}},{"kind":"Field","name":{"kind":"Name","value":"categoryName"}},{"kind":"Field","name":{"kind":"Name","value":"customerName"}},{"kind":"Field","name":{"kind":"Name","value":"updatedAtUtc"}},{"kind":"Field","name":{"kind":"Name","value":"version"}},{"kind":"Field","name":{"kind":"Name","value":"assigneeCount"}},{"kind":"Field","name":{"kind":"Name","value":"resolutionDueAtUtc"}},{"kind":"Field","name":{"kind":"Name","value":"isOverdue"}}]}}]} as unknown as DocumentNode<BoardTicketFragment, unknown>;
export const DashboardDocument = {"kind":"Document","definitions":[{"kind":"OperationDefinition","operation":"query","name":{"kind":"Name","value":"Dashboard"},"variableDefinitions":[{"kind":"VariableDefinition","variable":{"kind":"Variable","name":{"kind":"Name","value":"filter"}},"type":{"kind":"NamedType","name":{"kind":"Name","value":"AnalyticsFilterInput"}}}],"selectionSet":{"kind":"SelectionSet","selections":[{"kind":"Field","name":{"kind":"Name","value":"dashboard"},"arguments":[{"kind":"Argument","name":{"kind":"Name","value":"filter"},"value":{"kind":"Variable","name":{"kind":"Name","value":"filter"}}}],"selectionSet":{"kind":"SelectionSet","selections":[{"kind":"Field","name":{"kind":"Name","value":"generatedAtUtc"}},{"kind":"Field","name":{"kind":"Name","value":"timeZone"}},{"kind":"Field","name":{"kind":"Name","value":"fromUtc"}},{"kind":"Field","name":{"kind":"Name","value":"toUtc"}},{"kind":"Field","name":{"kind":"Name","value":"grouping"}},{"kind":"Field","name":{"kind":"Name","value":"current"},"selectionSet":{"kind":"SelectionSet","selections":[{"kind":"Field","name":{"kind":"Name","value":"openTickets"}},{"kind":"Field","name":{"kind":"Name","value":"unassignedTickets"}},{"kind":"Field","name":{"kind":"Name","value":"overdueTickets"}},{"kind":"Field","name":{"kind":"Name","value":"resolvedToday"}}]}},{"kind":"Field","name":{"kind":"Name","value":"createdInPeriod"}},{"kind":"Field","name":{"kind":"Name","value":"resolvedInPeriod"}},{"kind":"Field","name":{"kind":"Name","value":"avgFirstResponseMinutes"}},{"kind":"Field","name":{"kind":"Name","value":"firstResponseSample"}},{"kind":"Field","name":{"kind":"Name","value":"sla"},"selectionSet":{"kind":"SelectionSet","selections":[{"kind":"Field","name":{"kind":"Name","value":"eligibleCycles"}},{"kind":"Field","name":{"kind":"Name","value":"met"}},{"kind":"Field","name":{"kind":"Name","value":"breached"}},{"kind":"Field","name":{"kind":"Name","value":"compliancePercent"}}]}},{"kind":"Field","name":{"kind":"Name","value":"trend"},"selectionSet":{"kind":"SelectionSet","selections":[{"kind":"Field","name":{"kind":"Name","value":"periodStartUtc"}},{"kind":"Field","name":{"kind":"Name","value":"created"}},{"kind":"Field","name":{"kind":"Name","value":"resolved"}},{"kind":"Field","name":{"kind":"Name","value":"avgResolutionMinutes"}}]}},{"kind":"Field","name":{"kind":"Name","value":"openByStatus"},"selectionSet":{"kind":"SelectionSet","selections":[{"kind":"Field","name":{"kind":"Name","value":"key"}},{"kind":"Field","name":{"kind":"Name","value":"count"}}]}},{"kind":"Field","name":{"kind":"Name","value":"byPriority"},"selectionSet":{"kind":"SelectionSet","selections":[{"kind":"Field","name":{"kind":"Name","value":"key"}},{"kind":"Field","name":{"kind":"Name","value":"count"}}]}},{"kind":"Field","name":{"kind":"Name","value":"workload"},"selectionSet":{"kind":"SelectionSet","selections":[{"kind":"Field","name":{"kind":"Name","value":"membershipId"}},{"kind":"Field","name":{"kind":"Name","value":"displayName"}},{"kind":"Field","name":{"kind":"Name","value":"activeTickets"}}]}}]}}]}}]} as unknown as DocumentNode<DashboardQuery, DashboardQueryVariables>;
export const CustomersDocument = {"kind":"Document","definitions":[{"kind":"OperationDefinition","operation":"query","name":{"kind":"Name","value":"Customers"},"variableDefinitions":[{"kind":"VariableDefinition","variable":{"kind":"Variable","name":{"kind":"Name","value":"search"}},"type":{"kind":"NamedType","name":{"kind":"Name","value":"String"}}},{"kind":"VariableDefinition","variable":{"kind":"Variable","name":{"kind":"Name","value":"isActive"}},"type":{"kind":"NamedType","name":{"kind":"Name","value":"Boolean"}}},{"kind":"VariableDefinition","variable":{"kind":"Variable","name":{"kind":"Name","value":"page"}},"type":{"kind":"NamedType","name":{"kind":"Name","value":"Int"}}},{"kind":"VariableDefinition","variable":{"kind":"Variable","name":{"kind":"Name","value":"pageSize"}},"type":{"kind":"NamedType","name":{"kind":"Name","value":"Int"}}}],"selectionSet":{"kind":"SelectionSet","selections":[{"kind":"Field","name":{"kind":"Name","value":"customers"},"arguments":[{"kind":"Argument","name":{"kind":"Name","value":"search"},"value":{"kind":"Variable","name":{"kind":"Name","value":"search"}}},{"kind":"Argument","name":{"kind":"Name","value":"isActive"},"value":{"kind":"Variable","name":{"kind":"Name","value":"isActive"}}},{"kind":"Argument","name":{"kind":"Name","value":"page"},"value":{"kind":"Variable","name":{"kind":"Name","value":"page"}}},{"kind":"Argument","name":{"kind":"Name","value":"pageSize"},"value":{"kind":"Variable","name":{"kind":"Name","value":"pageSize"}}}],"selectionSet":{"kind":"SelectionSet","selections":[{"kind":"Field","name":{"kind":"Name","value":"page"}},{"kind":"Field","name":{"kind":"Name","value":"pageSize"}},{"kind":"Field","name":{"kind":"Name","value":"totalCount"}},{"kind":"Field","name":{"kind":"Name","value":"items"},"selectionSet":{"kind":"SelectionSet","selections":[{"kind":"Field","name":{"kind":"Name","value":"id"}},{"kind":"Field","name":{"kind":"Name","value":"name"}},{"kind":"Field","name":{"kind":"Name","value":"email"}},{"kind":"Field","name":{"kind":"Name","value":"phone"}},{"kind":"Field","name":{"kind":"Name","value":"isActive"}},{"kind":"Field","name":{"kind":"Name","value":"createdAtUtc"}}]}}]}}]}}]} as unknown as DocumentNode<CustomersQuery, CustomersQueryVariables>;
export const TeamMembersDocument = {"kind":"Document","definitions":[{"kind":"OperationDefinition","operation":"query","name":{"kind":"Name","value":"TeamMembers"},"variableDefinitions":[{"kind":"VariableDefinition","variable":{"kind":"Variable","name":{"kind":"Name","value":"page"}},"type":{"kind":"NamedType","name":{"kind":"Name","value":"Int"}}},{"kind":"VariableDefinition","variable":{"kind":"Variable","name":{"kind":"Name","value":"pageSize"}},"type":{"kind":"NamedType","name":{"kind":"Name","value":"Int"}}}],"selectionSet":{"kind":"SelectionSet","selections":[{"kind":"Field","name":{"kind":"Name","value":"teamMembers"},"arguments":[{"kind":"Argument","name":{"kind":"Name","value":"page"},"value":{"kind":"Variable","name":{"kind":"Name","value":"page"}}},{"kind":"Argument","name":{"kind":"Name","value":"pageSize"},"value":{"kind":"Variable","name":{"kind":"Name","value":"pageSize"}}}],"selectionSet":{"kind":"SelectionSet","selections":[{"kind":"Field","name":{"kind":"Name","value":"page"}},{"kind":"Field","name":{"kind":"Name","value":"pageSize"}},{"kind":"Field","name":{"kind":"Name","value":"totalCount"}},{"kind":"Field","name":{"kind":"Name","value":"items"},"selectionSet":{"kind":"SelectionSet","selections":[{"kind":"Field","name":{"kind":"Name","value":"id"}},{"kind":"Field","name":{"kind":"Name","value":"userId"}},{"kind":"Field","name":{"kind":"Name","value":"email"}},{"kind":"Field","name":{"kind":"Name","value":"displayName"}},{"kind":"Field","name":{"kind":"Name","value":"role"}},{"kind":"Field","name":{"kind":"Name","value":"isActive"}},{"kind":"Field","name":{"kind":"Name","value":"createdAtUtc"}}]}}]}}]}}]} as unknown as DocumentNode<TeamMembersQuery, TeamMembersQueryVariables>;
export const TicketSlaHistoryDocument = {"kind":"Document","definitions":[{"kind":"OperationDefinition","operation":"query","name":{"kind":"Name","value":"TicketSlaHistory"},"variableDefinitions":[{"kind":"VariableDefinition","variable":{"kind":"Variable","name":{"kind":"Name","value":"ticketId"}},"type":{"kind":"NonNullType","type":{"kind":"NamedType","name":{"kind":"Name","value":"UUID"}}}}],"selectionSet":{"kind":"SelectionSet","selections":[{"kind":"Field","name":{"kind":"Name","value":"ticketSla"},"arguments":[{"kind":"Argument","name":{"kind":"Name","value":"ticketId"},"value":{"kind":"Variable","name":{"kind":"Name","value":"ticketId"}}}],"selectionSet":{"kind":"SelectionSet","selections":[{"kind":"Field","name":{"kind":"Name","value":"cycles"},"selectionSet":{"kind":"SelectionSet","selections":[{"kind":"Field","name":{"kind":"Name","value":"id"}},{"kind":"Field","name":{"kind":"Name","value":"cycleNumber"}},{"kind":"Field","name":{"kind":"Name","value":"reason"}},{"kind":"Field","name":{"kind":"Name","value":"startedAtUtc"}},{"kind":"Field","name":{"kind":"Name","value":"responseDueAtUtc"}},{"kind":"Field","name":{"kind":"Name","value":"resolutionDueAtUtc"}},{"kind":"Field","name":{"kind":"Name","value":"firstResponseAtUtc"}},{"kind":"Field","name":{"kind":"Name","value":"resolvedAtUtc"}},{"kind":"Field","name":{"kind":"Name","value":"responseState"}},{"kind":"Field","name":{"kind":"Name","value":"resolutionState"}}]}}]}}]}}]} as unknown as DocumentNode<TicketSlaHistoryQuery, TicketSlaHistoryQueryVariables>;
export const KanbanBoardDocument = {"kind":"Document","definitions":[{"kind":"OperationDefinition","operation":"query","name":{"kind":"Name","value":"KanbanBoard"},"variableDefinitions":[{"kind":"VariableDefinition","variable":{"kind":"Variable","name":{"kind":"Name","value":"filter"}},"type":{"kind":"NamedType","name":{"kind":"Name","value":"KanbanFilterInput"}}},{"kind":"VariableDefinition","variable":{"kind":"Variable","name":{"kind":"Name","value":"perColumn"}},"type":{"kind":"NamedType","name":{"kind":"Name","value":"Int"}}}],"selectionSet":{"kind":"SelectionSet","selections":[{"kind":"Field","name":{"kind":"Name","value":"kanbanBoard"},"arguments":[{"kind":"Argument","name":{"kind":"Name","value":"filter"},"value":{"kind":"Variable","name":{"kind":"Name","value":"filter"}}},{"kind":"Argument","name":{"kind":"Name","value":"perColumn"},"value":{"kind":"Variable","name":{"kind":"Name","value":"perColumn"}}}],"selectionSet":{"kind":"SelectionSet","selections":[{"kind":"Field","name":{"kind":"Name","value":"status"}},{"kind":"Field","name":{"kind":"Name","value":"total"}},{"kind":"Field","name":{"kind":"Name","value":"tickets"},"selectionSet":{"kind":"SelectionSet","selections":[{"kind":"FragmentSpread","name":{"kind":"Name","value":"BoardTicket"}}]}}]}}]}},{"kind":"FragmentDefinition","name":{"kind":"Name","value":"BoardTicket"},"typeCondition":{"kind":"NamedType","name":{"kind":"Name","value":"TicketSummaryDto"}},"selectionSet":{"kind":"SelectionSet","selections":[{"kind":"Field","name":{"kind":"Name","value":"id"}},{"kind":"Field","name":{"kind":"Name","value":"displayNumber"}},{"kind":"Field","name":{"kind":"Name","value":"title"}},{"kind":"Field","name":{"kind":"Name","value":"priority"}},{"kind":"Field","name":{"kind":"Name","value":"status"}},{"kind":"Field","name":{"kind":"Name","value":"categoryName"}},{"kind":"Field","name":{"kind":"Name","value":"customerName"}},{"kind":"Field","name":{"kind":"Name","value":"updatedAtUtc"}},{"kind":"Field","name":{"kind":"Name","value":"version"}},{"kind":"Field","name":{"kind":"Name","value":"assigneeCount"}},{"kind":"Field","name":{"kind":"Name","value":"resolutionDueAtUtc"}},{"kind":"Field","name":{"kind":"Name","value":"isOverdue"}}]}}]} as unknown as DocumentNode<KanbanBoardQuery, KanbanBoardQueryVariables>;
export const ReportDocument = {"kind":"Document","definitions":[{"kind":"OperationDefinition","operation":"query","name":{"kind":"Name","value":"Report"},"variableDefinitions":[{"kind":"VariableDefinition","variable":{"kind":"Variable","name":{"kind":"Name","value":"filter"}},"type":{"kind":"NamedType","name":{"kind":"Name","value":"AnalyticsFilterInput"}}}],"selectionSet":{"kind":"SelectionSet","selections":[{"kind":"Field","name":{"kind":"Name","value":"report"},"arguments":[{"kind":"Argument","name":{"kind":"Name","value":"filter"},"value":{"kind":"Variable","name":{"kind":"Name","value":"filter"}}}],"selectionSet":{"kind":"SelectionSet","selections":[{"kind":"Field","name":{"kind":"Name","value":"generatedAtUtc"}},{"kind":"Field","name":{"kind":"Name","value":"timeZone"}},{"kind":"Field","name":{"kind":"Name","value":"fromUtc"}},{"kind":"Field","name":{"kind":"Name","value":"toUtc"}},{"kind":"Field","name":{"kind":"Name","value":"grouping"}},{"kind":"Field","name":{"kind":"Name","value":"ticketsCreated"}},{"kind":"Field","name":{"kind":"Name","value":"trend"},"selectionSet":{"kind":"SelectionSet","selections":[{"kind":"Field","name":{"kind":"Name","value":"periodStartUtc"}},{"kind":"Field","name":{"kind":"Name","value":"created"}},{"kind":"Field","name":{"kind":"Name","value":"resolved"}},{"kind":"Field","name":{"kind":"Name","value":"avgResolutionMinutes"}}]}},{"kind":"Field","name":{"kind":"Name","value":"byStatus"},"selectionSet":{"kind":"SelectionSet","selections":[{"kind":"Field","name":{"kind":"Name","value":"key"}},{"kind":"Field","name":{"kind":"Name","value":"count"}}]}},{"kind":"Field","name":{"kind":"Name","value":"byPriority"},"selectionSet":{"kind":"SelectionSet","selections":[{"kind":"Field","name":{"kind":"Name","value":"key"}},{"kind":"Field","name":{"kind":"Name","value":"count"}}]}},{"kind":"Field","name":{"kind":"Name","value":"resolution"},"selectionSet":{"kind":"SelectionSet","selections":[{"kind":"Field","name":{"kind":"Name","value":"completedCycles"}},{"kind":"Field","name":{"kind":"Name","value":"avgMinutes"}},{"kind":"Field","name":{"kind":"Name","value":"medianMinutes"}}]}},{"kind":"Field","name":{"kind":"Name","value":"sla"},"selectionSet":{"kind":"SelectionSet","selections":[{"kind":"Field","name":{"kind":"Name","value":"eligibleCycles"}},{"kind":"Field","name":{"kind":"Name","value":"met"}},{"kind":"Field","name":{"kind":"Name","value":"breached"}},{"kind":"Field","name":{"kind":"Name","value":"compliancePercent"}}]}},{"kind":"Field","name":{"kind":"Name","value":"openOverdueTickets"}},{"kind":"Field","name":{"kind":"Name","value":"reopen"},"selectionSet":{"kind":"SelectionSet","selections":[{"kind":"Field","name":{"kind":"Name","value":"resolvedCycles"}},{"kind":"Field","name":{"kind":"Name","value":"reopenedCycles"}},{"kind":"Field","name":{"kind":"Name","value":"reopenRatePercent"}},{"kind":"Field","name":{"kind":"Name","value":"reopenEventsInPeriod"}}]}},{"kind":"Field","name":{"kind":"Name","value":"agents"},"selectionSet":{"kind":"SelectionSet","selections":[{"kind":"Field","name":{"kind":"Name","value":"membershipId"}},{"kind":"Field","name":{"kind":"Name","value":"displayName"}},{"kind":"Field","name":{"kind":"Name","value":"currentWorkload"}},{"kind":"Field","name":{"kind":"Name","value":"resolvedInPeriod"}}]}},{"kind":"Field","name":{"kind":"Name","value":"categories"},"selectionSet":{"kind":"SelectionSet","selections":[{"kind":"Field","name":{"kind":"Name","value":"categoryId"}},{"kind":"Field","name":{"kind":"Name","value":"name"}},{"kind":"Field","name":{"kind":"Name","value":"count"}},{"kind":"Field","name":{"kind":"Name","value":"sharePercent"}}]}},{"kind":"Field","name":{"kind":"Name","value":"customers"},"selectionSet":{"kind":"SelectionSet","selections":[{"kind":"Field","name":{"kind":"Name","value":"customerId"}},{"kind":"Field","name":{"kind":"Name","value":"name"}},{"kind":"Field","name":{"kind":"Name","value":"tickets"}},{"kind":"Field","name":{"kind":"Name","value":"open"}},{"kind":"Field","name":{"kind":"Name","value":"resolved"}},{"kind":"Field","name":{"kind":"Name","value":"overdueOpen"}}]}}]}}]}}]} as unknown as DocumentNode<ReportQuery, ReportQueryVariables>;