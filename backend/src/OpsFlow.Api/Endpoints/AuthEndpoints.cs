using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OpsFlow.Api.Infrastructure;
using OpsFlow.Application.Abstractions;
using OpsFlow.Infrastructure.Identity;

namespace OpsFlow.Api.Endpoints;

internal static class AuthEndpoints
{
    private const string InvalidTokenTitle = "The link is invalid or has expired.";

    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/auth").WithTags("Auth").RequireRateLimiting(RateLimitPolicies.Sensitive);

        group.MapGet("/csrf", IssueCsrfToken).WithName("GetCsrfToken").AllowAnonymous();
        group.MapPost("/register", Register).WithName("Register").AllowAnonymous();
        group.MapPost("/confirm-email", ConfirmEmail).WithName("ConfirmEmail").AllowAnonymous();
        group.MapPost("/resend-confirmation", ResendConfirmation).WithName("ResendConfirmation").AllowAnonymous();
        group.MapPost("/login", Login).WithName("Login").AllowAnonymous();
        group.MapPost("/forgot-password", ForgotPassword).WithName("ForgotPassword").AllowAnonymous();
        group.MapPost("/reset-password", ResetPassword).WithName("ResetPassword").AllowAnonymous();
        group.MapPost("/logout", Logout).WithName("Logout");
        group.MapGet("/me", Me).WithName("GetCurrentUser");
        return app;
    }

    private static NoContent IssueCsrfToken(HttpContext http, IAntiforgery antiforgery)
    {
        var tokens = antiforgery.GetAndStoreTokens(http);
        http.Response.Cookies.Append("XSRF-TOKEN", tokens.RequestToken!, new CookieOptions
        {
            HttpOnly = false, // Angular must read it to echo it in X-XSRF-TOKEN
            SameSite = SameSiteMode.Strict,
            Secure = http.Request.IsHttps,
        });
        return TypedResults.NoContent();
    }

    private static async Task<Results<Accepted, ValidationProblem>> Register(
        RegisterRequest request, UserManager<ApplicationUser> users, IOpsFlowDbContext db, IEmailService email, IOptions<AppOptions> app, TimeProvider clock, CancellationToken ct)
    {
        var user = new ApplicationUser
        {
            UserName = request.Email.Trim(),
            Email = request.Email.Trim(),
            DisplayName = request.DisplayName.Trim(),
            CreatedAtUtc = clock.GetUtcNow().UtcDateTime,
        };

        // UPDATED: sirf username unique-index violation (concurrent duplicate) 202 banta hai; baaqi DB failures propagate + log hoti hain
        IdentityResult result;
        try
        {
            result = await users.CreateAsync(user, request.Password);
        }
        catch (DbUpdateException ex) when (db.IsUniqueViolation(ex, UniqueConstraintKind.UserName))
        {
            return TypedResults.Accepted((string?)null);
        }

        if (result.Succeeded)
        {
            await SendConfirmationAsync(users, email, app.Value, user, ct);
        }
        else if (!result.Errors.All(e => e.Code is nameof(IdentityErrorDescriber.DuplicateUserName) or nameof(IdentityErrorDescriber.DuplicateEmail)))
        {
            return TypedResults.ValidationProblem(ToErrors(result));
        }

        // Same answer for new and existing emails so registration cannot be used to enumerate accounts.
        return TypedResults.Accepted((string?)null);
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> ConfirmEmail(ConfirmEmailRequest request, UserManager<ApplicationUser> users)
    {
        var user = await users.FindByIdAsync(request.UserId.ToString());
        if (user is null || Decode(request.Token) is not { } token || !(await users.ConfirmEmailAsync(user, token)).Succeeded)
        {
            return TypedResults.Problem(title: InvalidTokenTitle, statusCode: StatusCodes.Status400BadRequest);
        }

        return TypedResults.NoContent();
    }

    private static async Task<Accepted> ResendConfirmation(
        EmailRequest request, UserManager<ApplicationUser> users, IEmailService email, IOptions<AppOptions> app, CancellationToken ct)
    {
        var user = await users.FindByEmailAsync(request.Email.Trim());
        if (user is { EmailConfirmed: false })
        {
            await SendConfirmationAsync(users, email, app.Value, user, ct);
        }

        return TypedResults.Accepted((string?)null);
    }

    private static async Task<Results<Ok<UserInfo>, ProblemHttpResult>> Login(LoginRequest request, SignInManager<ApplicationUser> signIn, UserManager<ApplicationUser> users)
    {
        var result = await signIn.PasswordSignInAsync(request.Email.Trim(), request.Password, request.RememberMe, lockoutOnFailure: true);
        if (!result.Succeeded)
        {
            // One generic answer for wrong password, unknown user, unconfirmed email and lockout.
            return TypedResults.Problem(title: "Invalid credentials or the account is not available.", statusCode: StatusCodes.Status401Unauthorized);
        }

        var user = (await users.FindByEmailAsync(request.Email.Trim()))!;
        return TypedResults.Ok(new UserInfo(user.Id, user.Email!, user.DisplayName));
    }

    private static async Task<Accepted> ForgotPassword(
        EmailRequest request, UserManager<ApplicationUser> users, IEmailService email, IOptions<AppOptions> app, CancellationToken ct)
    {
        var user = await users.FindByEmailAsync(request.Email.Trim());
        if (user is { EmailConfirmed: true })
        {
            var token = Encode(await users.GeneratePasswordResetTokenAsync(user));
            var link = $"{Base(app.Value)}/auth/reset-password?email={Uri.EscapeDataString(user.Email!)}&token={token}";
            await email.SendAsync(user.Email!, "Reset your OpsFlow password", $"Reset your password: {link}\n\nIf you did not request this, ignore this email.", ct);
        }

        return TypedResults.Accepted((string?)null);
    }

    private static async Task<Results<NoContent, ProblemHttpResult, ValidationProblem>> ResetPassword(ResetPasswordRequest request, UserManager<ApplicationUser> users)
    {
        var user = await users.FindByEmailAsync(request.Email.Trim());
        if (user is null || Decode(request.Token) is not { } token)
        {
            return TypedResults.Problem(title: InvalidTokenTitle, statusCode: StatusCodes.Status400BadRequest);
        }

        var result = await users.ResetPasswordAsync(user, token, request.NewPassword);
        if (result.Succeeded)
        {
            return TypedResults.NoContent();
        }

        return result.Errors.Any(e => e.Code == nameof(IdentityErrorDescriber.InvalidToken))
            ? TypedResults.Problem(title: InvalidTokenTitle, statusCode: StatusCodes.Status400BadRequest)
            : TypedResults.ValidationProblem(ToErrors(result));
    }

    private static async Task<NoContent> Logout(SignInManager<ApplicationUser> signIn)
    {
        await signIn.SignOutAsync();
        return TypedResults.NoContent();
    }

    private static async Task<Results<Ok<UserInfo>, UnauthorizedHttpResult>> Me(ClaimsPrincipal principal, UserManager<ApplicationUser> users)
    {
        var user = await users.GetUserAsync(principal);
        return user is null ? TypedResults.Unauthorized() : TypedResults.Ok(new UserInfo(user.Id, user.Email!, user.DisplayName));
    }

    private static async Task SendConfirmationAsync(UserManager<ApplicationUser> users, IEmailService email, AppOptions app, ApplicationUser user, CancellationToken ct)
    {
        var token = Encode(await users.GenerateEmailConfirmationTokenAsync(user));
        var link = $"{Base(app)}/auth/confirm-email?userId={user.Id}&token={token}";
        await email.SendAsync(user.Email!, "Confirm your OpsFlow email", $"Welcome to OpsFlow. Confirm your email: {link}", ct);
    }

    private static string Base(AppOptions app) => app.BaseUrl.TrimEnd('/');

    private static string Encode(string token) => WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(token));

    private static string? Decode(string token)
    {
        try
        {
            return Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(token));
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static Dictionary<string, string[]> ToErrors(IdentityResult result) =>
        result.Errors.GroupBy(e => e.Code).ToDictionary(g => g.Key, g => g.Select(e => e.Description).ToArray());
}
