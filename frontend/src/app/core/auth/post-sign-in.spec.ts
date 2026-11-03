import { TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import { of, throwError } from 'rxjs';
import { OrganizationDto } from '../api/generated/models';
import { PostSignIn } from './post-sign-in';
import { TenantService } from './tenant.service';

const org = (id: string): OrganizationDto => ({ id, name: id, slug: id, role: 'Agent' });

describe('PostSignIn', () => {
  const tenant = { load: vi.fn() };
  const router = { navigateByUrl: vi.fn() };
  let post: PostSignIn;

  beforeEach(() => {
    vi.clearAllMocks();
    TestBed.configureTestingModule({
      providers: [
        { provide: TenantService, useValue: tenant },
        { provide: Router, useValue: router },
      ],
    });
    post = TestBed.inject(PostSignIn);
  });

  it.each([
    [[], '/workspaces/new'],
    [[org('a')], '/dashboard'],
    [[org('a'), org('b')], '/workspaces'],
  ])('routes by membership count (%#)', (orgs, target) => {
    tenant.load.mockReturnValue(of(orgs));
    post.enter(null);
    expect(router.navigateByUrl).toHaveBeenCalledWith(target);
  });

  it('prefers a safe return URL and ignores an external one', () => {
    tenant.load.mockReturnValue(of([org('a')]));
    post.enter('/invitations/accept?token=t');
    expect(router.navigateByUrl).toHaveBeenLastCalledWith('/invitations/accept?token=t');
    post.enter('https://evil.test');
    expect(router.navigateByUrl).toHaveBeenLastCalledWith('/dashboard');
  });

  it('falls back to the workspace picker when memberships cannot be loaded', () => {
    tenant.load.mockReturnValue(throwError(() => new Error('down')));
    post.enter(null);
    expect(router.navigateByUrl).toHaveBeenCalledWith('/workspaces');
  });
});
