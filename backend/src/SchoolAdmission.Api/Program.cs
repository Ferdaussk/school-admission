using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using SchoolAdmission.Api;

var builder = WebApplication.CreateBuilder(args);
var connStr = builder.Configuration.GetConnectionString("Default")
    ?? "Host=localhost;Port=5432;Database=school;Username=postgres;Password=postgres";
var holdSeconds = int.TryParse(builder.Configuration["HOLD_DURATION_SECONDS"], out var hs) && hs > 0 ? hs : 600;

builder.Services.AddDbContext<AppDb>(o => o.UseNpgsql(connStr));
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();
app.UseCors();

// Any unexpected problem becomes a friendly JSON error. Bad input never becomes a 500.
app.Use(async (ctx, next) =>
{
    try { await next(); }
    catch (BadHttpRequestException)
    {
        if (ctx.Response.HasStarted) return;
        ctx.Response.StatusCode = 400;
        await ctx.Response.WriteAsJsonAsync(new { error = "অনুরোধের তথ্য ঠিক নেই। আবার চেষ্টা করুন।" });
    }
    catch (Exception ex)
    {
        app.Logger.LogError(ex, "Unhandled error");
        if (ctx.Response.HasStarted) return;
        ctx.Response.StatusCode = 500;
        await ctx.Response.WriteAsJsonAsync(new { error = "সার্ভারে সমস্যা হয়েছে। একটু পরে আবার চেষ্টা করুন।" });
    }
});

// Create tables (wait for the database to be ready).
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDb>();
    for (var i = 0; ; i++)
    {
        try { db.Database.EnsureCreated(); break; }
        catch when (i < 30) { Thread.Sleep(2000); }
    }
}

const string BadId = "জন্ম নিবন্ধন নম্বর ঠিক ১৭ অঙ্কের হতে হবে।";

static IResult Err(int code, string msg) => Results.Json(new { error = msg }, statusCode: code);

static bool TryStudent(HttpRequest r, out string id)
{
    id = r.Headers["X-Student-Id"].ToString().Trim();
    return Regex.IsMatch(id, "^[0-9]{17}$");
}

static string Eff(Hold h, DateTime now) => h.Status == "HELD" && h.ExpiresAt <= now ? "EXPIRED" : h.Status;

static object ClassDto(ClassEntity c, int held, int confirmed) => new
{
    id = c.Id,
    name = c.Name,
    totalSeats = c.TotalSeats,
    heldSeats = held,
    confirmedSeats = confirmed,
    availableSeats = Math.Max(0, c.TotalSeats - held - confirmed)
};

// Locks the class row so every hold/confirm/cancel on the same class runs one at a time,
// even across several backend processes sharing the same database.
static async Task<ClassEntity?> LockClass(AppDb db, string id)
{
    var list = await db.Classes
        .FromSqlInterpolated($"SELECT * FROM classes WHERE \"Id\" = {id} FOR UPDATE")
        .ToListAsync();
    return list.FirstOrDefault();
}

// 1. Create class
app.MapPost("/classes", async (CreateClassRequest? body, AppDb db) =>
{
    var name = body?.Name?.Trim();
    if (string.IsNullOrEmpty(name) || name.Length > 100) return Err(400, "ক্লাসের নাম দিন (সর্বোচ্চ ১০০ অক্ষর)।");
    if (body!.TotalSeats is null or < 1 or > 100000) return Err(400, "সিট সংখ্যা ১ থেকে ১,০০,০০০ এর মধ্যে একটি পূর্ণ সংখ্যা হতে হবে।");
    if (await db.Classes.AnyAsync(c => c.Name.ToLower() == name.ToLower()))
        return Err(409, "এই নামে একটি ক্লাস আগেই আছে।");
    var c = new ClassEntity { Id = "c_" + Guid.NewGuid().ToString("N"), Name = name, TotalSeats = body.TotalSeats.Value };
    db.Classes.Add(c);
    await db.SaveChangesAsync();
    return Results.Json(new { id = c.Id, name = c.Name, totalSeats = c.TotalSeats }, statusCode: 201);
});

// 2. List classes
app.MapGet("/classes", async (AppDb db) =>
{
    var now = DateTime.UtcNow;
    var classes = await db.Classes.AsNoTracking().OrderBy(c => c.Name).ToListAsync();
    var counts = await db.Holds.AsNoTracking()
        .Where(h => h.Status == "CONFIRMED" || (h.Status == "HELD" && h.ExpiresAt > now))
        .GroupBy(h => new { h.ClassId, h.Status })
        .Select(g => new { g.Key.ClassId, g.Key.Status, N = g.Count() })
        .ToListAsync();
    return Results.Ok(classes.Select(c => ClassDto(c,
        counts.Where(x => x.ClassId == c.Id && x.Status == "HELD").Sum(x => x.N),
        counts.Where(x => x.ClassId == c.Id && x.Status == "CONFIRMED").Sum(x => x.N))));
});

// 3. Get class
app.MapGet("/classes/{classId}", async (string classId, AppDb db) =>
{
    var now = DateTime.UtcNow;
    var c = await db.Classes.AsNoTracking().FirstOrDefaultAsync(x => x.Id == classId);
    if (c is null) return Err(404, "ক্লাসটি খুঁজে পাওয়া যায়নি।");
    var held = await db.Holds.CountAsync(h => h.ClassId == classId && h.Status == "HELD" && h.ExpiresAt > now);
    var conf = await db.Holds.CountAsync(h => h.ClassId == classId && h.Status == "CONFIRMED");
    return Results.Ok(ClassDto(c, held, conf));
});

// 4. Apply (hold a seat)
app.MapPost("/classes/{classId}/holds", async (string classId, HttpRequest req, AppDb db) =>
{
    if (!TryStudent(req, out var sid)) return Err(400, BadId);
    await using var tx = await db.Database.BeginTransactionAsync();
    var cls = await LockClass(db, classId);
    if (cls is null) return Err(404, "ক্লাসটি খুঁজে পাওয়া যায়নি।");

    var now = DateTime.UtcNow;
    var already = await db.Holds.AnyAsync(h => h.ClassId == classId && h.StudentId == sid &&
        (h.Status == "CONFIRMED" || (h.Status == "HELD" && h.ExpiresAt > now)));
    if (already) return Err(409, "এই ক্লাসে আপনার আবেদন বা ভর্তি আগেই আছে।");

    var used = await db.Holds.CountAsync(h => h.ClassId == classId &&
        (h.Status == "CONFIRMED" || (h.Status == "HELD" && h.ExpiresAt > now)));
    if (used >= cls.TotalSeats) return Err(409, "দুঃখিত, এই ক্লাসে আর কোনো সিট খালি নেই।");

    var hold = new Hold
    {
        Id = "h_" + Guid.NewGuid().ToString("N"),
        ClassId = classId,
        StudentId = sid,
        Status = "HELD",
        CreatedAt = now,
        ExpiresAt = now.AddSeconds(holdSeconds)
    };
    db.Holds.Add(hold);
    await db.SaveChangesAsync();
    await tx.CommitAsync();
    return Results.Json(new { holdId = hold.Id, classId = hold.ClassId, studentId = hold.StudentId, status = hold.Status, expiresAt = hold.ExpiresAt }, statusCode: 201);
});

// 5. Get hold
app.MapGet("/holds/{holdId}", async (string holdId, AppDb db) =>
{
    var h = await db.Holds.AsNoTracking().FirstOrDefaultAsync(x => x.Id == holdId);
    if (h is null) return Err(404, "আবেদনটি খুঁজে পাওয়া যায়নি।");
    return Results.Ok(new { holdId = h.Id, classId = h.ClassId, studentId = h.StudentId, status = Eff(h, DateTime.UtcNow), expiresAt = h.ExpiresAt });
});

// 6. Confirm (pay fee)
app.MapPost("/holds/{holdId}/confirm", async (string holdId, HttpRequest req, AppDb db) =>
{
    if (!TryStudent(req, out var sid)) return Err(400, BadId);
    var pre = await db.Holds.AsNoTracking().FirstOrDefaultAsync(x => x.Id == holdId);
    if (pre is null) return Err(404, "আবেদনটি খুঁজে পাওয়া যায়নি।");

    await using var tx = await db.Database.BeginTransactionAsync();
    await LockClass(db, pre.ClassId);
    var h = await db.Holds.FirstAsync(x => x.Id == holdId);
    if (h.StudentId != sid) return Err(403, "এটি আপনার আবেদন নয়।");

    var status = Eff(h, DateTime.UtcNow);
    if (status == "CONFIRMED") return Results.Ok(new { holdId = h.Id, status = "CONFIRMED" }); // repeat confirm: no change
    if (status == "EXPIRED") return Err(409, "আবেদনের মেয়াদ শেষ হয়ে গেছে। আবার আবেদন করুন।");
    if (status == "CANCELLED") return Err(409, "এই আবেদন বাতিল করা হয়েছে।");

    h.Status = "CONFIRMED";
    await db.SaveChangesAsync();
    await tx.CommitAsync();
    return Results.Ok(new { holdId = h.Id, status = "CONFIRMED" });
});

// 7. Cancel
app.MapDelete("/holds/{holdId}", async (string holdId, HttpRequest req, AppDb db) =>
{
    if (!TryStudent(req, out var sid)) return Err(400, BadId);
    var pre = await db.Holds.AsNoTracking().FirstOrDefaultAsync(x => x.Id == holdId);
    if (pre is null) return Err(404, "আবেদনটি খুঁজে পাওয়া যায়নি।");

    await using var tx = await db.Database.BeginTransactionAsync();
    await LockClass(db, pre.ClassId);
    var h = await db.Holds.FirstAsync(x => x.Id == holdId);
    if (h.StudentId != sid) return Err(403, "এটি আপনার আবেদন নয়।");

    var status = Eff(h, DateTime.UtcNow);
    if (status == "CANCELLED") return Results.NoContent();
    if (status == "CONFIRMED") return Err(409, "ভর্তি নিশ্চিত হয়ে গেছে, এখন বাতিল করা যাবে না।");
    if (status == "EXPIRED") return Err(409, "আবেদনের মেয়াদ আগেই শেষ হয়ে গেছে।");

    h.Status = "CANCELLED";
    await db.SaveChangesAsync();
    await tx.CommitAsync();
    return Results.NoContent();
});

// 8. My holds
app.MapGet("/me/holds", async (HttpRequest req, AppDb db) =>
{
    if (!TryStudent(req, out var sid)) return Err(400, BadId);
    var now = DateTime.UtcNow;
    var rows = await (from h in db.Holds.AsNoTracking()
                      join c in db.Classes.AsNoTracking() on h.ClassId equals c.Id
                      where h.StudentId == sid
                      orderby h.CreatedAt descending
                      select new { h, c.Name }).ToListAsync();
    return Results.Ok(rows.Select(r => new
    {
        holdId = r.h.Id,
        classId = r.h.ClassId,
        className = r.Name,
        status = Eff(r.h, now),
        expiresAt = r.h.ExpiresAt
    }));
});

app.Run();

public record CreateClassRequest(string? Name, int? TotalSeats);
public partial class Program { }
