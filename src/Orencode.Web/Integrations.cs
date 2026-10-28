using System.Text.Json.Nodes;
namespace Orencode;
public static class Integrations
{
    private static Actor Authorize(HttpContext c,JsonObject d,string scope)
    {
        string raw=c.Request.Headers.Authorization.ToString();J.Check(raw.StartsWith("Bearer ",StringComparison.Ordinal),"Нужен Bearer token",401);
        var hash=Security.Hash(raw[7..]);var t=d.Rows("tokens").FirstOrDefault(x=>x.S("hash")==hash&&!x.B("revoked")&&x.Day("until")>=J.Today);J.Check(t!=null,"Токен недействителен",401);J.Check(t!.Strings("scopes").Contains(scope),"Недостаточно прав токена",403);return new(t.S("id"),"","integration");
    }
    public static void Map(WebApplication app,Store store)
    {
        app.MapGet("/api/integrations/events",(HttpContext c)=>store.Read(d=>{Authorize(c,d,"events:read");return d.Rows("events").Where(x=>x.S("status")!="draft").ToArray();}));
        app.MapGet("/api/integrations/results",(HttpContext c)=>store.Read(d=>{Authorize(c,d,"results:read");return d["results"]!.DeepClone();}));
        app.MapPost("/api/integrations/results",(HttpContext c,JsonObject j)=>store.Write(d=>{var a=Authorize(c,d,"results:write");var r=Commands.Publish(d,a,j);Security.Audit(d,a,"integration:publish",j.S("event"));return r;}));
    }
}
