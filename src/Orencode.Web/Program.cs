using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.RateLimiting;
using Orencode;
using Microsoft.AspNetCore.HttpOverrides;
using System.Net;

var builder=WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<Store>();
if (!builder.Environment.IsDevelopment()) J.Check(Uri.TryCreate(builder.Configuration["PublicUrl"],UriKind.Absolute,out var publicUri)&&publicUri.Scheme=="https","Production requires HTTPS PublicUrl");
builder.Services.Configure<ForwardedHeadersOptions>(o=>{o.ForwardedHeaders=ForwardedHeaders.XForwardedFor|ForwardedHeaders.XForwardedProto;if(IPAddress.TryParse(builder.Configuration["ReverseProxy:Address"],out var ip))o.KnownProxies.Add(ip);});
var dataDir=Path.GetDirectoryName(Path.GetFullPath(builder.Configuration["Database:Path"]??"data/orencode.db"))!;
Directory.CreateDirectory(Path.Combine(dataDir,"keys"));
builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataDir,"keys"))).SetApplicationName("Orencode");
builder.Services.AddAuthentication("Cookies").AddCookie("Cookies",o=>
{
    o.Cookie.Name="orencode-session";o.Cookie.HttpOnly=true;o.Cookie.SameSite=SameSiteMode.Lax;
    o.Cookie.SecurePolicy=builder.Environment.IsDevelopment()?CookieSecurePolicy.SameAsRequest:CookieSecurePolicy.Always;
    o.ExpireTimeSpan=TimeSpan.FromHours(8);o.SlidingExpiration=false;
    o.Events.OnRedirectToLogin=c=>{c.Response.StatusCode=401;return Task.CompletedTask;};
    o.Events.OnRedirectToAccessDenied=c=>{c.Response.StatusCode=403;return Task.CompletedTask;};
});
builder.Services.AddAuthorization();
builder.Services.AddAntiforgery(o=>{o.HeaderName="X-CSRF";o.Cookie.SecurePolicy=builder.Environment.IsDevelopment()?CookieSecurePolicy.SameAsRequest:CookieSecurePolicy.Always;});
builder.Services.AddRateLimiter(o=>{o.RejectionStatusCode=429;o.AddPolicy("auth",c=>RateLimitPartition.GetFixedWindowLimiter(c.Connection.RemoteIpAddress?.ToString()??"unknown",_=>new FixedWindowRateLimiterOptions{PermitLimit=30,Window=TimeSpan.FromMinutes(1),QueueLimit=0}));});
builder.Services.AddHostedService<DeliveryWorker>();
var app=builder.Build();var store=app.Services.GetRequiredService<Store>();Seed.Run(store,app.Configuration);
app.Use(async(c,next)=>
{
    c.Response.Headers["X-Content-Type-Options"]="nosniff";c.Response.Headers["X-Frame-Options"]="DENY";c.Response.Headers["Referrer-Policy"]="same-origin";
    c.Response.Headers["Content-Security-Policy"]="default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; connect-src 'self'; frame-ancestors 'none'; base-uri 'self'; form-action 'self'";
    if(c.Request.Path.StartsWithSegments("/api")||c.Request.Path.StartsWithSegments("/connect"))c.Response.Headers.CacheControl="no-store";
    try{await next();}
    catch(DomainError e){c.Response.StatusCode=e.Status;await c.Response.WriteAsJsonAsync(new{error=e.Message});}
    catch(AntiforgeryValidationException){c.Response.StatusCode=400;await c.Response.WriteAsJsonAsync(new{error="Обновите страницу: проверка CSRF не пройдена"});}
    catch(Exception e) when(e is JsonException or FormatException){c.Response.StatusCode=400;await c.Response.WriteAsJsonAsync(new{error="Неверный формат данных"});}
    catch(Exception e){app.Logger.LogError(e,"Request failed");c.Response.StatusCode=500;await c.Response.WriteAsJsonAsync(new{error="Ошибка сервера. Изменения не сохранены"});}
});
app.UseForwardedHeaders();
app.UseAuthentication();app.UseAuthorization();app.UseRateLimiter();
app.Use(async(c,next)=>
{
    if(c.Request.Method!="GET"&&c.Request.Method!="HEAD"&&c.Request.Method!="OPTIONS"&&
       ((c.Request.Path.StartsWithSegments("/api")&&!c.Request.Path.StartsWithSegments("/api/integrations"))||c.Request.Path=="/connect/authorize"))
        await c.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(c);
    await next();
});
app.UseDefaultFiles();app.UseStaticFiles();
app.MapGet("/health",()=>Results.Ok(new{status="ok"}));
app.MapGet("/api/auth/csrf",(HttpContext c,IAntiforgery f)=>new{token=f.GetAndStoreTokens(c).RequestToken});
app.MapGet("/api/auth/me",(HttpContext c)=>store.Read(d=>{var a=Security.Current(c,d);return new{person=a.Person,role=a.Role,roles=d.Get("accounts",a.Id).Strings("roles")};}));
app.MapPost("/api/auth/login",async(HttpContext c,JsonObject j)=>
{
    var account=store.Write(d=>
    {
        var a=d.Rows("accounts").FirstOrDefault(x=>x.S("email").Equals(j.S("email").Trim(),StringComparison.OrdinalIgnoreCase));
        if(a==null)return null;
        if(a.B("disabled")||(DateTimeOffset.TryParse(a.S("lockedUntil"),out var until)&&until>DateTimeOffset.UtcNow))return null;
        if(!Security.Verify(a.S("passwordHash"),j.S("password"))){a["failures"]=a.I("failures")+1;if(a.I("failures")>=5){a["lockedUntil"]=DateTimeOffset.UtcNow.AddMinutes(10).ToString("O");a["failures"]=0;}return null;}
        a["failures"]=0;a["lockedUntil"]="";return a.DeepClone().AsObject();
    });
    J.Check(account!=null,"Неверные данные входа или временная блокировка",401);await Security.SignIn(c,account!,account!.Strings("roles")[0]);return Results.Ok(new{ok=true});
}).RequireRateLimiting("auth");
app.MapPost("/api/auth/register",async(HttpContext c,JsonObject j)=>
{
    var a=store.Write(d=>
    {
        var email=j.Required("email").ToLowerInvariant();J.Check(System.Net.Mail.MailAddress.TryCreate(email,out var parsed)&&parsed.Address==email,"Проверьте email");J.Check(j.S("password").Length>=12&&j.S("password").Length<=200,"Пароль должен содержать от 12 до 200 символов");J.Check(j.B("consent"),"Нужно согласие на обработку данных");J.Check(!d.Rows("accounts").Any(x=>x.S("email")==email),"Аккаунт с таким адресом уже существует",409);
        var birth=j.Day("birthDate");J.Check(birth<J.Today&&birth>J.Today.AddYears(-110),"Проверьте дату рождения");if(birth>J.Today.AddYears(-18))J.Check(j.B("guardianConsent")&&j.S("guardian").Length>=3,"Нужно согласие законного представителя");
        var disc=j.I("discipline");J.Check(disc>=0&&disc<6,"Выберите дисциплину");J.Check(j.S("sex") is "male" or "female","Выберите пол");
        var p=d.Add("people",new{name=j.Required("name"),city=j.Required("city"),birthDate=birth.ToString("yyyy-MM-dd"),sex=j.S("sex"),disciplines=new[]{disc},org="",startYear=J.Today.Year,stage=0,rank="Без разряда",category="",guardian=j.S("guardian")});
        var account=d.Add("accounts",new{person=p.S("id"),email,passwordHash=Security.Password(j.S("password")),roles=new[]{"athlete"},disabled=false});
        d.Add("consents",new{person=p.S("id"),guardian=j.B("guardianConsent"),representative=j.S("guardian"),version="registration-v1",at=J.Now});Security.Audit(d,new(account.S("id"),p.S("id"),"athlete"),"registration");return account.DeepClone().AsObject();
    });await Security.SignIn(c,a,"athlete");return Results.Ok(new{ok=true});
}).RequireRateLimiting("auth");
app.MapPost("/api/auth/role",async(HttpContext c,JsonObject j)=>{var a=store.Read(d=>{var who=Security.Current(c,d);var ac=d.Get("accounts",who.Id);J.Check(ac.Strings("roles").Contains(j.S("role")),"Роль не назначена",403);return ac;});await Security.SignIn(c,a,j.S("role"));return Results.Ok(new{ok=true});});
app.MapPost("/api/auth/logout",async(HttpContext c)=>{await c.SignOutAsync("Cookies");return Results.Ok(new{ok=true});});
app.MapGet("/api/state",(HttpContext c,int? year,int? discipline)=>store.Read(d=>Queries.State(d,Security.Current(c,d),year??J.Today.Year,discipline??-1)));
app.MapPost("/api/commands/{name}",(HttpContext c,string name,JsonObject j)=>store.Write(d=>Commands.Run(d,Security.Current(c,d),name,j)));
Documents.Map(app,store);Oidc.Map(app,store);Integrations.Map(app,store);
app.MapGet("/", () => Results.Redirect("/index.html"));
app.MapFallback(()=>Results.NotFound(new{error="Маршрут не найден"}));
app.Run();
