using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
namespace Orencode;
public record Actor(string Id,string Person,string Role);
public static class Security
{
    public static readonly string[] Roles=["federation","athlete","coach","judge","ministry"];
    public static string Hash(string raw)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
    public static string Secret()=>Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
    public static string Password(string p)=>new PasswordHasher<string>().HashPassword("account",p);
    public static bool Verify(string hash,string p)=>new PasswordHasher<string>().VerifyHashedPassword("account",hash,p)!=PasswordVerificationResult.Failed;
    public static Actor Current(HttpContext c,JsonObject d)
    {
        var id=c.User.FindFirstValue(ClaimTypes.NameIdentifier);
        var a=d.Rows("accounts").FirstOrDefault(x=>x.S("id")==id);
        J.Check(a!=null&&!a.B("disabled"),"Войдите в аккаунт",401);
        var role=c.User.FindFirstValue("active_role")??"";
        J.Check(a!.Strings("roles").Contains(role),"Роль больше недоступна",403);
        return new(a.S("id"),a.S("person"),role);
    }
    public static Task SignIn(HttpContext c,JsonObject a,string role)=>c.SignInAsync("Cookies",new ClaimsPrincipal(new ClaimsIdentity(new[]{new Claim(ClaimTypes.NameIdentifier,a.S("id")),new Claim("active_role",role)},"Cookies")));
    public static void Require(Actor a,params string[] roles)=>J.Check(roles.Contains(a.Role),"Нет доступа для активной роли",403);
    public static bool Visible(JsonObject d,Actor a,string person)=>a.Role=="federation"||a.Person==person||(a.Role=="coach"&&d.Rows("links").Any(x=>x.S("coach")==a.Person&&x.S("person")==person&&x.S("status")=="active"));
    public static bool CanAthlete(JsonObject d,Actor a,string person,int discipline)=>a.Role=="federation"||(a.Role=="athlete"&&a.Person==person)||(a.Role=="coach"&&d.Rows("links").Any(x=>x.S("coach")==a.Person&&x.S("person")==person&&x.I("discipline")==discipline&&x.S("status")=="active"));
    public static void Audit(JsonObject d,Actor a,string text,string entity="")=>d.Add("logs",new{actor=a.Id,role=a.Role,text,entity,at=J.Now});
    public static void Notify(JsonObject d,string person,string text)
    {
        d.Add("notifications",new{person,text,read=false,at=J.Now});
        foreach(var account in d.Rows("accounts").Where(x=>x.S("person")==person))d.Add("outbox",new{email=account.S("email"),text,status="pending",attempts=0,at=J.Now});
    }
    public static void NotifyFederation(JsonObject d,string text) {foreach(var a in d.Rows("accounts").Where(x=>x.Strings("roles").Contains("federation")))Notify(d,a.S("person"),text);}
}
