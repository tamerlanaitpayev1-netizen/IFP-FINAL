using Microsoft.EntityFrameworkCore;

namespace ArenaQuiz.Data;

public sealed class ArenaDbContext(DbContextOptions<ArenaDbContext> options) : DbContext(options)
{
    public DbSet<QuizEntity> Quizzes => Set<QuizEntity>();
    public DbSet<QuestionEntity> Questions => Set<QuestionEntity>();
    public DbSet<GameSessionEntity> GameSessions => Set<GameSessionEntity>();
    public DbSet<PlayerResultEntity> PlayerResults => Set<PlayerResultEntity>();
    public DbSet<QuestionStatEntity> QuestionStats => Set<QuestionStatEntity>();
    public DbSet<UserAccountEntity> Users => Set<UserAccountEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<QuizEntity>(entity =>
        {
            entity.HasKey(q => q.Id);
            entity.Property(q => q.Title).HasMaxLength(200).IsRequired();
            entity.Property(q => q.Category).HasMaxLength(80).IsRequired();
            entity.HasMany(q => q.Questions)
                .WithOne(qn => qn.Quiz)
                .HasForeignKey(qn => qn.QuizId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<QuestionEntity>(entity =>
        {
            entity.HasKey(q => q.Id);
            entity.Property(q => q.Prompt).HasMaxLength(600).IsRequired();
            entity.HasIndex(q => new { q.QuizId, q.OrderIndex });
        });

        modelBuilder.Entity<GameSessionEntity>(entity =>
        {
            entity.HasKey(s => s.Id);
            entity.Property(s => s.RoomCode).HasMaxLength(16).IsRequired();
            entity.HasIndex(s => s.FinishedAtUtc);
            entity.HasMany(s => s.Results)
                .WithOne(r => r.GameSession)
                .HasForeignKey(r => r.GameSessionId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<PlayerResultEntity>(entity =>
        {
            entity.HasKey(r => r.Id);
            entity.Property(r => r.Nickname).HasMaxLength(60).IsRequired();
        });

        modelBuilder.Entity<QuestionStatEntity>(entity =>
        {
            entity.HasKey(q => q.Id);
            entity.HasIndex(q => q.QuestionId).IsUnique();
        });

        modelBuilder.Entity<UserAccountEntity>(entity =>
        {
            entity.HasKey(u => u.Id);
            entity.HasIndex(u => u.Username).IsUnique();
        });
    }
}
