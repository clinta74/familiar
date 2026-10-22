using Familiar.Domain.Accounts;
using Familiar.Domain.Calendars;
using Familiar.Domain.Conversations;
using Familiar.Domain.Familiars;
using Familiar.Domain.Hooks;
using Familiar.Domain.Memories;
using Familiar.Domain.Notifications;
using Familiar.Domain.Scheduling;
using Familiar.Domain.Tools;
using Familiar.Persistence.Configurations;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Familiar.Persistence;

public sealed class FamiliarDbContext(DbContextOptions<FamiliarDbContext> options)
    : DbContext(options), IDataProtectionKeyContext
{
    public DbSet<Account> Accounts => Set<Account>();

    public DbSet<AccessToken> AccessTokens => Set<AccessToken>();

    public DbSet<FamiliarProfile> FamiliarProfiles => Set<FamiliarProfile>();

    public DbSet<Conversation> Conversations => Set<Conversation>();

    public DbSet<Message> Messages => Set<Message>();

    public DbSet<MessageImage> MessageImages => Set<MessageImage>();

    public DbSet<ScheduledJob> ScheduledJobs => Set<ScheduledJob>();

    public DbSet<ToolInvocation> ToolInvocations => Set<ToolInvocation>();

    public DbSet<PushSubscription> PushSubscriptions => Set<PushSubscription>();

    public DbSet<ServerSecret> ServerSecrets => Set<ServerSecret>();

    public DbSet<ServerSetting> ServerSettings => Set<ServerSetting>();

    public DbSet<Invite> Invites => Set<Invite>();

    public DbSet<MemoryFact> Memories => Set<MemoryFact>();

    public DbSet<AvatarAsset> AvatarAssets => Set<AvatarAsset>();

    public DbSet<HookToken> HookTokens => Set<HookToken>();

    public DbSet<Webhook> Webhooks => Set<Webhook>();

    public DbSet<McpServer> McpServers => Set<McpServer>();

    public DbSet<HomeAssistantLink> HomeAssistantLinks => Set<HomeAssistantLink>();

    public DbSet<CalendarSource> CalendarSources => Set<CalendarSource>();

    public DbSet<CalendarEvent> CalendarEvents => Set<CalendarEvent>();

    /// <summary>
    /// ASP.NET Data Protection's key ring. Kept in the database rather than a container path, so
    /// recreating the server container doesn't invalidate every session cookie.
    /// </summary>
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasPostgresExtension("citext");
        modelBuilder.HasPostgresExtension("vector");

        modelBuilder.ApplyConfigurationsFromAssembly(typeof(FamiliarDbContext).Assembly);

        // Last, so it sees every table, column, key and index the configurations declared.
        SnakeCaseNaming.ApplyTo(modelBuilder);
    }
}
