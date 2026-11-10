using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace SchoolAdmission.Tests;

// Needs a running PostgreSQL. Connection string comes from ConnectionStrings__Default.
public class AdmissionTests
{
    static AdmissionTests()
    {
        if (Environment.GetEnvironmentVariable("ConnectionStrings__Default") is null)
            Environment.SetEnvironmentVariable("ConnectionStrings__Default",
                "Host=localhost;Port=5432;Database=school_test;Username=postgres;Password=postgres");
    }

    static WebApplicationFactory<Program> Start(int holdSeconds = 600)
    {
        Environment.SetEnvironmentVariable("HOLD_DURATION_SECONDS", holdSeconds.ToString());
        var f = new WebApplicationFactory<Program>();
        f.CreateClient(); // builds the app now, so it reads the env var above
        return f;
    }

    static string Sid(int n) => (20150000000000000L + n).ToString(); // 17 digits

    static async Task<string> NewClass(HttpClient c, int seats)
    {
        var r = await c.PostAsJsonAsync("/classes", new { name = "Test " + Guid.NewGuid().ToString("N"), totalSeats = seats });
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        return (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
    }

    static Task<HttpResponseMessage> Send(HttpClient c, HttpMethod m, string url, string? sid)
    {
        var req = new HttpRequestMessage(m, url);
        if (sid != null) req.Headers.Add("X-Student-Id", sid);
        return c.SendAsync(req);
    }

    static Task<HttpResponseMessage> Apply(HttpClient c, string classId, string sid) =>
        Send(c, HttpMethod.Post, $"/classes/{classId}/holds", sid);

    static async Task<JsonElement> Json(HttpResponseMessage r) => await r.Content.ReadFromJsonAsync<JsonElement>();

    static Task<JsonElement> GetClass(HttpClient c, string id) => c.GetFromJsonAsync<JsonElement>($"/classes/{id}")!;

    [Fact]
    public async Task Concurrent_applications_never_exceed_total_seats()
    {
        using var f = Start();
        var c = f.CreateClient();
        var classId = await NewClass(c, 10);

        var results = await Task.WhenAll(Enumerable.Range(1, 60)
            .Select(i => Task.Run(async () => (await Apply(c, classId, Sid(i))).StatusCode)));

        Assert.Equal(10, results.Count(s => s == HttpStatusCode.Created));
        Assert.Equal(50, results.Count(s => s == HttpStatusCode.Conflict));
        var cls = await GetClass(c, classId);
        Assert.Equal(10, cls.GetProperty("heldSeats").GetInt32());
        Assert.Equal(0, cls.GetProperty("availableSeats").GetInt32());
    }

    [Fact]
    public async Task Same_student_cannot_hold_twice_and_others_cannot_confirm()
    {
        using var f = Start();
        var c = f.CreateClient();
        var classId = await NewClass(c, 5);

        var first = await Apply(c, classId, Sid(1));
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Apply(c, classId, Sid(1))).StatusCode);

        var holdId = (await Json(first)).GetProperty("holdId").GetString();
        var other = await Send(c, HttpMethod.Post, $"/holds/{holdId}/confirm", Sid(2));
        Assert.Equal(HttpStatusCode.Forbidden, other.StatusCode);
    }

    [Fact]
    public async Task Confirming_many_times_counts_the_seat_once()
    {
        using var f = Start();
        var c = f.CreateClient();
        var classId = await NewClass(c, 5);
        var holdId = (await Json(await Apply(c, classId, Sid(1)))).GetProperty("holdId").GetString();

        var results = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => Task.Run(() => Send(c, HttpMethod.Post, $"/holds/{holdId}/confirm", Sid(1)))));
        Assert.All(results, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));

        var cls = await GetClass(c, classId);
        Assert.Equal(1, cls.GetProperty("confirmedSeats").GetInt32());
        Assert.Equal(0, cls.GetProperty("heldSeats").GetInt32());
    }

    [Fact]
    public async Task Expired_hold_frees_the_seat_and_cannot_be_confirmed()
    {
        using var f = Start(holdSeconds: 1);
        var c = f.CreateClient();
        var classId = await NewClass(c, 1);
        var holdId = (await Json(await Apply(c, classId, Sid(1)))).GetProperty("holdId").GetString();
        Assert.Equal(HttpStatusCode.Conflict, (await Apply(c, classId, Sid(2))).StatusCode);

        await Task.Delay(1600);

        var hold = await c.GetFromJsonAsync<JsonElement>($"/holds/{holdId}");
        Assert.Equal("EXPIRED", hold.GetProperty("status").GetString());
        Assert.Equal(1, (await GetClass(c, classId)).GetProperty("availableSeats").GetInt32());
        Assert.Equal(HttpStatusCode.Conflict, (await Send(c, HttpMethod.Post, $"/holds/{holdId}/confirm", Sid(1))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await Apply(c, classId, Sid(2))).StatusCode);
    }

    [Fact]
    public async Task Bad_input_gives_4xx()
    {
        using var f = Start();
        var c = f.CreateClient();
        var classId = await NewClass(c, 2);

        Assert.Equal(HttpStatusCode.BadRequest, (await Apply(c, classId, "123")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(c, HttpMethod.Get, "/me/holds", null)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/classes", new { name = "X", totalSeats = -5 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/classes", new { name = "", totalSeats = 5 })).StatusCode);
        var broken = new StringContent("{not json", Encoding.UTF8, "application/json");
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsync("/classes", broken)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Apply(c, "c_missing", Sid(1))).StatusCode);
    }

    [Fact]
    public async Task Cancel_frees_seat_and_me_holds_lists_it()
    {
        using var f = Start();
        var c = f.CreateClient();
        var classId = await NewClass(c, 1);
        var holdId = (await Json(await Apply(c, classId, Sid(1)))).GetProperty("holdId").GetString();

        Assert.Equal(HttpStatusCode.NoContent, (await Send(c, HttpMethod.Delete, $"/holds/{holdId}", Sid(1))).StatusCode);
        Assert.Equal(1, (await GetClass(c, classId)).GetProperty("availableSeats").GetInt32());

        var mine = await (await Send(c, HttpMethod.Get, "/me/holds", Sid(1))).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("CANCELLED", mine[0].GetProperty("status").GetString());
    }
}
