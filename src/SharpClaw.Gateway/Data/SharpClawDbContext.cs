using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace SharpClaw.Gateway.Data;

public class SharpClawDbContext : DbContext
{
    public SharpClawDbContext(DbContextOptions<SharpClawDbContext> options) : base(options) { }

    public DbSet<Agent> Agents => Set<Agent>();
    public DbSet<AgentToken> AgentTokens => Set<AgentToken>();
    public DbSet<ConversationThread> Threads => Set<ConversationThread>();
    public DbSet<Message> Messages => Set<Message>();
    public DbSet<LlmCall> LlmCalls => Set<LlmCall>();
    public DbSet<ToolCallRecord> ToolCalls => Set<ToolCallRecord>();
    public DbSet<Approval> Approvals => Set<Approval>();
    public DbSet<ThreadEvent> ThreadEvents => Set<ThreadEvent>();
    public DbSet<EventLogState> EventLogState => Set<EventLogState>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        // Force Kind=Utc on read so DateTimes round-trip through SQLite as UTC.
        var utc = new ValueConverter<DateTime, DateTime>(
            v => v.ToUniversalTime(),
            v => DateTime.SpecifyKind(v, DateTimeKind.Utc));
        var utcNullable = new ValueConverter<DateTime?, DateTime?>(
            v => v.HasValue ? v.Value.ToUniversalTime() : v,
            v => v.HasValue ? DateTime.SpecifyKind(v.Value, DateTimeKind.Utc) : v);

        foreach (var entity in b.Model.GetEntityTypes())
        {
            foreach (var prop in entity.GetProperties())
            {
                if (prop.ClrType == typeof(DateTime))
                    prop.SetValueConverter(utc);
                else if (prop.ClrType == typeof(DateTime?))
                    prop.SetValueConverter(utcNullable);

                prop.SetColumnName(ToSnakeCase(prop.GetColumnName()));
            }
        }

        b.Entity<Agent>(e =>
        {
            e.ToTable("agents");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.Name).IsUnique();
            e.Property(x => x.Name).IsRequired();
            e.Property(x => x.Model).IsRequired();
        });

        b.Entity<AgentToken>(e =>
        {
            e.ToTable("agent_tokens");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.TokenHash).IsUnique();
            e.HasOne(x => x.Agent).WithMany(a => a.Tokens).HasForeignKey(x => x.AgentId);
        });

        b.Entity<ConversationThread>(e =>
        {
            e.ToTable("threads");
            e.HasKey(x => x.Id);
            e.HasOne(x => x.Agent).WithMany().HasForeignKey(x => x.AgentId);
            e.HasIndex(x => x.AgentId);
        });

        b.Entity<Message>(e =>
        {
            e.ToTable("messages");
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.AgentId, x.Role, x.Status, x.CreatedAt });
            e.HasIndex(x => new { x.ThreadId, x.CreatedAt });
            // One assistant reply per user message.
            e.HasIndex(x => x.CorrelationId)
                .IsUnique()
                .HasFilter("role = 'assistant'");
        });

        b.Entity<LlmCall>(e =>
        {
            e.ToTable("llm_calls");
            e.HasKey(x => x.Id);
            e.Property(x => x.Purpose).HasDefaultValue("chat");
            e.HasIndex(x => new { x.AgentId, x.CreatedAt });
            e.HasIndex(x => x.CorrelationId);
        });

        b.Entity<ToolCallRecord>(e =>
        {
            e.ToTable("tool_calls");
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.CorrelationId, x.ToolCallId }).IsUnique();
            e.HasIndex(x => new { x.AgentId, x.CreatedAt });
            e.HasOne<Agent>().WithMany().HasForeignKey(x => x.AgentId);
        });

        b.Entity<Approval>(e =>
        {
            e.ToTable("approvals");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.CorrelationId);
            e.HasOne<Agent>().WithMany().HasForeignKey(x => x.AgentId);
        });

        b.Entity<ThreadEvent>(e =>
        {
            e.ToTable("thread_events");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.HasIndex(x => new { x.ThreadId, x.Id });
            e.HasIndex(x => x.CreatedAt);
        });

        b.Entity<EventLogState>(e =>
        {
            e.ToTable("event_log_state");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.HasData(new EventLogState { Id = 1, PrunedThrough = 0 });
        });
    }

    private static string ToSnakeCase(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        var sb = new System.Text.StringBuilder(name.Length + 8);
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (char.IsUpper(c))
            {
                if (i > 0) sb.Append('_');
                sb.Append(char.ToLowerInvariant(c));
            }
            else
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }
}
