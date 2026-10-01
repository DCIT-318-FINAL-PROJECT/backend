using System.Security.Claims;
using System.Text.RegularExpressions;
using System.Threading.RateLimiting;
using FindMyID.Api;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

var seedOnly = args.Contains("--seed");
var builder = WebApplication.CreateBuilder(args.Where(arg => arg != "--seed").ToArray());
if (seedOnly && !builder.Environment.IsDevelopment())
    throw new InvalidOperationException("Demo seeding is available only in the Development environment.");
var dataDirectory = Path.Combine(builder.Environment.ContentRootPath, "Data");
Directory.CreateDirectory(dataDirectory);
builder.Services.AddDbContext<AppDb>(o => o.UseSqlite(
    builder.Configuration.GetConnectionString("Database") ?? $"Data Source={Path.Combine(dataDirectory, "findmyid.db")}"));
builder.Services.AddScoped<PasswordHasher<Account>>();
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(o =>
{
    o.Cookie.Name = "findmyid-session";
    o.Cookie.HttpOnly = true;
    o.Cookie.SameSite = SameSiteMode.Strict;
    o.Cookie.SecurePolicy = builder.Environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
    o.ExpireTimeSpan = TimeSpan.FromDays(7);
    o.Events.OnRedirectToLogin = c => { c.Response.StatusCode = 401; return Task.CompletedTask; };
    o.Events.OnRedirectToAccessDenied = c => { c.Response.StatusCode = 403; return Task.CompletedTask; };
});
builder.Services.AddAuthorization();
builder.Services.AddRateLimiter(o => o.AddPolicy("auth", context =>
    RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
    _ => new FixedWindowRateLimiterOptions { PermitLimit = 20, Window = TimeSpan.FromMinutes(1) })));
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 3 * 1024 * 1024);
var app = builder.Build();
app.UseExceptionHandler(handler => handler.Run(async context =>
{
    context.Response.StatusCode = 500;
    await context.Response.WriteAsJsonAsync(new { message = "Unable to complete the request. Please try again." });
}));
// Only JSON requests with a custom header may mutate data. Cross-origin forms cannot
// supply this header, and no cross-origin CORS permissions are granted.
app.Use(async (context, next) =>
{
    if (context.Request.Method is "POST" or "PUT" or "PATCH" or "DELETE")
    {
        if (context.Request.Headers["X-FindMyID"] != "1" ||
            !context.Request.HasJsonContentType())
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsJsonAsync(new { message = "A JSON API request is required." });
            return;
        }
    }
    context.Response.Headers.CacheControl = "no-store";
    await next();
});
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/api/health", async (AppDb db) => Results.Ok(new { status = await db.Database.CanConnectAsync() ? "ok" : "unavailable" }));
app.MapGet("/api/bootstrap", async (HttpContext ctx, AppDb db) =>
{
    var account = await CurrentAccount(ctx, db);
    var reports = await db.Reports.Include(r => r.Owner).OrderByDescending(r => r.CreatedAt).ToListAsync();
    return Results.Ok(new {
        profile = account is null ? null : Profile(account),
        prefs = account is null ? new Preferences(true, true, false) : new Preferences(account.Alerts, account.EmailAlerts, account.Dark),
        records = reports.Select(r => ReportDto(r, account))
    });
});
app.MapPost("/api/auth/register", async (Credentials input, HttpContext ctx, AppDb db, PasswordHasher<Account> hasher) =>
{
    var email = (input.Email ?? "").Trim().ToLowerInvariant();
    if (!Regex.IsMatch(email, @"^[^@\s]+@(?:st\.)?ug\.edu\.gh$") || email.Length > 254)
        return Error("Use your University of Ghana email address.");
    if (string.IsNullOrWhiteSpace(input.Name) || input.Name.Trim().Length > 100 || !ValidIndex(input.Index))
        return Error("Enter your name and an 8-digit index number.");
    if (string.IsNullOrEmpty(input.Password) || input.Password.Length < 8 || input.Password.Length > 128)
        return Error("Use a password between 8 and 128 characters.");
    if (await db.Accounts.AnyAsync(a => a.Email == email)) return Error("An account already exists for this email.", 409);
    var account = new Account { Email = email, Name = input.Name.Trim(), Index = input.Index! };
    account.PasswordHash = hasher.HashPassword(account, input.Password);
    db.Accounts.Add(account);
    try { await db.SaveChangesAsync(); }
    catch (DbUpdateException) { return Error("An account already exists for this email.", 409); }
    await SignIn(ctx, account);
    return Results.Ok(Profile(account));
}).RequireRateLimiting("auth");
app.MapPost("/api/auth/login", async (Credentials input, HttpContext ctx, AppDb db, PasswordHasher<Account> hasher) =>
{
    if (string.IsNullOrEmpty(input.Password) || input.Password.Length > 128) return Error("Invalid email or password.", 401);
    var account = await db.Accounts.SingleOrDefaultAsync(a => a.Email == (input.Email ?? "").Trim().ToLowerInvariant());
    if (account is null || hasher.VerifyHashedPassword(account, account.PasswordHash, input.Password) == PasswordVerificationResult.Failed)
        return Error("Invalid email or password.", 401);
    await SignIn(ctx, account);
    return Results.Ok(Profile(account));
}).RequireRateLimiting("auth");
app.MapPost("/api/auth/logout", async (HttpContext ctx) => { await ctx.SignOutAsync(); return Results.NoContent(); });
app.MapGet("/api/reports", async (string? q, HttpContext ctx, AppDb db) =>
{
    var query = db.Reports.Include(r => r.Owner).AsQueryable();
    if (!string.IsNullOrWhiteSpace(q))
    {
        q = q.Trim();
        var lower = q.ToLowerInvariant();
        var lastFour = Regex.IsMatch(q, @"^\d{4,8}$") ? q[^4..] : "";
        query = query.Where(r => r.Status != "Solved" && (r.Name.ToLower().Contains(lower) || r.Index == q || (lastFour != "" && r.Index.EndsWith(lastFour))));
    }
    var account = await CurrentAccount(ctx, db);
    return Results.Ok((await query.OrderByDescending(r => r.CreatedAt).ToListAsync()).Select(r => ReportDto(r, account)));
});
app.MapPost("/api/reports", async (ReportInput input, HttpContext ctx, AppDb db) =>
{
    var account = await CurrentAccount(ctx, db);
    if (account is null) return Results.Unauthorized();
    if (string.IsNullOrWhiteSpace(input.Name) || input.Name.Length > 100 || !ValidIndex(input.Index) || string.IsNullOrWhiteSpace(input.Location) || input.Location.Length > 250)
        return Error("Enter a name, 8-digit index number, and location (up to 250 characters).");
    var photo = DecodePhoto(input.Photo);
    if (photo is null) return Error("Add a valid JPG, PNG, or WebP photo no larger than 2 MB.");
    var report = new Report { Name = input.Name.Trim(), Index = input.Index, Location = input.Location.Trim(), OwnerId = account.Id, Owner = account, Photo = photo.Value.Bytes, PhotoType = photo.Value.Type };
    db.Reports.Add(report);
    await db.SaveChangesAsync();
    return Results.Created($"/api/reports/{report.Id}", ReportDto(report, account));
}).RequireAuthorization();
app.MapPatch("/api/reports/{id}/resolve", async (string id, HttpContext ctx, AppDb db) =>
{
    var account = await CurrentAccount(ctx, db);
    if (account is null) return Results.Unauthorized();
    var report = await db.Reports.Include(r => r.Owner).SingleOrDefaultAsync(r => r.Id == id);
    if (report is null) return Results.NotFound();
    if (report.OwnerId != account.Id) return Results.Forbid();
    report.Status = "Solved";
    await db.SaveChangesAsync();
    return Results.Ok(ReportDto(report, account));
}).RequireAuthorization();
app.MapGet("/api/reports/{id}/photo", async (string id, HttpContext ctx, AppDb db) =>
{
    var account = await CurrentAccount(ctx, db);
    if (account is null) return Results.Unauthorized();
    var report = await db.Reports.SingleOrDefaultAsync(r => r.Id == id);
    if (report is null || report.Photo is null) return Results.NotFound();
    if (report.OwnerId != account.Id) return Results.Forbid();
    return Results.File(report.Photo, report.PhotoType!);
}).RequireAuthorization();
app.MapPut("/api/profile", async (ProfileInput input, HttpContext ctx, AppDb db) =>
{
    var account = await CurrentAccount(ctx, db);
    if (account is null) return Results.Unauthorized();
    if (string.IsNullOrWhiteSpace(input.Name) || input.Name.Length > 100 || !ValidIndex(input.Index)) return Error("Enter a name and an 8-digit index number.");
    account.Name = input.Name.Trim(); account.Index = input.Index;
    await db.SaveChangesAsync(); return Results.Ok(Profile(account));
}).RequireAuthorization();
app.MapPut("/api/preferences", async (Preferences input, HttpContext ctx, AppDb db) =>
{
    var account = await CurrentAccount(ctx, db);
    if (account is null) return Results.Unauthorized();
    account.Alerts = input.Alerts; account.EmailAlerts = input.Email; account.Dark = input.Dark;
    await db.SaveChangesAsync(); return Results.Ok(input);
}).RequireAuthorization();
app.MapPost("/api/reports/{id}/messages", async (string id, TextInput input, HttpContext ctx, AppDb db) =>
{
    var account = await CurrentAccount(ctx, db);
    if (account is null) return Results.Unauthorized();
    var report = await db.Reports.SingleOrDefaultAsync(r => r.Id == id);
    if (report is null) return Results.NotFound();
    if (report.OwnerId is null) return Error("This sample report has no finder account. Choose a report submitted by a student.");
    if (report.OwnerId == account.Id) return Error("You cannot message yourself.");
    if (report.Status == "Solved") return Error("This report has already been resolved.");
    if (string.IsNullOrWhiteSpace(input.Body) || input.Body.Length > 2000) return Error("Enter a message of up to 2,000 characters.");
    db.Messages.Add(new FinderMessage { ReportId = id, SenderId = account.Id, RecipientId = report.OwnerId, Body = input.Body.Trim() });
    await db.SaveChangesAsync(); return Results.NoContent();
}).RequireAuthorization();
app.MapGet("/api/messages", async (HttpContext ctx, AppDb db) =>
{
    var account = await CurrentAccount(ctx, db);
    if (account is null) return Results.Unauthorized();
    return Results.Ok(await db.Messages.Where(m => m.RecipientId == account.Id || m.SenderId == account.Id)
        .OrderByDescending(m => m.CreatedAt).Select(m => new { m.Id, m.ReportId, m.Body, m.CreatedAt, senderName = m.Sender.Name, incoming = m.RecipientId == account.Id }).ToListAsync());
}).RequireAuthorization();
app.MapPost("/api/feedback", async (TextInput input, HttpContext ctx, AppDb db) =>
{
    var account = await CurrentAccount(ctx, db);
    if (account is null) return Results.Unauthorized();
    if (string.IsNullOrWhiteSpace(input.Body) || input.Body.Length > 2000) return Error("Enter feedback of up to 2,000 characters.");
    db.Feedback.Add(new Feedback { AccountId = account.Id, Body = input.Body.Trim() });
    await db.SaveChangesAsync(); return Results.NoContent();
}).RequireAuthorization();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDb>();
    await db.Database.EnsureCreatedAsync();
    if (app.Environment.IsDevelopment())
    {
        await SeedData.ApplyAsync(db, scope.ServiceProvider.GetRequiredService<PasswordHasher<Account>>());
        app.Logger.LogInformation("Development seed data is ready (existing accounts and reports are preserved).");
    }
}
if (seedOnly)
{
    await app.DisposeAsync();
    return;
}
app.Run();

static bool ValidIndex(string? index) => index is not null && Regex.IsMatch(index, @"^\d{8}$");
static IResult Error(string message, int status = 400) => Results.Json(new { message }, statusCode: status);
static object Profile(Account a) => new { a.Name, a.Email, a.Index };
static string? UserId(HttpContext ctx) => ctx.User.FindFirstValue(ClaimTypes.NameIdentifier);
static Task<Account?> CurrentAccount(HttpContext ctx, AppDb db) { var id = UserId(ctx); return db.Accounts.SingleOrDefaultAsync(a => a.Id == id); }
static object ReportDto(Report r, Account? account) => new {
    r.Id, r.Name, index = r.OwnerId == account?.Id && account is not null ? r.Index : "****" + r.Index[^4..],
    r.Institution, r.Location, time = r.CreatedAt.ToString("dd MMM yyyy, HH:mm 'UTC'"), r.Status,
    own = account is not null && r.OwnerId == account.Id,
    photo = account is not null && r.OwnerId == account.Id && r.Photo is not null ? $"/api/reports/{r.Id}/photo" : null,
    finderName = r.Owner?.Name ?? "Sample report", sample = r.OwnerId is null
};
static Task SignIn(HttpContext ctx, Account a) => ctx.SignInAsync(
    CookieAuthenticationDefaults.AuthenticationScheme,
    new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, a.Id) }, CookieAuthenticationDefaults.AuthenticationScheme)),
    new AuthenticationProperties { IsPersistent = true });
static (byte[] Bytes, string Type)? DecodePhoto(string? data)
{
    if (data is null || data.Length > 2_800_000) return null;
    var match = Regex.Match(data, @"^data:(image/(?:jpeg|png|webp));base64,(.+)$");
    if (!match.Success) return null;
    try
    {
        var bytes = Convert.FromBase64String(match.Groups[2].Value);
        if (bytes.Length == 0 || bytes.Length > 2 * 1024 * 1024) return null;
        var type = match.Groups[1].Value;
        var valid = type switch {
            "image/png" => bytes.AsSpan().StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }),
            "image/jpeg" => bytes.AsSpan().StartsWith(new byte[] { 255, 216, 255 }),
            "image/webp" => bytes.Length >= 12 && System.Text.Encoding.ASCII.GetString(bytes, 0, 4) == "RIFF" && System.Text.Encoding.ASCII.GetString(bytes, 8, 4) == "WEBP",
            _ => false
        };
        return valid ? (bytes, type) : null;
    }
    catch (FormatException) { return null; }
}
