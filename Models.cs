using Microsoft.EntityFrameworkCore;

namespace FindMyID.Api;

public class Account
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = "";
    public string Email { get; set; } = "";
    public string Index { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public bool Alerts { get; set; } = true;
    public bool EmailAlerts { get; set; } = true;
    public bool Dark { get; set; }
}
public class Report
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = "";
    public string Index { get; set; } = "";
    public string Institution { get; set; } = "University of Ghana";
    public string Location { get; set; } = "";
    public string Status { get; set; } = "Found";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string? OwnerId { get; set; }
    public Account? Owner { get; set; }
    public byte[]? Photo { get; set; }
    public string? PhotoType { get; set; }
}
public class FinderMessage
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string ReportId { get; set; } = "";
    public Report Report { get; set; } = null!;
    public string SenderId { get; set; } = "";
    public Account Sender { get; set; } = null!;
    public string RecipientId { get; set; } = "";
    public Account Recipient { get; set; } = null!;
    public string Body { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
public class Feedback
{
    public int Id { get; set; }
    public string AccountId { get; set; } = "";
    public Account Account { get; set; } = null!;
    public string Body { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
public class AppDb(DbContextOptions<AppDb> options) : DbContext(options)
{
    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<Report> Reports => Set<Report>();
    public DbSet<FinderMessage> Messages => Set<FinderMessage>();
    public DbSet<Feedback> Feedback => Set<Feedback>();
    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<Account>().HasIndex(x => x.Email).IsUnique();
        model.Entity<Report>().HasIndex(x => x.Index);
        model.Entity<Report>().HasOne(x => x.Owner).WithMany().HasForeignKey(x => x.OwnerId);
        model.Entity<FinderMessage>().HasOne(x => x.Sender).WithMany().HasForeignKey(x => x.SenderId);
        model.Entity<FinderMessage>().HasOne(x => x.Recipient).WithMany().HasForeignKey(x => x.RecipientId);
    }
}
public record Credentials(string Email, string Password, string? Name, string? Index);
public record ProfileInput(string Name, string Index);
public record Preferences(bool Alerts, bool Email, bool Dark);
public record ReportInput(string Name, string Index, string Location, string Photo);
public record TextInput(string Body);
