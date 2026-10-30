using Microsoft.Extensions.DependencyInjection;
using OpsFlow.Application.Abstractions;
using OpsFlow.Application.Auth;
using OpsFlow.Application.Customers;
using OpsFlow.Application.Tenants;
using OpsFlow.Application.Tickets;
using OpsFlow.Application.Invitations;
using OpsFlow.Application.Memberships;
using OpsFlow.Application.Notifications;
using OpsFlow.Application.Organizations;
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
        services.AddScoped<TicketNoteService>();
        services.AddScoped<TicketAttachmentService>();
        services.AddScoped<MembershipService>();
        services.AddScoped<InvitationService>();
        return services;
    }
}
