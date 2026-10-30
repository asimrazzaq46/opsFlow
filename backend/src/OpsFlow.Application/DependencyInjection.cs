using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpsFlow.Application.Abstractions;
using OpsFlow.Application.Auth;
using OpsFlow.Application.Customers;
using OpsFlow.Application.Tenants;
using OpsFlow.Application.Tickets;
using OpsFlow.Application.Invitations;
using OpsFlow.Application.Memberships;
using OpsFlow.Application.Notifications;
using OpsFlow.Application.Organizations;
using OpsFlow.Application.Outbox;
using OpsFlow.Realtime.Chat;

namespace OpsFlow.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<TenantContext>();
        services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<TenantContext>());
        // UPDATED: custom authentication services (Identity hata diya gaya)
        services.AddScoped<UserTokenService>();
        services.AddScoped<SessionService>();
        services.AddScoped<AccountService>();
        services.AddScoped<OrganizationService>();
        services.AddScoped<ActorAccess>();
        // UPDATED: Milestone 2 services
        services.AddScoped<TenantDefaultsService>();
        services.AddScoped<CustomerService>();
        services.AddScoped<TicketAccess>();
        services.AddScoped<TicketCategoryService>();
        services.AddScoped<SlaPolicyService>();
        services.AddScoped<TicketService>();
        services.AddScoped<TicketWorkflowService>();
        services.TryAddScoped<Analytics.IReportingTimeZoneProvider, Analytics.TenantReportingTimeZoneProvider>(); // UPDATED: tenant settings se timezone (UTC fallback)
        services.AddScoped<Tenants.TenantSettingsService>();
        services.AddScoped<Analytics.AnalyticsService>(); // UPDATED: Milestone 5 - dashboard/report aggregates
        services.AddScoped<TicketAssignmentService>();
        // UPDATED: Milestone 3 services
        services.AddScoped<TicketAudience>();
        services.AddScoped<NotificationService>();
        services.AddScoped<TicketMessageMapper>();
        services.AddScoped<TicketChatStore>();
        services.AddScoped<IChatConversationAuthorizer>(sp => sp.GetRequiredService<TicketChatStore>());
        services.AddScoped<IChatMessageStore<TicketMessageDto>>(sp => sp.GetRequiredService<TicketChatStore>());
        services.AddSingleton<IChatMessageValidator>(new DefaultChatMessageValidator(4000));
        services.AddScoped<ChatService<TicketMessageDto>>(); // UPDATED: ticket chat generic OpsFlow.Realtime pipeline se
        services.AddScoped<TicketMessageService>();
        services.AddScoped<TicketNoteMapper>();
        services.AddScoped<TicketNoteService>();
        // UPDATED: Milestone 4 - transactional outbox (writer + dispatcher + per-channel handlers)
        services.AddScoped<OutboxWriter>();
        services.AddScoped<NotificationEligibility>();
        services.AddScoped<IOutboxChannelHandler, RealtimeOutboxHandler>();
        services.AddScoped<IOutboxChannelHandler, EmailOutboxHandler>();
        services.AddScoped<IOutboxChannelHandler, CacheOutboxHandler>();
        services.AddScoped<OutboxDispatcher>();
        services.AddScoped<SlaMonitor>();
        services.TryAddSingleton<ITenantCache, NoCache>(); // Infrastructure replaces it with the Redis cache when configured
        services.AddScoped<TicketAttachmentService>();
        services.AddScoped<MembershipService>();
        services.AddScoped<InvitationService>();
        return services;
    }
}
