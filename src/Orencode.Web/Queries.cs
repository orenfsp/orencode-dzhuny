using System.Text.Json.Nodes;
namespace Orencode;
public static class Queries
{
    public static JsonArray Rating(JsonObject d,int year,int discipline=-1)
    {
        var totals=new Dictionary<string,(double points,int podiums,int starts)>();
        foreach(var r in d.Rows("results"))
        {
            var e=d.Get("events",r.S("event"));if(e.Day("date").Year!=year||(discipline>=0&&e.I("discipline")!=discipline))continue;
            var person=r.S("person");if(Commands.Suspended(d.Get("people",person),J.Today))continue;
            var points=d.Rows("ratingRules").FirstOrDefault(x=>x.S("level")==e.S("level")&&x.I("place")==r.I("place"))?.N("points")??0;
            var old=totals.GetValueOrDefault(person);totals[person]=(old.points+points,old.podiums+(r.I("place")<=3?1:0),old.starts+1);
        }
        return System.Text.Json.JsonSerializer.SerializeToNode(totals.Select(x=>new{person=x.Key,points=Math.Round(x.Value.points*(x.Value.podiums>=2?1.2:1),2),coefficient=x.Value.podiums>=2?1.2:1,starts=x.Value.starts}).OrderByDescending(x=>x.points).ThenBy(x=>x.person))!.AsArray();
    }
    public static JsonArray Risks(JsonObject d)
    {
        var result=new JsonArray();foreach(var p in d.Rows("people"))foreach(var key in new[]{"rank","category"})if(DateOnly.TryParse(p.S(key+"Until"),out var end)&&end<=J.Today.AddDays(30))result.Add(new JsonObject{{"person",p.S("id")},{"type",key},{"text",p.S("name")+" · "+(key=="rank"?"разряд":"категория")+" до "+end.ToString("dd.MM.yyyy")},{"due",end.ToString("yyyy-MM-dd")}});
        foreach(var e in d.Rows("events").Where(x=>x.S("status")=="open"))
        {
            if(!d.Rows("assignments").Any(x=>x.S("event")==e.S("id")))result.Add(new JsonObject{{"event",e.S("id")},{"type","judges"},{"text",e.S("title")+" · не назначена коллегия"}});
            if(e.Day("deadline")<=J.Today.AddDays(3))result.Add(new JsonObject{{"event",e.S("id")},{"type","deadline"},{"text",e.S("title")+" · дедлайн "+e.S("deadline")}});
        }
        foreach(var a in d.Rows("applications").Where(x=>x.S("status")=="pending")){var missing=Commands.MissingDocuments(d,a);if(missing.Length>0)result.Add(new JsonObject{{"person",a.S("person")},{"application",a.S("id")},{"type","documents"},{"text",d.Get("people",a.S("person")).S("name")+" · не хватает: "+string.Join(", ",missing)}});}
        return result;
    }
    public static JsonArray Census(JsonObject d,int year)
    {
        var athletes=d.Rows("people").Where(p=>d.Rows("accounts").Any(a=>a.S("person")==p.S("id")&&a.Strings("roles").Contains("athlete"))).ToArray();var rows=new JsonArray();
        foreach(int y in new[]{year-1,year})
        {
            var entries=athletes.Where(p=>p.I("startYear",J.Today.Year)<=y).SelectMany(p=>p["disciplines"]!.AsArray().Select(disc=>new{org=p.S("org"),city=p.S("city"),discipline=disc!.GetValue<int>(),age=Commands.Age(p.Day("birthDate"),new DateOnly(y,12,31))<18?"До 18 лет":"18 лет и старше"}));
            foreach(var g in entries.GroupBy(x=>new{x.org,x.city,x.discipline,x.age}))rows.Add(System.Text.Json.JsonSerializer.SerializeToNode(new{year=y,g.Key.org,g.Key.city,g.Key.discipline,g.Key.age,count=g.Count()}));
        }
        return rows;
    }
    public static object State(JsonObject d,Actor a,int year,int disc)
    {
        var result=new JsonObject();bool fed=a.Role=="federation",readAll=fed||a.Role=="ministry";
        foreach(var t in new[]{"organizations","events","results","assignments","roster","ratingRules","standards","prizeRules"})result[t]=d[t]!.DeepClone();
        result["people"]=new JsonArray(d.Rows("people").Select(p=>
        {
            var o=new JsonObject();foreach(var k in new[]{"id","name","city","org","disciplines","rank","category","suspendedUntil"})o[k]=p[k]?.DeepClone();
            o["roles"]=System.Text.Json.JsonSerializer.SerializeToNode(d.Rows("accounts").Where(x=>x.S("person")==p.S("id")).SelectMany(x=>x.Strings("roles")).Distinct());
            o["age"]=Commands.Age(p.Day("birthDate"),J.Today);
            if(Security.Visible(d,a,p.S("id")))foreach(var k in new[]{"birthDate","sex","stage","startYear","rankUntil","categoryUntil","rankOrder","categoryOrder","guardian","rusadaDate"})o[k]=p[k]?.DeepClone();return (JsonNode)o;
        }).ToArray());
        foreach(var t in new[]{"applications","qualifications","consents","documents","assessments","certificates"})result[t]=new JsonArray(d.Rows(t).Where(x=>(Security.Visible(d,a,x.S("person"))||(t=="applications"&&a.Role=="judge"&&d.Rows("assignments").Any(v=>v.S("event")==x.S("event")&&v.S("person")==a.Person)))).Select(x=>(JsonNode)x.DeepClone()).ToArray());
        result["links"]=new JsonArray(d.Rows("links").Where(x=>fed||x.S("person")==a.Person||x.S("coach")==a.Person).Select(x=>(JsonNode)x.DeepClone()).ToArray());
        result["groups"]=new JsonArray(d.Rows("groups").Where(x=>fed||x.S("coach")==a.Person||x.Strings("members").Contains(a.Person)).Select(x=>(JsonNode)x.DeepClone()).ToArray());
        result["lessons"]=new JsonArray(d.Rows("lessons").Where(x=>result.Rows("groups").Any(g=>g.S("id")==x.S("group"))).Select(x=>(JsonNode)x.DeepClone()).ToArray());
        result["notifications"]=new JsonArray(d.Rows("notifications").Where(x=>x.S("person")==a.Person).Select(x=>(JsonNode)x.DeepClone()).ToArray());
        foreach(var t in new[]{"logs","outbox","petitions","clients"})result[t]=fed?d[t]!.DeepClone():new JsonArray();
        result["accounts"]=fed?new JsonArray(d.Rows("accounts").Select(x=>(JsonNode)new JsonObject{{"id",x.S("id")},{"person",x.S("person")},{"email",x.S("email")},{"roles",x["roles"]!.DeepClone()}}).ToArray()):new JsonArray();
        result["tokens"]=fed?new JsonArray(d.Rows("tokens").Select(x=>(JsonNode)new JsonObject{{"id",x.S("id")},{"name",x.S("name")},{"scopes",x["scopes"]!.DeepClone()},{"until",x.S("until")},{"revoked",x.B("revoked")}}).ToArray()):new JsonArray();
        result["rating"]=Rating(d,year,disc);result["census"]=readAll?Census(d,year):new JsonArray();result["risks"]=fed?Risks(d):new JsonArray();
        result["role"]=a.Role;result["me"]=a.Person;result["year"]=year;result["discipline"]=disc;result["myRoles"]=d.Get("accounts",a.Id)["roles"]!.DeepClone();result["disciplines"]=System.Text.Json.JsonSerializer.SerializeToNode(Seed.Disciplines);
        return result;
    }
}
