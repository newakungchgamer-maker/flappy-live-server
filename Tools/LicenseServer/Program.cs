using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

string storePath = Environment.GetEnvironmentVariable("FLAPPY_LICENSE_STORE")
    ?? Path.Combine(AppContext.BaseDirectory, "license-store.json");
string adminKey = Environment.GetEnvironmentVariable("FLAPPY_ADMIN_KEY")
    ?? "change-this-admin-key";
ILicenseStore store = CreateLicenseStore(storePath);
DiscordAuthService? discordAuth = DiscordAuthService.TryCreate();

app.MapGet("/health", () => Results.Ok(new { ok = true, service = "flappy-live-license-server" }));

app.MapPost("/api/v1/licenses/activate", (ActivateRequest request) =>
{
    LicenseResult result = store.Activate(request.Code, request.DeviceId);
    return result.Ok ? Results.Ok(result) : Results.BadRequest(result);
});

app.MapPost("/api/v1/licenses/validate", (ValidateRequest request) =>
{
    LicenseResult result = store.Validate(request.Token, request.DeviceId);
    return result.Ok ? Results.Ok(result) : Results.BadRequest(result);
});

app.MapGet("/api/v1/admin/licenses", (HttpRequest request) =>
{
    if (!IsAdmin(request, adminKey)) return Results.Unauthorized();
    return Results.Ok(store.Summary());
});

app.MapPost("/api/v1/admin/licenses/revoke", (HttpRequest request, RevokeRequest body) =>
{
    if (!IsAdmin(request, adminKey)) return Results.Unauthorized();
    return store.Revoke(body.Code)
        ? Results.Ok(new { ok = true, message = "License revoked" })
        : Results.NotFound(new { ok = false, message = "License not found" });
});

app.MapGet("/api/v1/discord/login/start", (HttpRequest request) =>
{
    if (discordAuth is null)
        return Results.Problem("Discord login ยังไม่ได้ตั้งค่าในเซิร์ฟเวอร์");

    string deviceId = request.Query["deviceId"].ToString();
    if (string.IsNullOrWhiteSpace(deviceId))
        return Results.BadRequest(new { ok = false, message = "ไม่พบรหัสเครื่อง" });

    return Results.Ok(discordAuth.Start(deviceId));
});

app.MapGet("/api/v1/discord/callback", async (HttpRequest request) =>
{
    if (discordAuth is null)
        return Results.Content(DiscordAuthService.CallbackPage(false, "Discord login ยังไม่ได้ตั้งค่าในเซิร์ฟเวอร์"), "text/html");

    string loginId = request.Query["state"].ToString();
    string code = request.Query["code"].ToString();
    string error = request.Query["error"].ToString();
    string message = await discordAuth.HandleCallbackAsync(loginId, code, error);
    bool success = message.StartsWith("OK|", StringComparison.Ordinal);
    return Results.Content(DiscordAuthService.CallbackPage(success,
        success ? "ล็อกอินสำเร็จแล้ว กลับไปที่เกมได้เลย" : message), "text/html");
});

app.MapGet("/api/v1/discord/login/status", (HttpRequest request) =>
{
    if (discordAuth is null)
        return Results.Problem("Discord login ยังไม่ได้ตั้งค่าในเซิร์ฟเวอร์");

    string loginId = request.Query["loginId"].ToString();
    return Results.Ok(discordAuth.Status(loginId));
});

app.MapPost("/api/v1/discord/validate", async (DiscordValidateRequest request) =>
{
    if (discordAuth is null)
        return Results.Problem("Discord login ยังไม่ได้ตั้งค่าในเซิร์ฟเวอร์");

    DiscordValidationResult result = await discordAuth.ValidateAsync(request.Token, request.DeviceId);
    return result.Ok ? Results.Ok(result) : Results.BadRequest(result);
});

string listenPort = Environment.GetEnvironmentVariable("PORT") ?? "5057";
Console.WriteLine($"LicenseServer listening on http://127.0.0.1:{listenPort}");
Console.WriteLine(discordAuth is null
    ? "Discord login disabled: ตรวจค่า Client Secret และ Bot Token"
    : "Discord role login enabled.");
app.Run($"http://0.0.0.0:{listenPort}");

static bool IsAdmin(HttpRequest request, string expectedKey)
    => request.Headers.TryGetValue("X-Admin-Key", out var provided) && provided == expectedKey;

static ILicenseStore CreateLicenseStore(string filePath)
{
    string? databaseUrl = Environment.GetEnvironmentVariable("SUPABASE_DATABASE_URL");
    if (!string.IsNullOrWhiteSpace(databaseUrl))
    {
        Console.WriteLine("Using Supabase PostgreSQL license store.");
        return new PostgresLicenseStore(databaseUrl);
    }

    Console.WriteLine("Using local JSON license store.");
    return LicenseStore.Load(filePath);
}

interface ILicenseStore
{
    LicenseResult Activate(string? rawCode, string? deviceId);
    LicenseResult Validate(string? token, string? deviceId);
    bool Revoke(string? rawCode);
    object Summary();
}

sealed class LicenseStore : ILicenseStore
{
    readonly string path;
    readonly List<LicenseRecord> records;
    readonly object gate = new();

    LicenseStore(string path, List<LicenseRecord> records)
    {
        this.path = path;
        this.records = records;
    }

    public static LicenseStore Load(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? AppContext.BaseDirectory);
        var records = new List<LicenseRecord>();
        if (File.Exists(path))
        {
            try
            {
                var saved = JsonSerializer.Deserialize<List<LicenseRecord>>(File.ReadAllText(path));
                if (saved is not null) records = saved;
            }
            catch (JsonException)
            {
                Console.WriteLine($"Warning: could not read {path}; starting with an empty store.");
            }
        }

        string importPath = Path.Combine(Directory.GetParent(path)?.FullName ?? AppContext.BaseDirectory, "licenses.txt");
        var imported = File.Exists(importPath)
            ? File.ReadAllLines(importPath)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.Ordinal)
                .Select(ParseLicenseLine)
                .Where(record => record is not null)
                .Select(record => record!)
                .ToList()
            : new List<LicenseRecord>();

        int added = 0;
        foreach (LicenseRecord record in imported)
        {
            if (records.All(existing => existing.CodeHash != record.CodeHash))
            {
                records.Add(record);
                added++;
            }
        }

        var store = new LicenseStore(path, records);
        if (!File.Exists(path) || added > 0) store.Save();
        Console.WriteLine(imported.Count == 0
            ? "No licenses imported. Put licenses.txt beside the server executable."
            : $"Imported {added} new license(s) from {importPath}.");
        return store;
    }

    public LicenseResult Activate(string? rawCode, string? deviceId)
    {
        string code = Normalize(rawCode);
        if (code.Length == 0) return LicenseResult.Fail("กรุณาใส่รหัส");
        if (string.IsNullOrWhiteSpace(deviceId)) return LicenseResult.Fail("ไม่พบรหัสเครื่อง");

        lock (gate)
        {
            var record = records.FirstOrDefault(item => item.CodeHash == Hash(code));
            if (record is null || !record.Active) return LicenseResult.Fail("รหัสไม่ถูกต้องหรือถูกยกเลิก");
            if (record.ExpiresAtUtc is not null && record.ExpiresAtUtc <= DateTimeOffset.UtcNow)
                return LicenseResult.Fail("รหัสนี้หมดอายุแล้ว");
            if (record.ActivatedDevice is not null && record.ActivatedDevice != deviceId)
                return LicenseResult.Fail("รหัสนี้ถูกใช้งานกับเครื่องอื่นแล้ว");

            string token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
                .Replace('+', '-').Replace('/', '_').TrimEnd('=');
            record.ActivatedDevice = deviceId;
            record.ActivatedAtUtc = DateTimeOffset.UtcNow;
            if (record.Duration.Equals("30d", StringComparison.OrdinalIgnoreCase))
                record.ExpiresAtUtc = DateTimeOffset.UtcNow.AddDays(30);
            record.TokenHash = Hash(token);
            Save();
            return LicenseResult.Success(token, record.CodePreview, record.ExpiresAtUtc);
        }
    }

    public LicenseResult Validate(string? token, string? deviceId)
    {
        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(deviceId))
            return LicenseResult.Fail("ไม่พบข้อมูลการเปิดใช้งาน");

        lock (gate)
        {
            var record = records.FirstOrDefault(item => item.Active && item.TokenHash == Hash(token));
            if (record is null || record.ActivatedDevice != deviceId)
                return LicenseResult.Fail("สิทธิ์ใช้งานไม่ถูกต้อง");
            if (record.ExpiresAtUtc is not null && record.ExpiresAtUtc <= DateTimeOffset.UtcNow)
                return LicenseResult.Fail("สิทธิ์ใช้งานหมดอายุแล้ว");
            return LicenseResult.Success(null, record.CodePreview, record.ExpiresAtUtc);
        }
    }

    public bool Revoke(string? rawCode)
    {
        string code = Normalize(rawCode);
        lock (gate)
        {
            var record = records.FirstOrDefault(item => item.CodeHash == Hash(code));
            if (record is null) return false;
            record.Active = false;
            Save();
            return true;
        }
    }

    public object Summary()
    {
        lock (gate)
        {
            return records.Select(item => new
            {
                code = item.CodePreview,
                duration = item.Duration,
                active = item.Active,
                activated = item.ActivatedDevice is not null,
                activatedAtUtc = item.ActivatedAtUtc,
                expiresAtUtc = item.ExpiresAtUtc
            }).ToArray();
        }
    }

    void Save()
    {
        string tempPath = path + ".tmp";
        File.WriteAllText(tempPath, JsonSerializer.Serialize(records, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tempPath, path, true);
    }

    static string Normalize(string? value)
        => new string((value ?? string.Empty).Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();

    static LicenseRecord? ParseLicenseLine(string line)
    {
        string[] parts = line.Split('|', 2, StringSplitOptions.TrimEntries);
        string code = Normalize(parts[0]);
        if (code.Length == 0) return null;
        string duration = parts.Length > 1 && parts[1].Equals("30d", StringComparison.OrdinalIgnoreCase)
            ? "30d"
            : "permanent";
        return new LicenseRecord
        {
            CodeHash = Hash(code),
            CodePreview = code.Length <= 8 ? code : code[..8] + "...",
            Duration = duration
        };
    }

    static string Hash(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}

sealed class PostgresLicenseStore : ILicenseStore
{
    readonly string connectionString;

    public PostgresLicenseStore(string connectionString)
    {
        this.connectionString = NormalizeConnectionString(connectionString);
        EnsureSchema();
        ImportLicenseFile();
    }

    public LicenseResult Activate(string? rawCode, string? deviceId)
    {
        string code = Normalize(rawCode);
        if (code.Length == 0) return LicenseResult.Fail("กรุณาใส่รหัส");
        if (string.IsNullOrWhiteSpace(deviceId)) return LicenseResult.Fail("ไม่พบรหัสเครื่อง");

        using var connection = OpenConnection();
        using var read = new NpgsqlCommand(@"
            SELECT code_preview, duration, active, activated_device, activated_at, expires_at
            FROM public.licenses WHERE code_hash = @hash", connection);
        read.Parameters.AddWithValue("hash", Hash(code));
        using var reader = read.ExecuteReader();
        if (!reader.Read()) return LicenseResult.Fail("รหัสไม่ถูกต้องหรือถูกยกเลิก");

        string preview = reader.GetString(0);
        string duration = reader.GetString(1);
        bool active = reader.GetBoolean(2);
        string? activatedDevice = reader.IsDBNull(3) ? null : reader.GetString(3);
        DateTimeOffset? expiresAt = ReadTimestamp(reader, 5);
        if (!active) return LicenseResult.Fail("รหัสไม่ถูกต้องหรือถูกยกเลิก");
        if (expiresAt is not null && expiresAt <= DateTimeOffset.UtcNow)
            return LicenseResult.Fail("รหัสนี้หมดอายุแล้ว");
        if (activatedDevice is not null && activatedDevice != deviceId)
            return LicenseResult.Fail("รหัสนี้ถูกใช้งานกับเครื่องอื่นแล้ว");

        string token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (duration.Equals("30d", StringComparison.OrdinalIgnoreCase) && expiresAt is null)
            expiresAt = now.AddDays(30);

        reader.Close();
        using var update = new NpgsqlCommand(@"
            UPDATE public.licenses
            SET activated_device = @device, activated_at = @activated, expires_at = @expires, token_hash = @token
            WHERE code_hash = @hash", connection);
        update.Parameters.AddWithValue("device", deviceId);
        update.Parameters.AddWithValue("activated", now);
        update.Parameters.AddWithValue("expires", (object?)expiresAt ?? DBNull.Value);
        update.Parameters.AddWithValue("token", Hash(token));
        update.Parameters.AddWithValue("hash", Hash(code));
        update.ExecuteNonQuery();
        return LicenseResult.Success(token, preview, expiresAt);
    }

    public LicenseResult Validate(string? token, string? deviceId)
    {
        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(deviceId))
            return LicenseResult.Fail("ไม่พบข้อมูลการเปิดใช้งาน");

        using var connection = OpenConnection();
        using var command = new NpgsqlCommand(@"
            SELECT code_preview, activated_device, expires_at
            FROM public.licenses
            WHERE token_hash = @token AND active = TRUE", connection);
        command.Parameters.AddWithValue("token", Hash(token));
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return LicenseResult.Fail("สิทธิ์ใช้งานไม่ถูกต้อง");
        if (reader.GetString(1) != deviceId) return LicenseResult.Fail("สิทธิ์ใช้งานไม่ถูกต้อง");
        DateTimeOffset? expiresAt = ReadTimestamp(reader, 2);
        if (expiresAt is not null && expiresAt <= DateTimeOffset.UtcNow)
            return LicenseResult.Fail("สิทธิ์ใช้งานหมดอายุแล้ว");
        return LicenseResult.Success(null, reader.GetString(0), expiresAt);
    }

    public bool Revoke(string? rawCode)
    {
        string code = Normalize(rawCode);
        using var connection = OpenConnection();
        using var command = new NpgsqlCommand("UPDATE public.licenses SET active = FALSE WHERE code_hash = @hash", connection);
        command.Parameters.AddWithValue("hash", Hash(code));
        return command.ExecuteNonQuery() > 0;
    }

    public object Summary()
    {
        var rows = new List<LicenseSummary>();
        using var connection = OpenConnection();
        using var command = new NpgsqlCommand(@"
            SELECT code_preview, duration, active, activated_device, activated_at, expires_at
            FROM public.licenses ORDER BY created_at DESC", connection);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new LicenseSummary
            {
                Code = reader.GetString(0),
                Duration = reader.GetString(1),
                Active = reader.GetBoolean(2),
                Activated = !reader.IsDBNull(3),
                ActivatedAtUtc = ReadTimestamp(reader, 4),
                ExpiresAtUtc = ReadTimestamp(reader, 5)
            });
        }
        return rows;
    }

    void EnsureSchema()
    {
        using var connection = OpenConnection();
        using var command = new NpgsqlCommand(@"
            CREATE TABLE IF NOT EXISTS public.licenses (
                code_hash TEXT PRIMARY KEY,
                code_preview TEXT NOT NULL,
                duration TEXT NOT NULL DEFAULT 'permanent',
                active BOOLEAN NOT NULL DEFAULT TRUE,
                activated_device TEXT NULL,
                activated_at TIMESTAMPTZ NULL,
                expires_at TIMESTAMPTZ NULL,
                token_hash TEXT NULL,
                created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
            );
            CREATE INDEX IF NOT EXISTS licenses_token_hash_idx ON public.licenses(token_hash);
        ", connection);
        command.ExecuteNonQuery();
    }

    void ImportLicenseFile()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "licenses.txt");
        if (!File.Exists(path))
        {
            Console.WriteLine("No licenses.txt found; database import skipped.");
            return;
        }

        using var connection = OpenConnection();
        int imported = 0;
        foreach (string line in File.ReadAllLines(path))
        {
            LicenseImport? license = ParseLicenseLine(line);
            if (license is null) continue;
            using var command = new NpgsqlCommand(@"
                INSERT INTO public.licenses (code_hash, code_preview, duration)
                VALUES (@hash, @preview, @duration)
                ON CONFLICT (code_hash) DO NOTHING", connection);
            command.Parameters.AddWithValue("hash", license.CodeHash);
            command.Parameters.AddWithValue("preview", license.CodePreview);
            command.Parameters.AddWithValue("duration", license.Duration);
            imported += command.ExecuteNonQuery();
        }
        Console.WriteLine($"Imported {imported} new license(s) into Supabase.");
    }

    NpgsqlConnection OpenConnection()
    {
        var connection = new NpgsqlConnection(connectionString);
        connection.Open();
        return connection;
    }

    static string NormalizeConnectionString(string value)
    {
        string trimmed = value.Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out Uri? uri) ||
            (uri.Scheme != "postgres" && uri.Scheme != "postgresql"))
            return trimmed;

        string[] credentials = uri.UserInfo.Split(':', 2);
        if (credentials.Length != 2 || string.IsNullOrWhiteSpace(uri.Host))
            throw new ArgumentException("Supabase PostgreSQL URI ต้องมี username และ password ครบ");

        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = uri.Host,
            Port = uri.Port > 0 ? uri.Port : 5432,
            Database = uri.AbsolutePath.Trim('/'),
            Username = Uri.UnescapeDataString(credentials[0]),
            Password = Uri.UnescapeDataString(credentials[1]),
            SslMode = SslMode.Require
        };
        return builder.ConnectionString;
    }

    static DateTimeOffset? ReadTimestamp(NpgsqlDataReader reader, int index)
    {
        if (reader.IsDBNull(index)) return null;
        object value = reader.GetValue(index);
        return value switch
        {
            DateTimeOffset dateTimeOffset => dateTimeOffset,
            DateTime dateTime => new DateTimeOffset(DateTime.SpecifyKind(dateTime, DateTimeKind.Utc)),
            _ => null
        };
    }

    static LicenseImport? ParseLicenseLine(string line)
    {
        string[] parts = line.Split('|', 2, StringSplitOptions.TrimEntries);
        string code = Normalize(parts[0]);
        if (code.Length == 0) return null;
        string duration = parts.Length > 1 && parts[1].Equals("30d", StringComparison.OrdinalIgnoreCase)
            ? "30d"
            : "permanent";
        return new LicenseImport(Hash(code), code.Length <= 8 ? code : code[..8] + "...", duration);
    }

    static string Normalize(string? value)
        => new string((value ?? string.Empty).Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();

    static string Hash(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}

sealed record LicenseImport(string CodeHash, string CodePreview, string Duration);

sealed class LicenseSummary
{
    public string Code { get; set; } = string.Empty;
    public string Duration { get; set; } = "permanent";
    public bool Active { get; set; }
    public bool Activated { get; set; }
    public DateTimeOffset? ActivatedAtUtc { get; set; }
    public DateTimeOffset? ExpiresAtUtc { get; set; }
}

sealed class LicenseRecord
{
    public string CodeHash { get; set; } = string.Empty;
    public string CodePreview { get; set; } = string.Empty;
    public string Duration { get; set; } = "permanent";
    public bool Active { get; set; } = true;
    public string? ActivatedDevice { get; set; }
    public DateTimeOffset? ActivatedAtUtc { get; set; }
    public DateTimeOffset? ExpiresAtUtc { get; set; }
    public string? TokenHash { get; set; }
}

sealed record ActivateRequest(string Code, string DeviceId);
sealed record ValidateRequest(string Token, string DeviceId);
sealed record RevokeRequest(string Code);
sealed record DiscordValidateRequest(string Token, string DeviceId);

sealed record LicenseResult(bool Ok, string Message, string? Token, string? CodePreview, DateTimeOffset? ExpiresAtUtc)
{
    public static LicenseResult Success(string? token, string preview, DateTimeOffset? expiresAtUtc)
        => new(true, "เปิดใช้งานสำเร็จ", token, preview, expiresAtUtc);

    public static LicenseResult Fail(string message)
        => new(false, message, null, null, null);
}

sealed class DiscordAuthService
{
    static readonly HttpClient Http = new();
    readonly ConcurrentDictionary<string, PendingLogin> pending = new();
    readonly ConcurrentDictionary<string, DiscordSession> sessions = new();
    readonly string clientId;
    readonly string clientSecret;
    readonly string redirectUri;
    readonly string guildId;
    readonly string roleId;
    readonly string botToken;

    DiscordAuthService(string clientId, string clientSecret, string redirectUri, string guildId, string roleId, string botToken)
    {
        this.clientId = clientId;
        this.clientSecret = clientSecret;
        this.redirectUri = redirectUri;
        this.guildId = guildId;
        this.roleId = roleId;
        this.botToken = botToken;
    }

    public static DiscordAuthService? TryCreate()
    {
        string[] values =
        {
            Environment.GetEnvironmentVariable("DISCORD_CLIENT_ID") ?? string.Empty,
            Environment.GetEnvironmentVariable("DISCORD_CLIENT_SECRET") ?? string.Empty,
            Environment.GetEnvironmentVariable("DISCORD_REDIRECT_URI") ?? string.Empty,
            Environment.GetEnvironmentVariable("DISCORD_GUILD_ID") ?? string.Empty,
            Environment.GetEnvironmentVariable("DISCORD_LICENSE_ROLE_ID") ?? string.Empty,
            Environment.GetEnvironmentVariable("DISCORD_BOT_TOKEN") ?? string.Empty
        };

        if (values.Any(string.IsNullOrWhiteSpace))
        {
            Console.WriteLine("Discord login disabled. Set DISCORD_CLIENT_ID, DISCORD_CLIENT_SECRET, DISCORD_REDIRECT_URI, DISCORD_GUILD_ID, DISCORD_LICENSE_ROLE_ID, and DISCORD_BOT_TOKEN to enable it.");
            return null;
        }

        Console.WriteLine("Discord role login enabled.");
        return new DiscordAuthService(values[0], values[1], values[2], values[3], values[4], values[5]);
    }

    public object Start(string deviceId)
    {
        Cleanup();
        string loginId = RandomToken(24);
        pending[loginId] = new PendingLogin(deviceId, DateTimeOffset.UtcNow);

        string authorizationUrl =
            "https://discord.com/oauth2/authorize?response_type=code" +
            "&client_id=" + Uri.EscapeDataString(clientId) +
            "&scope=identify" +
            "&state=" + Uri.EscapeDataString(loginId) +
            "&redirect_uri=" + Uri.EscapeDataString(redirectUri);

        return new { ok = true, loginId, authorizationUrl };
    }

    public object Status(string loginId)
    {
        if (!pending.TryGetValue(loginId, out PendingLogin? login))
            return new { ok = false, state = "missing", message = "ไม่พบรายการล็อกอิน หรือรายการหมดอายุแล้ว" };
        if (DateTimeOffset.UtcNow - login.CreatedAt > TimeSpan.FromMinutes(10))
            return new { ok = false, state = "expired", message = "รายการล็อกอินหมดอายุแล้ว" };
        if (!login.Completed)
            return new { ok = true, state = "pending", message = "รอการยืนยันจาก Discord" };
        return new { ok = login.Approved, state = login.Approved ? "approved" : "denied", token = login.SessionToken, message = login.Message };
    }

    public async Task<string> HandleCallbackAsync(string loginId, string code, string error)
    {
        if (!pending.TryGetValue(loginId, out PendingLogin? login))
            return "ไม่พบรายการล็อกอิน หรือรายการหมดอายุแล้ว";
        if (DateTimeOffset.UtcNow - login.CreatedAt > TimeSpan.FromMinutes(10))
            return "รายการล็อกอินหมดอายุแล้ว";
        if (!string.IsNullOrWhiteSpace(error))
        {
            login.Complete(false, null, "ผู้ใช้ยกเลิกการล็อกอิน Discord");
            return "ผู้ใช้ยกเลิกการล็อกอิน Discord";
        }
        if (string.IsNullOrWhiteSpace(code))
        {
            login.Complete(false, null, "ไม่ได้รับรหัสยืนยันจาก Discord");
            return "ไม่ได้รับรหัสยืนยันจาก Discord";
        }

        try
        {
            string discordAccessToken = await ExchangeCodeAsync(code);
            string userId = await GetDiscordUserIdAsync(discordAccessToken);
            if (!await HasLicenseRoleAsync(userId))
            {
                login.Complete(false, null, "บัญชี Discord นี้ยังไม่มี Role GAME LICENSED");
                return "บัญชี Discord นี้ยังไม่มี Role GAME LICENSED";
            }

            string sessionToken = RandomToken(48);
            sessions[Hash(sessionToken)] = new DiscordSession(userId, login.DeviceId, DateTimeOffset.UtcNow.AddDays(30));
            login.Complete(true, sessionToken, "Discord ยืนยันสิทธิ์แล้ว");
            return "OK|approved";
        }
        catch (Exception exception)
        {
            Console.WriteLine($"Discord OAuth error: {exception.Message}");
            login.Complete(false, null, "ตรวจสอบ Discord ไม่สำเร็จ กรุณาลองใหม่");
            return "ตรวจสอบ Discord ไม่สำเร็จ กรุณาลองใหม่";
        }
    }

    public async Task<DiscordValidationResult> ValidateAsync(string token, string deviceId)
    {
        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(deviceId))
            return DiscordValidationResult.Fail("ไม่พบข้อมูล Discord login");
        string tokenHash = Hash(token);
        if (!sessions.TryGetValue(tokenHash, out DiscordSession? session))
            return DiscordValidationResult.Fail("Discord session ไม่ถูกต้องหรือหมดอายุ");
        if (session.DeviceId != deviceId || session.ExpiresAtUtc <= DateTimeOffset.UtcNow)
        {
            sessions.TryRemove(tokenHash, out _);
            return DiscordValidationResult.Fail("Discord session หมดอายุแล้ว");
        }
        if (!await HasLicenseRoleAsync(session.DiscordUserId))
            return DiscordValidationResult.Fail("บัญชี Discord ไม่มี Role GAME LICENSED แล้ว");
        return DiscordValidationResult.Success(session.ExpiresAtUtc);
    }

    async Task<string> ExchangeCodeAsync(string code)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://discord.com/api/oauth2/token");
        string credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{clientId}:{clientSecret}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);
        request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = redirectUri
        });
        using HttpResponseMessage response = await Http.SendAsync(request);
        string json = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Discord token exchange failed ({(int)response.StatusCode}): {json}");
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty("access_token").GetString()
            ?? throw new InvalidOperationException("Discord ไม่ส่ง access token");
    }

    async Task<string> GetDiscordUserIdAsync(string accessToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://discord.com/api/v10/users/@me");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using HttpResponseMessage response = await Http.SendAsync(request);
        string json = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Discord user lookup failed ({(int)response.StatusCode}): {json}");
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty("id").GetString()
            ?? throw new InvalidOperationException("Discord ไม่ส่ง user id");
    }

    async Task<bool> HasLicenseRoleAsync(string userId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"https://discord.com/api/v10/guilds/{Uri.EscapeDataString(guildId)}/members/{Uri.EscapeDataString(userId)}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bot", botToken);
        using HttpResponseMessage response = await Http.SendAsync(request);
        if (response.StatusCode == HttpStatusCode.NotFound) return false;
        string json = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Discord bot member lookup failed ({(int)response.StatusCode}): {json}");
        using JsonDocument document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("roles", out JsonElement roles)) return false;
        return roles.EnumerateArray().Any(role => role.GetString() == roleId);
    }

    void Cleanup()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        foreach (var pair in pending.Where(item => now - item.Value.CreatedAt > TimeSpan.FromMinutes(10)).ToArray())
            pending.TryRemove(pair.Key, out _);
        foreach (var pair in sessions.Where(item => item.Value.ExpiresAtUtc <= now).ToArray())
            sessions.TryRemove(pair.Key, out _);
    }

    static string RandomToken(int byteCount)
        => Convert.ToBase64String(RandomNumberGenerator.GetBytes(byteCount))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');

    static string Hash(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    public static string CallbackPage(bool success, string message)
    {
        string safeMessage = WebUtility.HtmlEncode(message);
        string color = success ? "#5CFF6A" : "#FF6B6B";
        return $"<!doctype html><html><head><meta charset='utf-8'><title>Flappy Live</title></head>" +
            $"<body style='background:#092b3d;color:white;font-family:Arial;text-align:center;padding-top:20vh'>" +
            $"<h1 style='color:{color}'>{safeMessage}</h1><p>ปิดหน้านี้แล้วกลับไปที่เกมได้เลย</p></body></html>";
    }

    sealed class PendingLogin
    {
        public string DeviceId { get; }
        public DateTimeOffset CreatedAt { get; }
        public bool Completed { get; private set; }
        public bool Approved { get; private set; }
        public string? SessionToken { get; private set; }
        public string Message { get; private set; } = "กำลังรอการยืนยัน";

        public PendingLogin(string deviceId, DateTimeOffset createdAt)
        {
            DeviceId = deviceId;
            CreatedAt = createdAt;
        }

        public void Complete(bool approved, string? sessionToken, string message)
        {
            lock (this)
            {
                Completed = true;
                Approved = approved;
                SessionToken = sessionToken;
                Message = message;
            }
        }
    }

    sealed record DiscordSession(string DiscordUserId, string DeviceId, DateTimeOffset ExpiresAtUtc);
}

sealed record DiscordValidationResult(bool Ok, string Message, DateTimeOffset? ExpiresAtUtc)
{
    public static DiscordValidationResult Success(DateTimeOffset expiresAtUtc)
        => new(true, "Discord ยืนยันสิทธิ์แล้ว", expiresAtUtc);

    public static DiscordValidationResult Fail(string message)
        => new(false, message, null);
}
