using ProjectC.Application.Orders;
using ProjectC.Application.Orders.PlaceOrder;
using ProjectC.Domain.Payments;
using ProjectC.Infrastructure.Payments;
using ProjectC.Infrastructure.Persistence;
using ProjectC.Infrastructure.Persistence.Repositories;
using ProjectC.Infrastructure.Security;

namespace ProjectC.Infrastructure.Tests.TestSupport;

/// <summary>共用的 OrderService 組裝邏輯，避免每個並發測試檔各自手動重複 11 個建構參數
/// （OrderService 建構子曾在 ticket-issuance-and-redemption 這次變更中改過一次）。</summary>
public static class OrderServiceTestFactory
{
    public static OrderService Create(ApplicationDbContext dbContext, IPaymentGateway? paymentGateway = null)
    {
        var dateTimeProvider = new SystemDateTimeProvider();
        return new OrderService(
            new TicketTypeRepository(dbContext),
            new EventSeatRepository(dbContext),
            new EventRepository(dbContext),
            new SeatMapRepository(dbContext),
            new OrderRepository(dbContext),
            new UnitOfWork(dbContext),
            new PlaceOrderRequestValidator(),
            dateTimeProvider,
            new CreateOrderHandler(dateTimeProvider),
            new ConfirmOrderHandler(dateTimeProvider, paymentGateway ?? new MockPaymentGateway(new MockPaymentGatewayOptions()), new TicketRepository(dbContext)),
            new CancelOrderHandler(dateTimeProvider));
    }
}
