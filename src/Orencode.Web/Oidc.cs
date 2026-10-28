using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.WebUtilities;
namespace Orencode;
// Restricted public-client OIDC provider: authorization_code + mandatory PKCE S256.
// No implicit, password, refresh-token or dynamic-registration grants.
public static class Oidc
{
    public static void Map(WebApplication app,Store store)
    {
        var issuer=(app.Configuration["PublicUrl"]??"http://localhost:5080").TrimEnd('/');var keyPath=Path.Combine(store.DataDirectory,"oidc-signing.pem");var rsa=RSA.Create(2048);
        if(File.Exists(keyPath))rsa.ImportFromPem(File.ReadAllText(keyPath));else {File.WriteAllText(keyPath,rsa.ExportPkcs8PrivateKeyPem());if(!OperatingSystem.IsWindows())File.SetUnixFileMode(keyPath,UnixFileMode.UserRead|UnixFileMode.UserWrite);}
        var pub=rsa.ExportParameters(false);var kid=Security.Hash(Convert.ToBase64String(pub.Modulus!))[..16];
        string Sign(object payload)
        {
            var head=WebEncoders.Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(new{alg="RS256",typ="JWT",kid}));var body=WebEncoders.Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(payload));var input=head+"."+body;
            byte[] sig;lock(rsa)sig=rsa.SignData(Encoding.UTF8.GetBytes(input),HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1);return input+"."+WebEncoders.Base64UrlEncode(sig);
        }
        app.MapGet("/.well-known/openid-configuration",()=>new{issuer,authorization_endpoint=issuer+"/connect/authorize",token_endpoint=issuer+"/connect/token",userinfo_endpoint=issuer+"/connect/userinfo",jwks_uri=issuer+"/.well-known/jwks.json",response_types_supported=new[]{"code"},grant_types_supported=new[]{"authorization_code"},subject_types_supported=new[]{"public"},id_token_signing_alg_values_supported=new[]{"RS256"},code_challenge_methods_supported=new[]{"S256"},token_endpoint_auth_methods_supported=new[]{"none"},scopes_supported=new[]{"openid","profile"},claims_supported=new[]{"sub","name","roles","disciplines","rank"}});
        app.MapGet("/.well-known/jwks.json",()=>new{keys=new[]{new{kty="RSA",use="sig",alg="RS256",kid,n=WebEncoders.Base64UrlEncode(pub.Modulus!),e=WebEncoders.Base64UrlEncode(pub.Exponent!)}}});
        void Validate(JsonObject d,Dictionary<string,string> q)
        {
            string V(string key)=>q.GetValueOrDefault(key,"");var client=d.Rows("clients").FirstOrDefault(x=>x.S("id")==V("client_id")&&!x.B("disabled"));
            J.Check(client!=null&&client.S("redirectUri")==V("redirect_uri"),"Неверный клиент или redirect_uri");J.Check(V("response_type")=="code","Поддерживается response_type=code");
            var scopes=V("scope").Split(' ',StringSplitOptions.RemoveEmptyEntries);J.Check(scopes.Contains("openid")&&scopes.All(x=>x is "openid" or "profile"),"Неверный scope");
            J.Check(V("code_challenge_method")=="S256"&&Regex.IsMatch(V("code_challenge"),"^[A-Za-z0-9_-]{43}$"),"Обязателен PKCE S256");J.Check(V("state").Length<=1024&&V("nonce").Length<=1024,"Слишком длинные параметры");
        }
        app.MapGet("/connect/authorize",(HttpContext c,IAntiforgery csrf)=>
        {
            var q=c.Request.Query.ToDictionary(x=>x.Key,x=>x.Value.ToString());store.Read(d=>{Validate(d,q);return 0;});
            if(c.User.Identity?.IsAuthenticated!=true)return Results.Redirect("/?returnTo="+Uri.EscapeDataString(c.Request.Path+c.Request.QueryString));
            var clientName=store.Read(d=>{Security.Current(c,d);return d.Get("clients",q["client_id"]).S("name");});
            var token=csrf.GetAndStoreTokens(c);var fields=string.Join("",q.Select(x=>"<input type='hidden' name='"+Documents.H(x.Key)+"' value='"+Documents.H(x.Value)+"'>"));
            return Results.Content(Documents.Page("Войти через ФСП ID","<p>Приложение «"+Documents.H(clientName)+"» запрашивает ваш идентификатор"+(q["scope"].Split(' ').Contains("profile")?", ФИО, роли, дисциплины и разряд":"")+".</p><form method='post' action='/connect/authorize'>"+fields+"<input type='hidden' name='"+Documents.H(token.FormFieldName)+"' value='"+Documents.H(token.RequestToken!)+"'><button name='decision' value='allow'>Разрешить</button> <button name='decision' value='deny'>Отказать</button></form>"),"text/html; charset=utf-8");
        });
        app.MapPost("/connect/authorize",async(HttpContext c)=>
        {
            var form=await c.Request.ReadFormAsync();var q=form.ToDictionary(x=>x.Key,x=>x.Value.ToString());
            return store.Write(d=>
            {
                Validate(d,q);var a=Security.Current(c,d);var state=q.GetValueOrDefault("state","");if(q.GetValueOrDefault("decision")!="allow")return Results.Redirect(QueryHelpers.AddQueryString(q["redirect_uri"],new Dictionary<string,string?>{{"error","access_denied"},{"state",state}}));
                var code=Security.Secret();d.Add("codes",new{hash=Security.Hash(code),account=a.Id,client=q["client_id"],redirect=q["redirect_uri"],challenge=q["code_challenge"],scope=q["scope"],nonce=q.GetValueOrDefault("nonce",""),expires=DateTimeOffset.UtcNow.AddMinutes(2).ToString("O"),used=false});
                d.Add("consents",new{person=a.Person,client=q["client_id"],scope=q["scope"],version="oidc-v1",at=J.Now});Security.Audit(d,a,"oidc-consent",q["client_id"]);
                return Results.Redirect(QueryHelpers.AddQueryString(q["redirect_uri"],new Dictionary<string,string?>{{"code",code},{"state",state}}));
            });
        });
        bool Cors(HttpContext c)
        {
            string origin=c.Request.Headers.Origin.ToString();if(origin=="")return true;
            bool allowed=store.Read(d=>d.Rows("clients").Any(x=>!x.B("disabled")&&new Uri(x.S("redirectUri")).GetLeftPart(UriPartial.Authority)==origin));
            if(allowed){c.Response.Headers.AccessControlAllowOrigin=origin;c.Response.Headers.Vary="Origin";c.Response.Headers.AccessControlAllowMethods="GET, POST, OPTIONS";c.Response.Headers.AccessControlAllowHeaders="Content-Type, Authorization";}return allowed;
        }
        app.MapMethods("/connect/{endpoint}",["OPTIONS"],(HttpContext c,string endpoint)=>endpoint is "token" or "userinfo"&&Cors(c)?Results.NoContent():Results.StatusCode(403));
        app.MapPost("/connect/token",async(HttpContext c)=>
        {
            if(!Cors(c))return Results.StatusCode(403);var f=await c.Request.ReadFormAsync();
            try{return store.Write(d=>
            {
                J.Check(f["grant_type"]=="authorization_code","unsupported_grant_type");var code=d.Rows("codes").FirstOrDefault(x=>x.S("hash")==Security.Hash(f["code"].ToString()));J.Check(code!=null&&!code.B("used")&&DateTimeOffset.Parse(code.S("expires"))>DateTimeOffset.UtcNow,"invalid_grant");
                var client=d.Get("clients",code!.S("client"));J.Check(!client.B("disabled")&&code.S("client")==f["client_id"]&&code.S("redirect")==f["redirect_uri"],"invalid_grant");
                var verifier=f["code_verifier"].ToString();J.Check(Regex.IsMatch(verifier,"^[A-Za-z0-9._~-]{43,128}$")&&WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)))==code!.S("challenge"),"invalid_grant");
                var account=d.Get("accounts",code.S("account"));J.Check(!account.B("disabled"),"invalid_grant");code!["used"]=true;
                var raw=Security.Secret();d.Add("oidcTokens",new{hash=Security.Hash(raw),account=account.S("id"),client=client.S("id"),scope=code.S("scope"),expires=DateTimeOffset.UtcNow.AddMinutes(15).ToString("O")});
                var now=DateTimeOffset.UtcNow.ToUnixTimeSeconds();var payload=new JsonObject{{"iss",issuer},{"sub",account.S("person")},{"aud",client.S("id")},{"iat",now},{"exp",now+900}};if(code.S("nonce")!="")payload["nonce"]=code.S("nonce");
                return Results.Ok(new{access_token=raw,token_type="Bearer",expires_in=900,scope=code.S("scope"),id_token=Sign(payload)});
            });}catch(DomainError e){return Results.BadRequest(new{error=e.Message});}
        }).RequireRateLimiting("auth");
        app.MapGet("/connect/userinfo",(HttpContext c)=>
        {
            if(!Cors(c))return Results.StatusCode(403);
            return store.Read(d=>
            {
                var bearer=c.Request.Headers.Authorization.ToString();J.Check(bearer.StartsWith("Bearer "),"invalid_token",401);var t=d.Rows("oidcTokens").FirstOrDefault(x=>x.S("hash")==Security.Hash(bearer[7..])&&DateTimeOffset.Parse(x.S("expires"))>DateTimeOffset.UtcNow);J.Check(t!=null,"invalid_token",401);
                var account=d.Get("accounts",t!.S("account"));J.Check(!account.B("disabled")&&!d.Get("clients",t.S("client")).B("disabled"),"invalid_token",401);var p=d.Get("people",account.S("person"));var info=new JsonObject{{"sub",p.S("id")}};
                if(t.S("scope").Split(' ').Contains("profile")){info["name"]=p.S("name");info["roles"]=account["roles"]!.DeepClone();info["disciplines"]=p["disciplines"]!.DeepClone();info["rank"]=p.S("rank");}return Results.Ok(info);
            });
        });
    }
}
