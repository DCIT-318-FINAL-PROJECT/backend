using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace FindMyID.Api;

public static class SeedData
{
    public const string Password = "FindMyID123!";

    // Repeatable development fixtures. Existing accounts and edited reports are preserved.
    public static async Task ApplyAsync(AppDb db, PasswordHasher<Account> hasher)
    {
        await using var transaction = await db.Database.BeginTransactionAsync();
        var ama = await AccountAsync(db, hasher, "ama", "Ama Mensah", "22012345");
        var kwame = await AccountAsync(db, hasher, "kwame", "Kwame Owusu", "23012345");
        var abena = await AccountAsync(db, hasher, "abena", "Abena Mansa", "24012345");
        // A small placeholder PNG; seed reports contain fictional sample information.
        var photo = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+j4V8AAAAASUVORK5CYII=");
        var now = DateTime.UtcNow;
        var fixtures = new[] {
            new Report { Id = "ama", Name = "Ama Mensah", Index = "22012345", Location = "Balme Library, Legon", OwnerId = kwame.Id, CreatedAt = now.AddHours(-1) },
            new Report { Id = "kwame", Name = "Kwame Asante", Index = "23012345", Location = "Campus security office", OwnerId = ama.Id, CreatedAt = now.AddHours(-2) },
            new Report { Id = "abena", Name = "Abena Mansa", Index = "24012345", Location = "Akuafo Hall reception", OwnerId = kwame.Id, CreatedAt = now.AddDays(-1) },
            new Report { Id = "marcus", Name = "Marcus Thompson", Index = "22000928", Institution = "State University of Technology", Location = "Campus security office", OwnerId = abena.Id, CreatedAt = now.AddHours(-3) },
            new Report { Id = "sarah", Name = "Sarah Jenkins", Index = "22000104", Institution = "Metropolitan Arts College", Location = "Student services", Status = "Solved", OwnerId = ama.Id, CreatedAt = now.AddDays(-2) }
        };
        foreach (var fixture in fixtures)
        {
            var existing = await db.Reports.FindAsync(fixture.Id);
            if (existing is null)
            {
                fixture.Photo = photo;
                fixture.PhotoType = "image/png";
                db.Reports.Add(fixture);
            }
            else if (existing.OwnerId is null)
            {
                // Upgrade the ownerless samples created by the earlier backend version.
                existing.OwnerId = fixture.OwnerId;
                existing.Photo ??= photo;
                existing.PhotoType ??= "image/png";
            }
        }
        if (!await db.Messages.AnyAsync(m => m.Id == "seed-handover-message"))
        {
            var finderId = db.Reports.Local.Single(r => r.Id == "kwame").OwnerId!;
            if (finderId != kwame.Id)
                db.Messages.Add(new FinderMessage {
                    Id = "seed-handover-message", ReportId = "kwame", SenderId = kwame.Id,
                    RecipientId = finderId, Body = "Hi, can we arrange a handover at campus security?", CreatedAt = now.AddMinutes(-30)
                });
        }
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
    }

    private static async Task<Account> AccountAsync(AppDb db, PasswordHasher<Account> hasher, string key, string name, string index)
    {
        var email = $"{key}@st.ug.edu.gh";
        var existing = await db.Accounts.SingleOrDefaultAsync(a => a.Email == email);
        if (existing is not null) return existing;
        var account = new Account { Id = $"seed-{key}", Name = name, Email = email, Index = index };
        account.PasswordHash = hasher.HashPassword(account, Password);
        db.Accounts.Add(account);
        return account;
    }
}
