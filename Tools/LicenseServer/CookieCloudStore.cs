using System.Text.Json;
using CookieDash;
using Npgsql;
using NpgsqlTypes;

// Durable PostgreSQL only. Never fall back to Render's ephemeral filesystem.
public sealed class CookieCloudStore
{
    public static readonly JsonSerializerOptions Json=new(){IncludeFields=true,PropertyNameCaseInsensitive=false};
    readonly string connectionString;
    readonly SemaphoreSlim schemaGate=new(1,1);
    bool ready;
    public CookieCloudStore(string connection)
    {
        if(Uri.TryCreate(connection,UriKind.Absolute,out var u) && (u.Scheme=="postgres" || u.Scheme=="postgresql"))
        {
            var credentials=u.UserInfo.Split(':',2);
            if(credentials.Length!=2)throw new ArgumentException("Invalid database configuration");
            connection=new NpgsqlConnectionStringBuilder{Host=u.Host,Port=u.Port>0?u.Port:5432,Database=u.AbsolutePath.Trim('/'),
                Username=Uri.UnescapeDataString(credentials[0]),Password=Uri.UnescapeDataString(credentials[1]),SslMode=SslMode.Require}.ConnectionString;
        }
        var config=new NpgsqlConnectionStringBuilder(connection){MaxPoolSize=10,Timeout=10,CommandTimeout=20};
        connectionString=config.ConnectionString;
    }
    public async Task EnsureSchema(CancellationToken ct)
    {
        if(ready)return;
        await schemaGate.WaitAsync(ct);
        try
        {
            if(ready)return;
            await using var c=new NpgsqlConnection(connectionString);await c.OpenAsync(ct);
            await using var cmd=new NpgsqlCommand("""
                CREATE SCHEMA IF NOT EXISTS cookielive_private;
                REVOKE ALL ON SCHEMA cookielive_private FROM PUBLIC;
                CREATE TABLE IF NOT EXISTS cookielive_private.history_accounts(
                    owner_id text PRIMARY KEY, bootstrap_id text, history jsonb NOT NULL, updated_at timestamptz NOT NULL DEFAULT now());
                CREATE TABLE IF NOT EXISTS cookielive_private.history_matches(
                    owner_id text NOT NULL REFERENCES cookielive_private.history_accounts(owner_id),
                    match_id text NOT NULL, PRIMARY KEY(owner_id,match_id));
                CREATE TABLE IF NOT EXISTS cookielive_private.history_photos(
                    owner_id text NOT NULL REFERENCES cookielive_private.history_accounts(owner_id),
                    viewer_id text NOT NULL, png bytea NOT NULL, PRIMARY KEY(owner_id,viewer_id));
                REVOKE ALL ON ALL TABLES IN SCHEMA cookielive_private FROM PUBLIC;
                """,c);
            await cmd.ExecuteNonQueryAsync(ct);ready=true;
        }
        finally{schemaGate.Release();}
    }
    public static void Validate(CookieCloudSyncRequest r)
    {
        if(r==null || string.IsNullOrWhiteSpace(r.deviceId) || r.deviceId.Length>256 || r.matches==null || r.matches.Count>10)
            throw new ArgumentException("Invalid sync");
        if(r.hasBootstrap && (!CookieCloudRules.Valid(r.bootstrap,out _) || r.bootstrap.completedMatches<1 ||
           r.bootstrapId==null || r.bootstrapId.Length!=64 || !r.bootstrapId.All(Uri.IsHexDigit)))throw new ArgumentException("Invalid migration");
        var ids=new HashSet<string>(StringComparer.Ordinal);
        foreach(var m in r.matches)if(!CookieCloudRules.IsDelta(m) || !ids.Add(m.appliedIds[0]))throw new ArgumentException("Invalid match");
    }
    public async Task<CookieCloudSyncReply> Sync(string owner,CookieCloudSyncRequest request,CancellationToken ct)
    {
        Validate(request);await EnsureSchema(ct);
        await using var c=new NpgsqlConnection(connectionString);await c.OpenAsync(ct);
        await using var tx=await c.BeginTransactionAsync(ct);
        await using(var insert=new NpgsqlCommand("INSERT INTO cookielive_private.history_accounts(owner_id,history) VALUES(@owner,@empty) ON CONFLICT DO NOTHING",c,tx))
        {
            insert.Parameters.AddWithValue("owner",owner);insert.Parameters.AddWithValue("empty",NpgsqlDbType.Jsonb,JsonSerializer.Serialize(new CookieHistoryData(),Json));
            await insert.ExecuteNonQueryAsync(ct);
        }
        CookieHistoryData data;string? seed;
        // Serialize all devices for this Discord account. Different accounts remain independent.
        await using(var read=new NpgsqlCommand("SELECT history::text,bootstrap_id FROM cookielive_private.history_accounts WHERE owner_id=@owner FOR UPDATE",c,tx))
        {
            read.Parameters.AddWithValue("owner",owner);
            await using var reader=await read.ExecuteReaderAsync(ct);await reader.ReadAsync(ct);
            data=JsonSerializer.Deserialize<CookieHistoryData>(reader.GetString(0),Json)!;seed=reader.IsDBNull(1)?null:reader.GetString(1);
        }
        if(!CookieCloudRules.Valid(data,out _))throw new InvalidOperationException("Stored history invalid");
        var reply=new CookieCloudSyncReply{ok=true,ownerId=owner,bootstrapState="none"};
        if(request.hasBootstrap)
        {
            if(seed==request.bootstrapId)reply.bootstrapState="accepted";
            else if(data.completedMatches==0 && seed==null)
            {
                data=request.bootstrap;seed=request.bootstrapId;reply.bootstrapState="accepted";
                foreach(var id in data.appliedIds)await InsertMatch(id);
            }
            else reply.bootstrapState="conflict";
        }
        foreach(var delta in request.matches)
        {
            string id=delta.appliedIds[0];
            if(await InsertMatch(id))CookieCloudRules.Merge(data,delta);
            reply.acknowledged.Add(id);
        }
        string json=JsonSerializer.Serialize(data,Json);
        if(System.Text.Encoding.UTF8.GetByteCount(json)>CookieCloudRules.MaxFileBytes)throw new InvalidOperationException("History capacity reached");
        await using(var write=new NpgsqlCommand("UPDATE cookielive_private.history_accounts SET history=@data,bootstrap_id=@seed,updated_at=now() WHERE owner_id=@owner",c,tx))
        {
            write.Parameters.AddWithValue("owner",owner);write.Parameters.AddWithValue("data",NpgsqlDbType.Jsonb,json);
            write.Parameters.AddWithValue("seed",NpgsqlDbType.Text,(object?)seed??DBNull.Value);await write.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);reply.history=data;return reply;
        async Task<bool> InsertMatch(string id)
        {
            await using var cmd=new NpgsqlCommand("INSERT INTO cookielive_private.history_matches(owner_id,match_id) VALUES(@owner,@id) ON CONFLICT DO NOTHING",c,tx);
            cmd.Parameters.AddWithValue("owner",owner);cmd.Parameters.AddWithValue("id",id);
            return await cmd.ExecuteNonQueryAsync(ct)==1;
        }
    }
    public static void Map(WebApplication app,CookieDiscordAuth? auth)
    {
        string? connection=Environment.GetEnvironmentVariable("SUPABASE_DATABASE_URL");
        CookieCloudStore? store=string.IsNullOrWhiteSpace(connection)?null:new(connection);
        app.MapGet("/api/v1/cookielive/history/health",async (HttpContext context)=>
        {
            if(store==null || auth==null)return Results.Json(new{protocol=1,configured=false,ready=false},statusCode:503);
            try{await store.EnsureSchema(context.RequestAborted);return Results.Ok(new{protocol=1,configured=true,ready=true});}
            catch(Exception e) when(e is NpgsqlException or TimeoutException){return Results.Json(new{protocol=1,configured=true,ready=false},statusCode:503);}
        });
        app.MapPost("/api/v1/cookielive/history/photos",async (HttpContext context)=>
        {
            context.Response.Headers.CacheControl="no-store";
            if(auth==null || store==null)return Results.StatusCode(503);
            var feature=context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>();
            if(feature is {IsReadOnly:false})feature.MaxRequestBodySize=2*1024*1024;
            string header=context.Request.Headers.Authorization.ToString();
            if(!header.StartsWith("Bearer ",StringComparison.Ordinal))return Results.Unauthorized();
            try
            {
                var r=await JsonSerializer.DeserializeAsync<CookieCloudPhotoRequest>(context.Request.Body,Json,context.RequestAborted);
                if(r==null)return Results.BadRequest();
                string? owner=await auth.StatisticsOwner(header[7..],r.deviceId);if(owner==null)return Results.Unauthorized();
                return Results.Json(await store.Photos(owner,r,context.RequestAborted),Json);
            }
            catch(Exception e) when(e is JsonException or ArgumentException or BadHttpRequestException or FormatException){return Results.BadRequest();}
            catch(Exception e) when(e is NpgsqlException or TimeoutException){return Results.StatusCode(503);}
        });
        app.MapPost("/api/v1/cookielive/history/sync",async (HttpContext context)=>
        {
            context.Response.Headers.CacheControl="no-store";
            if(auth==null || store==null)return Results.StatusCode(503);
            var feature=context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>();
            if(feature is {IsReadOnly:false})feature.MaxRequestBodySize=20*1024*1024;
            string header=context.Request.Headers.Authorization.ToString();
            if(!header.StartsWith("Bearer ",StringComparison.Ordinal))return Results.Unauthorized();
            try
            {
                var request=await JsonSerializer.DeserializeAsync<CookieCloudSyncRequest>(context.Request.Body,Json,context.RequestAborted);
                if(request==null)return Results.BadRequest();
                string? owner=await auth.StatisticsOwner(header[7..],request.deviceId);
                if(owner==null)return Results.Unauthorized();
                Validate(request);
                return Results.Json(await store.Sync(owner,request,context.RequestAborted),Json);
            }
            catch(Exception e) when(e is JsonException or ArgumentException or BadHttpRequestException){return Results.BadRequest(new{error="invalid_sync"});}
            catch(Exception e) when(e is NpgsqlException or TimeoutException or InvalidOperationException or OverflowException)
            {context.RequestServices.GetRequiredService<ILogger<CookieCloudStore>>().LogWarning("CookieLive statistics unavailable ({Type})",e.GetType().Name);return Results.StatusCode(503);}
        });
    }
    public static bool ValidPng(byte[] b)=>b.Length>=33 && b.Length<=65536 &&
        b[0]==137 && b[1]==80 && b[2]==78 && b[3]==71 && b[4]==13 && b[5]==10 && b[6]==26 && b[7]==10 &&
        b[12]==73 && b[13]==72 && b[14]==68 && b[15]==82 &&
        b[16]==0 && b[17]==0 && b[18]==0 && b[19]==96 && b[20]==0 && b[21]==0 && b[22]==0 && b[23]==96;
    public async Task<CookieCloudPhotoReply> Photos(string owner,CookieCloudPhotoRequest request,CancellationToken ct)
    {
        bool Id(string id)=>!string.IsNullOrWhiteSpace(id) && id.Length<=64;
        if(request.ids==null || request.photos==null || request.ids.Count>20 || request.photos.Count>20 || request.ids.Any(id=>!Id(id)))
            throw new ArgumentException("Invalid photo request");
        var uploads=new Dictionary<string,byte[]>();
        foreach(var p in request.photos)
        {
            if(p==null || !Id(p.viewerId) || p.png==null || p.png.Length>90000)throw new ArgumentException("Invalid thumbnail");
            var bytes=Convert.FromBase64String(p.png);if(!ValidPng(bytes))throw new ArgumentException("Invalid thumbnail");
            uploads[p.viewerId]=bytes;
        }
        await EnsureSchema(ct);await using var c=new NpgsqlConnection(connectionString);await c.OpenAsync(ct);
        var reply=new CookieCloudPhotoReply{ok=true,ownerId=owner};
        foreach(var p in uploads)
        {
            await using var cmd=new NpgsqlCommand("""
                INSERT INTO cookielive_private.history_photos(owner_id,viewer_id,png)
                SELECT @owner,@viewer,@png WHERE EXISTS (
                    SELECT 1 FROM cookielive_private.history_accounts a, jsonb_array_elements(a.history->'players') p
                    WHERE a.owner_id=@owner AND p->>'viewerId'=@viewer)
                ON CONFLICT(owner_id,viewer_id) DO UPDATE SET png=excluded.png
                """,c);
            cmd.Parameters.AddWithValue("owner",owner);cmd.Parameters.AddWithValue("viewer",p.Key);cmd.Parameters.AddWithValue("png",p.Value);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await using(var cmd=new NpgsqlCommand("SELECT viewer_id,png FROM cookielive_private.history_photos WHERE owner_id=@owner AND viewer_id=ANY(@ids)",c))
        {
            cmd.Parameters.AddWithValue("owner",owner);cmd.Parameters.AddWithValue("ids",request.ids.ToArray());
            await using var reader=await cmd.ExecuteReaderAsync(ct);
            while(await reader.ReadAsync(ct))reply.photos.Add(new CookieCloudPhoto{viewerId=reader.GetString(0),png=Convert.ToBase64String((byte[])reader.GetValue(1))});
        }
        return reply;
    }
}
