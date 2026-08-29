using Microsoft.EntityFrameworkCore;
using ProjectC.Application.Common.Interfaces;
using ProjectC.Domain.Authentication;
using ProjectC.Domain.Events;
using ProjectC.Domain.Members;
using ProjectC.Domain.Orders;
using ProjectC.Domain.PurchaseQueue;
using ProjectC.Domain.Tickets;
using ProjectC.Domain.Venues;

namespace ProjectC.Infrastructure.Persistence;

public class ApplicationDbContext : DbContext, IApplicationDbContext
{
    public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
        : base(options)
    {
    }

    public DbSet<Member> Members => Set<Member>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<PasswordResetToken> PasswordResetTokens => Set<PasswordResetToken>();

    // 這些 DbSet 刻意不加進 IApplicationDbContext 介面：售票資料一律透過 Repository 存取
    // （design.md 決策 3），只有 Repository 實作內部需要直接用這個具體類別。
    public DbSet<Venue> Venues => Set<Venue>();
    public DbSet<SeatMap> SeatMaps => Set<SeatMap>();
    public DbSet<Seat> Seats => Set<Seat>();
    public DbSet<Event> Events => Set<Event>();
    public DbSet<EventSeat> EventSeats => Set<EventSeat>();
    public DbSet<TicketType> TicketTypes => Set<TicketType>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<OrderItem> OrderItems => Set<OrderItem>();
    public DbSet<Ticket> Tickets => Set<Ticket>();
    public DbSet<PurchaseQueueEntry> PurchaseQueueEntries => Set<PurchaseQueueEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(ApplicationDbContext).Assembly);
    }
}
