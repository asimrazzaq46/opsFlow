import { Injectable, inject } from '@angular/core';
import { Observable, map, timeout } from 'rxjs';
import { Api } from '../../core/api/generated/api';
import {
  createCustomer,
  createInvitation,
  setCustomerStatus,
  unlinkCustomerUser,
  updateCustomer,
} from '../../core/api/generated/functions';
import { TenantService } from '../../core/auth/tenant.service';
import {
  CustomerDocument,
  CustomerQuery,
  CustomersDocument,
  CustomersQuery,
  LinkableCustomerUsersDocument,
  LinkableCustomerUsersQuery,
} from '../../core/graphql/generated';
import { GraphqlClient } from '../../core/graphql/graphql-client';

/** Sort keys the API whitelists for customers. */
export type CustomerSort = 'name' | 'email' | 'created';

export interface CustomerListQuery {
  search?: string;
  isActive?: boolean;
  sortBy: CustomerSort;
  descending: boolean;
  page: number;
  pageSize: number;
}

export interface CustomerInput {
  name: string;
  email: string;
  phone: string | null;
  /** On update, null keeps the current portal-user link. */
  linkedUserId: string | null;
}

/** Fields an Owner or Manager may change on an existing customer. */
export interface CustomerUpdate {
  name: string;
  /** Null keeps the current portal-user link. */
  linkedUserId: string | null;
}

export type CustomerPage = CustomersQuery['customers'];
export type CustomerRow = CustomersQuery['customers']['items'][number];
export type CustomerDetailData = CustomerQuery['customer'];
export type LinkableUser = LinkableCustomerUsersQuery['linkableCustomerUsers'][number];

const WRITE_TIMEOUT_MS = 20_000;

/** Reads go through GraphQL, writes through the generated REST commands; tenant routing stays here. */
@Injectable({ providedIn: 'root' })
export class CustomersApi {
  private readonly gql = inject(GraphqlClient);
  private readonly api = inject(Api);
  private readonly tenant = inject(TenantService);

  private get tenantId(): string {
    const id = this.tenant.tenantId();
    if (!id) throw new Error('No active workspace');
    return id;
  }

  list(q: CustomerListQuery): Observable<CustomerPage> {
    return this.gql
      .query(CustomersDocument, {
        search: q.search || null,
        isActive: q.isActive ?? null,
        sortBy: q.sortBy,
        descending: q.descending,
        page: q.page,
        pageSize: q.pageSize,
      })
      .pipe(map((r) => r.customers));
  }

  get(id: string): Observable<CustomerDetailData> {
    return this.gql.query(CustomerDocument, { id }).pipe(map((r) => r.customer));
  }

  linkableUsers(search: string): Observable<LinkableUser[]> {
    return this.gql
      .query(LinkableCustomerUsersDocument, { search: search || null })
      .pipe(map((r) => r.linkableCustomerUsers));
  }

  create(body: CustomerInput) {
    return this.api
      .invoke(createCustomer, { tenantId: this.tenantId, body })
      .pipe(timeout(WRITE_TIMEOUT_MS));
  }

  /** Email and phone are not part of an update: only the account owner may change them. */
  update(customerId: string, body: CustomerUpdate) {
    return this.api
      .invoke(updateCustomer, { tenantId: this.tenantId, customerId, body: { ...body, email: null, phone: null } })
      .pipe(timeout(WRITE_TIMEOUT_MS));
  }

  setActive(customerId: string, isActive: boolean) {
    return this.api
      .invoke(setCustomerStatus, { tenantId: this.tenantId, customerId, body: { isActive } })
      .pipe(timeout(WRITE_TIMEOUT_MS));
  }

  unlinkUser(customerId: string) {
    return this.api
      .invoke(unlinkCustomerUser, { tenantId: this.tenantId, customerId })
      .pipe(timeout(WRITE_TIMEOUT_MS));
  }

  /** Reuses the invitation service: accepting links (or creates) the contact for the invited address. */
  invite(email: string) {
    return this.api
      .invoke(createInvitation, { tenantId: this.tenantId, body: { email, role: 'Customer' } })
      .pipe(timeout(WRITE_TIMEOUT_MS));
  }
}
