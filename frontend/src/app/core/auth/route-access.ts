import { ActivatedRouteSnapshot } from '@angular/router';
import { TenantRole } from '../api/generated/models';

/** Who may open a workspace page. Shapes navigation and routing only; the backend authorizes every request. */
export type RouteAccess = 'member' | 'staff' | 'manager';

/** Single eligibility rule shared by the route guard, the live revalidation in the shell and the sidebar. */
export function canAccess(access: RouteAccess, role: TenantRole | null): boolean {
  if (role === null) return false;
  if (access === 'manager') return role === 'TenantOwner' || role === 'Manager';
  if (access === 'staff') return role !== 'Customer';
  return true;
}

/** Access level declared by the deepest route (`data.access`), or null for pages outside a workspace. */
export function routeAccess(root: ActivatedRouteSnapshot): RouteAccess | null {
  let route = root;
  while (route.firstChild) route = route.firstChild;
  return (route.data['access'] as RouteAccess | undefined) ?? null;
}
