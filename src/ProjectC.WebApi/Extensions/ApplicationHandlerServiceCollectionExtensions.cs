using FluentValidation;
using ProjectC.Application.Authentication.Login;
using ProjectC.Application.Authentication.Logout;
using ProjectC.Application.Authentication.PasswordReset;
using ProjectC.Application.Authentication.Refresh;
using ProjectC.Application.Events.CreateEvent;
using ProjectC.Application.Events.GetAdminEvents;
using ProjectC.Application.Events.GetEventSeats;
using ProjectC.Application.Events.GetEvents;
using ProjectC.Application.Events.SetEventQueueMode;
using ProjectC.Application.Members.Activate;
using ProjectC.Application.Members.Deactivate;
using ProjectC.Application.Members.GetMyProfile;
using ProjectC.Application.Members.Register;
using ProjectC.Application.Members.RegisterRealName;
using ProjectC.Application.Members.UpdateMyProfile;
using ProjectC.Application.Orders;
using ProjectC.Application.Orders.GetOrderById;
using ProjectC.Application.Orders.GetMyOrderDetail;
using ProjectC.Application.Orders.GetMyOrders;
using ProjectC.Application.Orders.GetOrders;
using ProjectC.Application.Orders.GetEventSalesReport;
using ProjectC.Application.Organizers.ApplyForOrganizer;
using ProjectC.Application.Organizers.GetMyOrganizers;
using ProjectC.Application.Organizers.GetPendingOrganizers;
using ProjectC.Application.Organizers.ReviewOrganizer;
using ProjectC.Application.Organizers.SuspendOrganizer;
using ProjectC.Application.Organizers.SwitchOrganizerContext;
using ProjectC.Application.PurchaseQueue.GetMyQueueStatus;
using ProjectC.Application.PurchaseQueue.JoinPurchaseQueue;
using ProjectC.Application.Tickets.CreateTicketType;
using ProjectC.Application.Tickets.GetTicketQrCode;
using ProjectC.Application.Tickets.GetTicketTypes;
using ProjectC.Application.Tickets.GetTicketHolder;
using ProjectC.Application.Tickets.RedeemTicket;
using ProjectC.Application.Venues.CreateSeatMap;
using ProjectC.Application.Venues.CreateVenue;
using ProjectC.Application.Venues.GetSeatMapById;
using ProjectC.Application.Venues.GetVenueById;
using ProjectC.Application.Venues.GetVenues;

namespace ProjectC.WebApi.Extensions;

public static class ApplicationHandlerServiceCollectionExtensions
{
    public static IServiceCollection AddApplicationHandlers(this IServiceCollection services)
    {
        services.AddValidatorsFromAssemblyContaining<RegisterMemberRequestValidator>();

        services.AddScoped<CreateVenueHandler>();
        services.AddScoped<CreateSeatMapHandler>();
        services.AddScoped<GetVenuesHandler>();
        services.AddScoped<GetVenueByIdHandler>();
        services.AddScoped<GetSeatMapByIdHandler>();
        services.AddScoped<CreateEventHandler>();
        services.AddScoped<CreateTicketTypeHandler>();
        services.AddScoped<GetAdminEventsHandler>();

        services.AddScoped<CreateOrderHandler>();
        services.AddScoped<ConfirmOrderHandler>();
        services.AddScoped<CancelOrderHandler>();
        services.AddScoped<OrderService>();
        services.AddScoped<GetEventsHandler>();
        services.AddScoped<GetEventSeatsHandler>();
        services.AddScoped<GetTicketTypesHandler>();
        services.AddScoped<GetOrdersHandler>();
        services.AddScoped<GetEventSalesReportHandler>();
        services.AddScoped<GetOrderByIdHandler>();
        services.AddScoped<GetMyOrdersHandler>();
        services.AddScoped<GetMyOrderDetailHandler>();
        services.AddScoped<GetTicketQrCodeHandler>();
        services.AddScoped<RedeemTicketHandler>();
        services.AddScoped<GetTicketHolderHandler>();
        services.AddScoped<SetEventQueueModeHandler>();
        services.AddScoped<JoinPurchaseQueueHandler>();
        services.AddScoped<GetMyQueueStatusHandler>();

        services.AddScoped<ApplyForOrganizerHandler>();
        services.AddScoped<GetMyOrganizersHandler>();
        services.AddScoped<GetPendingOrganizersHandler>();
        services.AddScoped<ReviewOrganizerHandler>();
        services.AddScoped<SuspendOrganizerHandler>();
        services.AddScoped<SwitchOrganizerContextHandler>();

        services.AddScoped<RegisterMemberHandler>();
        services.AddScoped<GetMyProfileHandler>();
        services.AddScoped<UpdateMyProfileHandler>();
        services.AddScoped<RegisterRealNameHandler>();
        services.AddScoped<ActivateMemberHandler>();
        services.AddScoped<DeactivateMemberHandler>();
        services.AddScoped<LoginHandler>();
        services.AddScoped<RefreshTokenHandler>();
        services.AddScoped<LogoutHandler>();
        services.AddScoped<RequestPasswordResetHandler>();
        services.AddScoped<ResetPasswordHandler>();

        return services;
    }
}
