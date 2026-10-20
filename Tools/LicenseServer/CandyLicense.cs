using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Npgsql;
using NpgsqlTypes;

// CandyLive entitlements are independent of all other products on this host.
public record CandyEntitlement(string DiscordId, bool Active, DateTimeOffset? ExpiresAt, string Note);
public interface ICandyLicenseStore
{
    Task<CandyEntitlement?> Find(string id, CancellationToken ct);
    Task<IReadOnlyList<CandyEntitlement>> List(CancellationToken ct);
    Task Set(CandyEntitlement item, CancellationToken ct);
    Task<string> SigningKey(CancellationToken ct);
}

public sealed class CandyLicenseStore : ICandyLicenseStore
{
    readonly string connection;
    readonly SemaphoreSlim schemaGate = new(1);
    bool ready;
    public CandyLicenseStore(string connection)
    {
        if (Uri.TryCreate(connection, UriKind.Absolute, out var u) && (u.Scheme == "postgres" || u.Scheme == "postgresql"))
        {
            var credentials = u.UserInfo.Split(':', 2);
            if (credentials.Length != 2) throw new ArgumentException("Invalid database configuration");
            connection = new NpgsqlConnectionStringBuilder { Host = u.Host, Port = u.Port > 0 ? u.Port : 5432,
                Database = u.AbsolutePath.Trim('/'), Username = Uri.UnescapeDataString(credentials[0]),
                Password = Uri.UnescapeDataString(credentials[1]), SslMode = SslMode.Require }.ConnectionString;
        }
        this.connection = new NpgsqlConnectionStringBuilder(connection) { MaxPoolSize = 5, Timeout = 10, CommandTimeout = 15 }.ConnectionString;
    }
    async Task<NpgsqlConnection> Open(CancellationToken ct)
    {
        var c = new NpgsqlConnection(connection);
        try
        {
            await c.OpenAsync(ct);
            await schemaGate.WaitAsync(ct);
            try
            {
                if (!ready)
                {
                    await using var cmd = new NpgsqlCommand("""
                        CREATE SCHEMA IF NOT EXISTS candylive_private;
                        REVOKE ALL ON SCHEMA candylive_private FROM PUBLIC;
                        CREATE TABLE IF NOT EXISTS candylive_private.entitlements (
                            discord_id text PRIMARY KEY, active boolean NOT NULL, expires_at timestamptz,
                            note text NOT NULL DEFAULT '', updated_at timestamptz NOT NULL DEFAULT now());
                        CREATE TABLE IF NOT EXISTS candylive_private.audit (
                            id bigserial PRIMARY KEY, discord_id text NOT NULL, active boolean NOT NULL,
                            expires_at timestamptz, note text NOT NULL, changed_at timestamptz NOT NULL DEFAULT now());
                        CREATE TABLE IF NOT EXISTS candylive_private.signing_keys (
                            id integer PRIMARY KEY CHECK(id=1), pem text NOT NULL);
                        REVOKE ALL ON ALL TABLES IN SCHEMA candylive_private FROM PUBLIC;
                        REVOKE ALL ON ALL SEQUENCES IN SCHEMA candylive_private FROM PUBLIC;
                        """, c);
                    await cmd.ExecuteNonQueryAsync(ct); ready = true;
                }
            }
            finally { schemaGate.Release(); }
            return c;
        }
        catch { await c.DisposeAsync(); throw; }
    }
    public async Task<CandyEntitlement?> Find(string id, CancellationToken ct)
    {
        await using var c = await Open(ct);
        await using var cmd = new NpgsqlCommand("SELECT discord_id,active,expires_at,note FROM candylive_private.entitlements WHERE discord_id=@id", c);
        cmd.Parameters.AddWithValue("id", id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? Read(r) : null;
    }
    static CandyEntitlement Read(NpgsqlDataReader r) => new(r.GetString(0), r.GetBoolean(1),
        r.IsDBNull(2) ? null : r.GetFieldValue<DateTimeOffset>(2), r.GetString(3));
    public async Task<IReadOnlyList<CandyEntitlement>> List(CancellationToken ct)
    {
        await using var c = await Open(ct);
        await using var cmd = new NpgsqlCommand("SELECT discord_id,active,expires_at,note FROM candylive_private.entitlements ORDER BY updated_at DESC LIMIT 500", c);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var items = new List<CandyEntitlement>(); while (await r.ReadAsync(ct)) items.Add(Read(r)); return items;
    }
    public async Task Set(CandyEntitlement item, CancellationToken ct)
    {
        await using var c = await Open(ct); await using var tx = await c.BeginTransactionAsync(ct);
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO candylive_private.entitlements(discord_id,active,expires_at,note)
            VALUES(@id,@active,@expires,@note) ON CONFLICT(discord_id) DO UPDATE
            SET active=excluded.active,expires_at=excluded.expires_at,note=excluded.note,updated_at=now();
            INSERT INTO candylive_private.audit(discord_id,active,expires_at,note) VALUES(@id,@active,@expires,@note);
            """, c, tx);
        cmd.Parameters.AddWithValue("id", item.DiscordId); cmd.Parameters.AddWithValue("active", item.Active);
        cmd.Parameters.AddWithValue("expires", NpgsqlDbType.TimestampTz, (object?)item.ExpiresAt ?? DBNull.Value);
        cmd.Parameters.AddWithValue("note", item.Note);
        await cmd.ExecuteNonQueryAsync(ct); await tx.CommitAsync(ct);
    }
    public async Task<string> SigningKey(CancellationToken ct)
    {
        await using var c = await Open(ct);
        await using var get = new NpgsqlCommand("SELECT pem FROM candylive_private.signing_keys WHERE id=1", c);
        if (await get.ExecuteScalarAsync(ct) is string existing) return existing;
        using var rsa = RSA.Create(2048);
        await using var insert = new NpgsqlCommand("INSERT INTO candylive_private.signing_keys(id,pem) VALUES(1,@pem) ON CONFLICT DO NOTHING", c);
        insert.Parameters.AddWithValue("pem", rsa.ExportPkcs8PrivateKeyPem()); await insert.ExecuteNonQueryAsync(ct);
        return (string)(await get.ExecuteScalarAsync(ct))!;
    }
}

public sealed class CandyLicense
{
    public const string ClientId = "1557259899506008084";
    readonly ICandyLicenseStore store;
    readonly HttpClient discord;
    readonly SemaphoreSlim signingGate = new(1);
    string? signingPem;
    public CandyLicense(ICandyLicenseStore store, HttpClient discord) { this.store = store; this.discord = discord; }
    public static bool IdValid(string? s) => s != null && Regex.IsMatch(s, @"\A[0-9]{17,20}\z");
    public record CheckRequest(string? Nonce);
    public record Reply(bool Ok, string Code, string Payload = "", string Signature = "");
    public record Decision(int Status, Reply Body);
    public async Task<Decision> Check(string token, string? nonce, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(nonce) || !Regex.IsMatch(nonce, @"\A[a-f0-9]{32}\z"))
            return new(400, new(false, "invalid-request"));
        if (string.IsNullOrEmpty(token) || token.Length > 2048 || token.Any(char.IsWhiteSpace))
            return new(401, new(false, "invalid-session"));
        // Never accept a user-supplied Discord ID. Discord also confirms the app and scope.
        using var req = new HttpRequestMessage(HttpMethod.Get, "https://discord.com/api/v10/oauth2/@me");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var result = await discord.SendAsync(req, ct);
        if (result.StatusCode == HttpStatusCode.Unauthorized || result.StatusCode == HttpStatusCode.Forbidden)
            return new(401, new(false, "invalid-session"));
        if (!result.IsSuccessStatusCode) return new(503, new(false, "temporarily-unavailable"));
        using var json = JsonDocument.Parse(await result.Content.ReadAsStringAsync(ct));
        var root = json.RootElement;
        if (!root.TryGetProperty("application", out var app) || !app.TryGetProperty("id", out var appId) || appId.GetString() != ClientId ||
            !root.TryGetProperty("user", out var user) || !user.TryGetProperty("id", out var id) || !IdValid(id.GetString()) ||
            !root.TryGetProperty("scopes", out var scopes) || scopes.ValueKind != JsonValueKind.Array ||
            !scopes.EnumerateArray().Any(s => s.GetString() == "identify") ||
            !root.TryGetProperty("expires", out var expires) || !DateTimeOffset.TryParse(expires.GetString(), out var tokenExpiry) ||
            tokenExpiry <= DateTimeOffset.UtcNow)
            return new(401, new(false, "invalid-session"));
        string owner = id.GetString()!;
        var record = await store.Find(owner, ct);
        if (record == null || !record.Active) return new(403, new(false, "not-entitled"));
        var now = DateTimeOffset.UtcNow;
        if (record.ExpiresAt <= now) return new(403, new(false, "expired"));
        var end = now.AddMinutes(5);
        if (record.ExpiresAt.HasValue && record.ExpiresAt < end) end = record.ExpiresAt.Value;
        if (tokenExpiry < end) end = tokenExpiry;
        string payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(
            $"candylive-v1\n{owner}\n{nonce}\n{now.ToUnixTimeSeconds()}\n{end.ToUnixTimeSeconds()}"));
        using var rsa = RSA.Create(); rsa.ImportFromPem(await Key(ct));
        var sig = rsa.SignData(Encoding.UTF8.GetBytes(payload), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return new(200, new(true, "licensed", payload, Convert.ToBase64String(sig)));
    }
    async Task<string> Key(CancellationToken ct)
    {
        await signingGate.WaitAsync(ct);
        try { return signingPem ??= await store.SigningKey(ct); }
        finally { signingGate.Release(); }
    }
    public async Task<object> PublicKey(CancellationToken ct)
    {
        using var rsa = RSA.Create(); rsa.ImportFromPem(await Key(ct)); var p = rsa.ExportParameters(false);
        return new { modulus = Convert.ToBase64String(p.Modulus!), exponent = Convert.ToBase64String(p.Exponent!) };
    }
    public static bool IsAdmin(HttpRequest req, string? key)
    {
        if (string.IsNullOrEmpty(key) || key.Length < 24 || key == "change-this-admin-key") return false;
        var supplied = req.Headers["X-Admin-Key"].ToString();
        return CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(supplied)),
            SHA256.HashData(Encoding.UTF8.GetBytes(key)));
    }
    public static void Map(WebApplication app)
    {
        var database = Environment.GetEnvironmentVariable("SUPABASE_DATABASE_URL");
        ICandyLicenseStore? store = string.IsNullOrWhiteSpace(database) ? null : new CandyLicenseStore(database);
        var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(15) };
        Map(app,store,http,Environment.GetEnvironmentVariable("FLAPPY_ADMIN_KEY"));
    }
    public static void Map(WebApplication app, ICandyLicenseStore? store, HttpClient http, string? adminKey)
    {
        var service = store == null ? null : new CandyLicense(store, http);
        var slots = new SemaphoreSlim(12);
        app.Use(async (ctx,next) =>
        {
            if(ctx.Request.Path.StartsWithSegments("/api/v1/candylive")||ctx.Request.Path=="/candy/admin")
            {
                ctx.Response.Headers.CacheControl="no-store";
                ctx.Response.Headers["X-Content-Type-Options"]="nosniff";
                ctx.Response.Headers["X-Frame-Options"]="DENY";
                ctx.Response.Headers["Referrer-Policy"]="no-referrer";
                ctx.Response.Headers["Content-Security-Policy"]="default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'";
                var bodyLimit=ctx.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>();
                if(bodyLimit!=null&&!bodyLimit.IsReadOnly)bodyLimit.MaxRequestBodySize=4096;
            }
            await next(ctx);
        });
        app.MapGet("/api/v1/candylive/public-key", async (HttpContext ctx) =>
        {
            ctx.Response.Headers.CacheControl = "no-store";
            if (service == null) return Results.StatusCode(503);
            try { return Results.Ok(await service.PublicKey(ctx.RequestAborted)); }
            catch { return Results.StatusCode(503); }
        });
        app.MapPost("/api/v1/candylive/license/check", async (HttpContext ctx) =>
        {
            ctx.Response.Headers.CacheControl = "no-store";
            if (service == null) return Results.Json(new Reply(false, "server-not-configured"), statusCode: 503);
            if (!await slots.WaitAsync(0)) return Results.StatusCode(429);
            try
            {
                if (ctx.Request.ContentLength is > 1024) return Results.StatusCode(413);
                var body = await ctx.Request.ReadFromJsonAsync<CheckRequest>(ctx.RequestAborted);
                string auth = ctx.Request.Headers.Authorization.ToString();
                var decision = await service.Check(auth.StartsWith("Bearer ", StringComparison.Ordinal) ? auth[7..] : "", body?.Nonce, ctx.RequestAborted);
                return Results.Json(decision.Body, statusCode: decision.Status);
            }
            catch (JsonException) { return Results.StatusCode(400); }
            catch { return Results.Json(new Reply(false, "temporarily-unavailable"), statusCode: 503); }
            finally { slots.Release(); }
        });
        app.MapGet("/candy/admin", () => Results.Content(CandyAdminPage.Html, "text/html; charset=utf-8"));
        app.MapGet("/api/v1/candylive/admin/licenses", async (HttpContext ctx) =>
        {
            ctx.Response.Headers.CacheControl = "no-store";
            if (!IsAdmin(ctx.Request, adminKey)) return Results.Unauthorized();
            if (store == null) return Results.StatusCode(503);
            try { return Results.Ok(await store.List(ctx.RequestAborted)); } catch { return Results.StatusCode(503); }
        });
        app.MapPost("/api/v1/candylive/admin/licenses", async (HttpContext ctx) =>
        {
            ctx.Response.Headers.CacheControl = "no-store";
            if (!IsAdmin(ctx.Request, adminKey)) return Results.Unauthorized();
            if (store == null) return Results.StatusCode(503);
            try
            {
                if (ctx.Request.ContentLength is > 4096) return Results.StatusCode(413);
                var item = await ctx.Request.ReadFromJsonAsync<CandyEntitlement>(ctx.RequestAborted);
                if (item == null || !IdValid(item.DiscordId) || item.Note == null || item.Note.Length > 300 ||
                    (item.Active && item.ExpiresAt.HasValue && item.ExpiresAt <= DateTimeOffset.UtcNow))
                    return Results.BadRequest(new { code = "invalid-entitlement" });
                await store.Set(item, ctx.RequestAborted);
                return Results.Ok(new { ok = true });
            }
            catch (JsonException) { return Results.StatusCode(400); } catch { return Results.StatusCode(503); }
        });
    }
}
