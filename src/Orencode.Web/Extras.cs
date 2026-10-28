using System.Text.Json.Nodes;
namespace Orencode;
public static class Extras
{
    public static object Run(JsonObject d,Actor a,string name,JsonObject j)
    {
        void Fed()=>Security.Require(a,"federation");
        switch(name)
        {
            case "group":
            {
                Security.Require(a,"coach","federation");var coach=a.Role=="coach"?a.Person:j.Required("coach");var disc=j.I("discipline");var members=j.Strings("members");
                J.Check(disc>=0&&disc<6&&j.I("stage")>=0&&j.I("stage")<=3,"Проверьте дисциплину и этап");d.Get("organizations",j.Required("org"));
                J.Check(d.Rows("accounts").Any(x=>x.S("person")==coach&&x.Strings("roles").Contains("coach")),"У выбранного участника нет роли тренера");
                foreach(var p in members)J.Check(d.Rows("links").Any(x=>x.S("coach")==coach&&x.S("person")==p&&x.I("discipline")==disc&&x.S("status")=="active"),"Нет подтверждённой связи с участником группы");
                return d.Add("groups",new{name=j.Required("name"),coach,discipline=disc,stage=j.I("stage"),org=j.S("org"),members}).DeepClone();
            }
            case "lesson":
            {
                Security.Require(a,"coach","federation");var g=d.Get("groups",j.Required("group"));J.Check(a.Role=="federation"||g.S("coach")==a.Person,"Чужая группа",403);
                J.Check(j.Day("date")<=J.Today,"Нельзя отметить будущее занятие");var present=j.Strings("present");J.Check(present.All(g.Strings("members").Contains),"Спортсмен не входит в группу");
                foreach(var p in present)J.Check(d.Rows("links").Any(x=>x.S("coach")==g.S("coach")&&x.S("person")==p&&x.S("status")=="active"&&x.I("discipline")==g.I("discipline")),"Связь с участником прекращена");
                var mode=j.S("format");J.Check(mode is "online" or "offline","Неизвестный формат");if(mode=="online")J.Check(Uri.TryCreate(j.S("evidence"),UriKind.Absolute,out var uri)&&uri.Scheme=="https","Для онлайн-занятия нужна HTTPS-ссылка на подтверждение");
                return d.Add("lessons",new{group=g.S("id"),date=j.S("date"),topic=j.Required("topic"),format=mode,evidence=j.S("evidence"),present,at=J.Now}).DeepClone();
            }
            case "standard":
            {
                Fed();J.Check(j.S("comparison") is "gte" or "lte"&&j.N("threshold")>=0&&j.I("stage")>=0&&j.I("stage")<=3&&j.I("discipline")>=0&&j.I("discipline")<6,"Проверьте условия норматива");J.Check(j.S("sex") is "male" or "female","Выберите пол");
                var r=j.S("id")==""?d.Add("standards",new{}):d.Get("standards",j.S("id"));foreach(var k in new[]{"name","unit","source"})r[k]=j.Required(k,2000);
                foreach(var k in new[]{"sex","comparison","threshold","stage","discipline","verified"})r[k]=j[k]?.DeepClone();r["revision"]=r.I("revision")+1;return r.DeepClone();
            }
            case "assessment":
            {
                Security.Require(a,"coach","federation");var r=d.Get("standards",j.Required("standard"));var p=d.Get("people",j.Required("person"));J.Check(Security.CanAthlete(d,a,p.S("id"),r.I("discipline")),"Нет связи с этим спортсменом",403);
                J.Check(p.S("sex")==r.S("sex")&&p.I("stage")==r.I("stage")&&Commands.HasDisc(p,r.I("discipline")),"Норматив не соответствует спортсмену");var value=j.N("value");J.Check(double.IsFinite(value)&&value>=0,"Неверное значение");
                return d.Add("assessments",new{person=p.S("id"),standard=r.S("id"),value,passed=r.S("comparison")=="gte"?value>=r.N("threshold"):value<=r.N("threshold"),snapshot=r.DeepClone(),at=J.Now}).DeepClone();
            }
            case "promote":
            {
                Security.Require(a,"coach","federation");var p=d.Get("people",j.Required("person"));var disc=j.I("discipline");J.Check(Security.CanAthlete(d,a,p.S("id"),disc),"Нет доступа",403);
                J.Check(Ready(d,p,disc),"Не все подтверждённые нормативы выполнены за последний год");J.Check(p.I("stage")<3,"Достигнут высший этап");p["stage"]=p.I("stage")+1;Security.Notify(d,p.S("id"),"Оформлен перевод на следующий этап подготовки");return new{ok=true};
            }
            case "prize-rule":
            {
                Fed();J.Check(j.I("place")>=1&&j.I("place")<=3&&j.N("amount")>0&&j.N("amount")<=100000000,"Проверьте сумму и место");j.Required("source",2000);var r=j.S("id")==""?d.Add("prizeRules",new{}):d.Get("prizeRules",j.S("id"));foreach(var k in new[]{"level","place","amount","source","verified"})r[k]=j[k]?.DeepClone();return r.DeepClone();
            }
            case "petition":
            {
                Fed();var year=j.I("year");J.Check(year>=2000&&year<=J.Today.Year,"Проверьте год");var candidates=PrizeCandidates(d,year);J.Check(candidates.Count>0,"Нет кандидатов по подтверждённым правилам");J.Check(!d.Rows("petitions").Any(x=>x.I("year")==year),"Ходатайство за этот год уже создано");return d.Add("petitions",new{year,status="prepared",candidates,at=J.Now}).DeepClone();
            }
            case "petition-status":
            {
                Fed();var p=d.Get("petitions",j.Required("id"));J.Check((p.S("status"),j.S("status")) is ("prepared","sent") or ("sent","awarded"),"Неверный переход статуса");p["status"]=j.S("status");p["reference"]=j.Required("reference");return new{ok=true};
            }
            case "certificate":
            {
                Security.Require(a,"athlete","federation");var r=d.Get("results",j.Required("result"));J.Check(a.Role=="federation"||r.S("person")==a.Person,"Чужой результат",403);var existing=d.Rows("certificates").FirstOrDefault(x=>x.S("result")==r.S("id")&&!x.B("revoked"));return (existing??d.Add("certificates",new{result=r.S("id"),person=r.S("person"),number="ФСП-"+J.Today.Year+"-"+Security.Secret()[..10].ToUpperInvariant(),token=Security.Secret(),revoked=false,at=J.Now})).DeepClone();
            }
            case "certificate-revoke":Fed();d.Get("certificates",j.Required("id"))["revoked"]=true;return new{ok=true};
            case "api-token":
            {
                Fed();var scopes=j.Strings("scopes");J.Check(scopes.Length>0&&scopes.All(x=>new[]{"events:read","results:read","results:write"}.Contains(x)),"Неверные права");var until=j.Day("until");J.Check(until>J.Today&&until<=J.Today.AddYears(1),"Срок токена: до одного года");var raw=Security.Secret();var token=d.Add("tokens",new{name=j.Required("name"),hash=Security.Hash(raw),scopes,until=until.ToString("yyyy-MM-dd"),revoked=false});return new{id=token.S("id"),token=raw};
            }
            case "token-revoke":Fed();d.Get("tokens",j.Required("id"))["revoked"]=true;return new{ok=true};
            case "client":
            {
                Fed();J.Check(Uri.TryCreate(j.S("redirectUri"),UriKind.Absolute,out var uri)&&uri.Fragment==""&&(uri.Scheme=="https"||(uri.Scheme=="http"&&uri.IsLoopback)),"Нужен HTTPS redirect_uri (HTTP допустим только для localhost)");return d.Add("clients",new{name=j.Required("name"),redirectUri=j.S("redirectUri"),disabled=false}).DeepClone();
            }
            case "client-disable":Fed();d.Get("clients",j.Required("id"))["disabled"]=true;return new{ok=true};
            default:throw new DomainError("Неизвестная команда",404);
        }
    }
    public static bool Ready(JsonObject d,JsonObject p,int disc)
    {
        var rules=d.Rows("standards").Where(x=>x.I("stage")==p.I("stage")&&x.S("sex")==p.S("sex")&&x.I("discipline")==disc).ToArray();
        return rules.Length>0&&rules.All(r=>r.B("verified")&&d.Rows("assessments").Where(x=>x.S("person")==p.S("id")&&x.S("standard")==r.S("id")&&DateTimeOffset.Parse(x.S("at"))>=DateTimeOffset.UtcNow.AddYears(-1)).OrderByDescending(x=>x.S("at")).Take(1).Any(x=>x.B("passed")&&x["snapshot"]!.I("revision")==r.I("revision")));
    }
    public static List<object> PrizeCandidates(JsonObject d,int year)
    {
        var rows=new List<object>();foreach(var result in d.Rows("results"))
        {
            var e=d.Get("events",result.S("event"));if(e.Day("date").Year!=year)continue;
            var rule=d.Rows("prizeRules").FirstOrDefault(x=>x.B("verified")&&x.S("level")==e.S("level")&&x.I("place")==result.I("place"));if(rule==null)continue;
            rows.Add(new{person=result.S("person"),result=result.S("id"),amount=rule.N("amount"),rule=rule.DeepClone()});
        }
        return rows;
    }
}
