using Microsoft.EntityFrameworkCore;
using SharpClaw.Contracts;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using SharpClaw.Gateway.Configuration;
using SharpClaw.Gateway.Services.Broker;

namespace SharpClaw.Gateway.Data;

public class SharpClawDbContext : DbContext
{
    public SharpClawDbContext(DbContextOptions<SharpClawDbContext> options) : base(options) { }

    public DbSet<Agent> Agents => Set<Agent>();
    public DbSet<AgentToken> AgentTokens => Set<AgentToken>();
    public DbSet<AgentSkill> AgentSkills => Set<AgentSkill>();
    public DbSet<ConversationThread> Threads => Set<ConversationThread>();
    public DbSet<Message> Messages => Set<Message>();
    public DbSet<MessageAttachment> MessageAttachments => Set<MessageAttachment>();
    public DbSet<LlmCall> LlmCalls => Set<LlmCall>();
    public DbSet<ToolCallRecord> ToolCalls => Set<ToolCallRecord>();
    public DbSet<Approval> Approvals => Set<Approval>();
    public DbSet<ApprovalGrant> ApprovalGrants => Set<ApprovalGrant>();
    public DbSet<ThreadEvent> ThreadEvents => Set<ThreadEvent>();
    public DbSet<EventLogState> EventLogState => Set<EventLogState>();
    public DbSet<User> Users => Set<User>();
    public DbSet<Session> Sessions => Set<Session>();
    public DbSet<BrokerCall> BrokerCalls => Set<BrokerCall>();
    public DbSet<SandboxClone> SandboxClones => Set<SandboxClone>();

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

        b.Entity<AgentSkill>(e =>
        {
            e.ToTable("agent_skills");
            e.HasKey(x => new { x.AgentId, x.Name });
            e.Property(x => x.Description).IsRequired();
            e.HasOne<Agent>().WithMany().HasForeignKey(x => x.AgentId).OnDelete(DeleteBehavior.Cascade);
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
            e.HasIndex(x => new { x.Archived, x.LastMessageAt });
            e.Property(x => x.Model).HasMaxLength(ModelsOptions.MaxModelIdChars);
            // One linked thread per (recipient, sender, originating thread).
            e.HasIndex(x => new { x.AgentId, x.OriginAgentId, x.OriginThreadId })
                .IsUnique()
                .HasFilter("origin_thread_id IS NOT NULL");
        });

        b.Entity<Message>(e =>
        {
            e.ToTable("messages");
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.AgentId, x.Role, x.Status, x.CreatedAt });
            e.HasIndex(x => new { x.ThreadId, x.Seq }).IsUnique();
            e.Property(x => x.Kind).HasDefaultValue("message");
            e.Property(x => x.SentByUser).HasMaxLength(100);
            // GET /api/activity: newest conversational rows across all threads.
            e.HasIndex(x => new { x.Kind, x.CreatedAt });
            // One final assistant reply per user message; tool_request rows share the correlation id.
            e.HasIndex(x => x.CorrelationId)
                .IsUnique()
                .HasFilter("role = 'assistant' AND kind = 'message'");
            // Agent asks: one message per (asking turn, tool call), and lookups by asking turn.
            e.Property(x => x.OriginToolCallId).HasMaxLength(200);
            e.HasIndex(x => new { x.OriginMessageId, x.OriginToolCallId })
                .IsUnique()
                .HasFilter("origin_message_id IS NOT NULL");
            e.HasIndex(x => x.ChainRootId).HasFilter("chain_root_id IS NOT NULL");
        });

        b.Entity<MessageAttachment>(e =>
        {
            e.ToTable("message_attachments");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.MessageId);
            e.Property(x => x.Kind).HasMaxLength(10);
            e.Property(x => x.FileName).HasMaxLength(AttachmentRules.MaxFileNameChars);
            e.Property(x => x.MediaType).HasMaxLength(100);
            e.HasOne<Message>().WithMany().HasForeignKey(x => x.MessageId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<LlmCall>(e =>
        {
            e.ToTable("llm_calls");
            e.HasKey(x => x.Id);
            e.Property(x => x.Purpose).HasDefaultValue("chat");
            e.HasIndex(x => new { x.AgentId, x.CreatedAt });
            e.HasIndex(x => x.CorrelationId);
            // The all-agent usage window (GET /api/usage without an agent filter).
            e.HasIndex(x => x.CreatedAt);
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
            e.Property(x => x.DecidedByUser).HasMaxLength(100);
            e.Property(x => x.ArgsHash).HasMaxLength(64);
            e.Property(x => x.Detail).HasMaxLength(ApprovalDetailLimits.MaxChars);
            e.HasIndex(x => x.CorrelationId);
            e.HasOne<Agent>().WithMany().HasForeignKey(x => x.AgentId);
        });

        b.Entity<ApprovalGrant>(e =>
        {
            e.ToTable("approval_grants");
            e.HasKey(x => x.Id);
            e.Property(x => x.ToolName).HasMaxLength(64);
            e.Property(x => x.CreatedByUser).HasMaxLength(100);
            e.HasIndex(x => new { x.AgentId, x.ToolName });
            e.HasOne<Agent>().WithMany().HasForeignKey(x => x.AgentId);
        });

        b.Entity<BrokerCall>(e =>
        {
            e.ToTable("broker_calls");
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.AgentId, x.CreatedAt });
            e.HasIndex(x => x.CorrelationId);
            e.HasOne<Agent>().WithMany().HasForeignKey(x => x.AgentId);
        });

        b.Entity<SandboxClone>(e =>
        {
            e.ToTable("sandbox_clones");
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.AgentId, x.Path }).IsUnique();
            e.Property(x => x.BaseSha).HasMaxLength(64);
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

        b.Entity<User>(e =>
        {
            e.ToTable("users");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.Name).IsUnique();
        });

        b.Entity<Session>(e =>
        {
            e.ToTable("sessions");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.TokenHash).IsUnique();
            e.HasIndex(x => x.UserId);
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId);
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
