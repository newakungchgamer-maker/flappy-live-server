using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

// Separate endpoints and session namespace: no Flappy token/role can unlock CookieLive.
public sealed class CookieDiscordAuth
{
    public const string Product = "cookielive", DefaultRole = "1559797818837962752";
    public record Options(string ClientId,string ClientSecret,string RedirectUri,string GuildId,string RoleId,string BotToken);
    public record Request(string? DeviceId,string? LoginId=null,string? PollToken=null,string? Token=null);
    public record Reply(bool Ok,string State,string Message,string Product="cookielive",string? LoginId=null,string? PollToken=null,string? AuthorizationUrl=null,string? Token=null);
    sealed class Pending(string device,string pollHash,DateTimeOffset expiry)
    {
        public readonly string Device=device,PollHash=pollHash;
        public readonly DateTimeOffset Expiry=expiry;
        public int Started;
        public Reply Result=new(true,"pending","รอยืนยันใน Discord");
    }
    record Session(string User,string Device,DateTimeOffset Expiry);
    readonly ConcurrentDictionary<string,Pending> pending=new();
    readonly ConcurrentDictionary<string,Session> sessions=new();
    readonly Options options;
    readonly HttpClient http;
    readonly TimeProvider clock;
    public CookieDiscordAuth(Options options,HttpClient http,TimeProvider? clock=null)
    {this.options=options;this.http=http;this.clock=clock??TimeProvider.System;}
    public static CookieDiscordAuth? FromEnvironment()
    {
        string E(string name)=>Environment.GetEnvironmentVariable(name)??"";
        var o=new Options(E("DISCORD_CLIENT_ID"),E("DISCORD_CLIENT_SECRET"),E("DISCORD_REDIRECT_URI"),E("DISCORD_GUILD_ID"),E("COOKIE_LIVE_DISCORD_ROLE_ID") is {Length:>0} role?role:DefaultRole,E("DISCORD_BOT_TOKEN"));
        if(new[]{o.ClientId,o.ClientSecret,o.RedirectUri,o.GuildId,o.RoleId,o.BotToken}.Any(string.IsNullOrWhiteSpace))return null;
        return new(o,new HttpClient{Timeout=TimeSpan.FromSeconds(15)});
    }
    static bool Valid(string? value,int max=256)=>!string.IsNullOrWhiteSpace(value) && value.Length<=max;
    static string Random(int bytes)=>Convert.ToHexString(RandomNumberGenerator.GetBytes(bytes)).ToLowerInvariant();
    static string Hash(string value)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    static Reply Denied(string text)=>new(false,"denied",text);
    public Reply Start(Request request)
    {
        Cleanup();
        if(!Valid(request.DeviceId))return Denied("ไม่พบรหัสเครื่อง");
        if(pending.Count>=5000)return Denied("มีผู้ใช้งานมาก กรุณาลองอีกครั้ง");
        string id="cookie_"+Random(24),secret=Random(32);
        pending[id]=new(request.DeviceId!,Hash(secret),clock.GetUtcNow().AddMinutes(5));
        string url="https://discord.com/oauth2/authorize?response_type=code&scope=identify&client_id="+Uri.EscapeDataString(options.ClientId)+"&redirect_uri="+Uri.EscapeDataString(options.RedirectUri)+"&state="+id;
        return new(true,"pending","เปิด Discord เพื่อยืนยันสิทธิ์",LoginId:id,PollToken:secret,AuthorizationUrl:url);
    }
    public Reply Status(Request request)
    {
        if(!Valid(request.LoginId) || !Valid(request.PollToken) || !pending.TryGetValue(request.LoginId!,out var login) || login.Device!=request.DeviceId || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(login.PollHash),Encoding.ASCII.GetBytes(Hash(request.PollToken!))))
            return Denied("ไม่พบรายการล็อกอิน");
        if(login.Expiry<=clock.GetUtcNow()){pending.TryRemove(request.LoginId!,out _);return new(false,"expired","หมดเวลาล็อกอิน กรุณาลองใหม่");}
        return Volatile.Read(ref login.Result);
    }
    public async Task<Reply> Callback(string state,string code,string error)
    {
        if(!pending.TryGetValue(state,out var login) || login.Expiry<=clock.GetUtcNow())return Denied("รายการล็อกอินหมดอายุ");
        if(Interlocked.CompareExchange(ref login.Started,1,0)!=0)return Denied("รายการนี้ถูกดำเนินการแล้ว");
        Reply result;
        if(!string.IsNullOrEmpty(error) || !Valid(code,2048))result=Denied("ยกเลิกการล็อกอิน กรุณาลองใหม่");
        else try
        {
            using var tokenRequest=new HttpRequestMessage(HttpMethod.Post,"https://discord.com/api/oauth2/token");
            tokenRequest.Headers.Authorization=new AuthenticationHeaderValue("Basic",Convert.ToBase64String(Encoding.UTF8.GetBytes(options.ClientId+":"+options.ClientSecret)));
            tokenRequest.Content=new FormUrlEncodedContent(new Dictionary<string,string>{{"grant_type","authorization_code"},{"code",code},{"redirect_uri",options.RedirectUri}});
            using var tokenResponse=await http.SendAsync(tokenRequest);tokenResponse.EnsureSuccessStatusCode();
            using var tokenJson=JsonDocument.Parse(await tokenResponse.Content.ReadAsStringAsync());
            string access=tokenJson.RootElement.GetProperty("access_token").GetString()??throw new InvalidOperationException();
            using var userRequest=new HttpRequestMessage(HttpMethod.Get,"https://discord.com/api/v10/users/@me");
            userRequest.Headers.Authorization=new AuthenticationHeaderValue("Bearer",access);
            using var userResponse=await http.SendAsync(userRequest);userResponse.EnsureSuccessStatusCode();
            using var userJson=JsonDocument.Parse(await userResponse.Content.ReadAsStringAsync());
            string user=userJson.RootElement.GetProperty("id").GetString()??throw new InvalidOperationException();
            if(!await HasRole(user))result=Denied("บัญชีนี้ยังไม่มี Role สำหรับ CookieLive");
            else if(login.Expiry<=clock.GetUtcNow())result=Denied("รายการล็อกอินหมดอายุ กรุณาลองใหม่");
            else
            {
                string token="cookie_"+Random(48);sessions[Hash(token)]=new(user,login.Device,clock.GetUtcNow().AddDays(30));
                result=new(true,"approved","ยืนยันสิทธิ์ CookieLive แล้ว",Token:token);
            }
        }
        catch(Exception e) when(e is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException or KeyNotFoundException)
        {result=new(false,"unavailable","ตรวจสอบ Discord ไม่สำเร็จ กรุณาลองใหม่");}
        Volatile.Write(ref login.Result,result);return result;
    }
    public async Task<Reply> Validate(Request request)
    {
        Cleanup();
        if(!Valid(request.Token) || !Valid(request.DeviceId) || !sessions.TryGetValue(Hash(request.Token!),out var session) || session.Device!=request.DeviceId || session.Expiry<=clock.GetUtcNow())return Denied("เซสชัน CookieLive หมดอายุ กรุณาล็อกอินใหม่");
        try
        {
            if(await HasRole(session.User))return new(true,"approved","ยืนยันสิทธิ์ CookieLive แล้ว");
            sessions.TryRemove(Hash(request.Token!),out _);return Denied("บัญชีนี้ไม่มี Role สำหรับ CookieLive แล้ว");
        }
        catch(Exception e) when(e is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {return new(false,"unavailable","ติดต่อ Discord ไม่ได้ กรุณาลองใหม่");}
    }
    public Reply Logout(Request request)
    {
        if(Valid(request.Token) && sessions.TryGetValue(Hash(request.Token!),out var session) && session.Device==request.DeviceId)sessions.TryRemove(Hash(request.Token!),out _);
        return new(true,"logged-out","ออกจากระบบแล้ว");
    }
    async Task<bool> HasRole(string user)
    {
        using var request=new HttpRequestMessage(HttpMethod.Get,$"https://discord.com/api/v10/guilds/{Uri.EscapeDataString(options.GuildId)}/members/{Uri.EscapeDataString(user)}");
        request.Headers.Authorization=new AuthenticationHeaderValue("Bot",options.BotToken);
        using var response=await http.SendAsync(request);
        if(response.StatusCode==HttpStatusCode.NotFound)return false;
        response.EnsureSuccessStatusCode();using var json=JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.TryGetProperty("roles",out var roles) && roles.ValueKind==JsonValueKind.Array && roles.EnumerateArray().Any(r=>r.GetString()==options.RoleId);
    }
    void Cleanup()
    {
        var now=clock.GetUtcNow();
        foreach(var item in pending)if(item.Value.Expiry<=now)pending.TryRemove(item.Key,out _);
        foreach(var item in sessions)if(item.Value.Expiry<=now)sessions.TryRemove(item.Key,out _);
    }
    public static void Map(WebApplication app,CookieDiscordAuth? service)
    {
        const string root="/api/v1/cookielive/discord";
        Reply Unavailable()=>new(false,"unavailable","เซิร์ฟเวอร์ยังไม่พร้อมสำหรับ CookieLive");
        app.MapGet(root+"/health",()=>Results.Ok(new{ok=service!=null,product=Product,protocol=1}));
        app.MapPost(root+"/start",(Request request,HttpResponse response)=>{response.Headers.CacheControl="no-store";return Results.Json(service?.Start(request)??Unavailable());});
        app.MapPost(root+"/status",(Request request,HttpResponse response)=>{response.Headers.CacheControl="no-store";return Results.Json(service?.Status(request)??Unavailable());});
        app.MapPost(root+"/validate",async (Request request,HttpResponse response)=>{response.Headers.CacheControl="no-store";return Results.Json(service==null?Unavailable():await service.Validate(request));});
        app.MapPost(root+"/logout",(Request request)=>Results.Json(service?.Logout(request)??Unavailable()));
    }
    public static string Page(Reply result)=>"<!doctype html><html lang='th'><meta charset='utf-8'><meta name='viewport' content='width=device-width,initial-scale=1'><title>CookieLive</title><body style='background:#fff2cf;color:#593c30;font:20px sans-serif;text-align:center;padding:15vh 24px'><h1>CookieLive</h1><h2>"+WebUtility.HtmlEncode(result.Message)+"</h2><p>กลับไปที่เกมเพื่อตรวจสถานะได้เลย</p></body></html>";
}
