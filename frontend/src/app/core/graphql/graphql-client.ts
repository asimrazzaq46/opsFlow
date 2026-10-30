import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { TypedDocumentNode } from '@graphql-typed-document-node/core';
import { print } from 'graphql';
import { Observable, map, throwError } from 'rxjs';
import { TenantService } from '../auth/tenant.service';

export interface GraphqlErrorItem {
  message: string;
  extensions?: { code?: string };
}

/** Stable API error codes (see AppExceptionErrorFilter on the server). */
export class GraphqlError extends Error {
  constructor(readonly errors: GraphqlErrorItem[]) {
    super(errors[0]?.message ?? 'GraphQL error');
  }

  get code(): string | undefined {
    return this.errors[0]?.extensions?.code;
  }
}

const printed = new WeakMap<object, string>();

/**
 * The only GraphQL entry point. It goes through HttpClient, so the central Bearer + single-flight refresh interceptor applies; the
 * tenant comes from the active workspace route segment (the server validates the membership, nothing is cached across workspaces).
 */
@Injectable({ providedIn: 'root' })
export class GraphqlClient {
  private readonly http = inject(HttpClient);
  private readonly tenant = inject(TenantService);

  query<TData, TVars extends Record<string, unknown>>(doc: TypedDocumentNode<TData, TVars>, variables?: TVars): Observable<TData> {
    const tenantId = this.tenant.tenantId();
    if (!tenantId) return throwError(() => new Error('No active workspace'));
    let text = printed.get(doc);
    if (!text) {
      text = print(doc);
      printed.set(doc, text);
    }
    return this.http
      .post<{ data: TData | null; errors?: GraphqlErrorItem[] }>(`/api/v1/tenants/${tenantId}/graphql`, { query: text, variables })
      .pipe(
        map((r) => {
          if (r.errors?.length || !r.data) throw new GraphqlError(r.errors ?? []);
          return r.data;
        }),
      );
  }
}
