using System.Text.Json.Nodes;
namespace Orencode;
public static class Commands
{
    public static object Run(JsonObject d,Actor a,string name,JsonObject j)
    {
        J.Check(a.Role!="ministry","Кабинет министерства доступен только для чтения",403);
        string id=j.S("id"), me=a.Person; JsonObject Find(string t)=>d.Get(t,id);
        void Fed()=>Security.Require(a,"federation");
        object result=new {ok=true};
        switch(name)
        {
            case "profile":
            {
                var p=d.Get("people",me);var birth=j.Day("birthDate");J.Check(birth<J.Today&&birth>J.Today.AddYears(-110),"Проверьте дату рождения");
                if(birth>J.Today.AddYears(-18)){J.Check(j.B("guardianConsent")&&j.S("guardian").Length>=3,"Нужно согласие законного представителя");d.Add("consents",new{person=me,guardian=true,representative=j.S("guardian"),version="guardian-v1",at=J.Now});}
                p["name"]=j.Required("name");p["city"]=j.Required("city");p["birthDate"]=birth.ToString("yyyy-MM-dd");
                J.Check(j.S("sex") is "male" or "female","Выберите пол для нормативов");p["sex"]=j.S("sex");
                var discs=j["disciplines"]?.AsArray().Select(x=>x!.GetValue<int>()).Distinct().ToArray()??[];J.Check(discs.Length>0&&discs.All(x=>x>=0&&x<6),"Выберите дисциплины");p["disciplines"]=System.Text.Json.JsonSerializer.SerializeToNode(discs);
                if(j.S("org")!="")d.Get("organizations",j.S("org"));p["org"]=j.S("org");
                p["guardian"]=j.S("guardian");p["rusadaDate"]=j.S("rusadaDate");
                break;
            }
            case "roles":
                Fed();var account=Find("accounts");var roles=j.Strings("roles");J.Check(roles.Length>0&&roles.All(Security.Roles.Contains),"Неверные роли");J.Check(account.S("id")!=a.Id||roles.Contains("federation"),"Нельзя лишить себя доступа федерации");account["roles"]=System.Text.Json.JsonSerializer.SerializeToNode(roles);break;
            case "organization":
                Fed();result=d.Add("organizations",new{name=j.Required("name"),city=j.Required("city"),license=j.S("license"),programs=j.S("programs")}).DeepClone();break;
            case "event-save":
            {
                Fed();var date=j.Day("date");var deadline=j.Day("deadline");J.Check(deadline<=date&&date>=J.Today,"Проверьте даты соревнования");
                int discipline=j.I("discipline"),capacity=j.I("capacity"),min=j.I("minAge"),max=j.I("maxAge");
                J.Check(discipline>=0&&discipline<6&&capacity>=1&&capacity<=10000&&min>=0&&max>=min&&max<=110,"Проверьте дисциплину, вместимость и возраст");
                J.Check(new[]{"regional","interregional","youth","national"}.Contains(j.S("level")),"Неизвестный уровень");
                var e=id==""?d.Add("events",new{status="draft",revision=0}):Find("events");
                J.Check(e.S("status")=="draft"&&!d.Rows("applications").Any(x=>x.S("event")==e.S("id")),"Редактируется только черновик без заявок");
                J.Check(e.I("revision")==j.I("revision"),"Запись уже изменена, обновите страницу",409);
                foreach(var k in new[]{"title","city","venue","format","type","level","calendarPlan","calendarNumber","regulation"})e[k]=j.Required(k,k=="regulation"?10000:200);
                e["date"]=date.ToString("yyyy-MM-dd");e["deadline"]=deadline.ToString("yyyy-MM-dd");e["discipline"]=discipline;e["capacity"]=capacity;e["minAge"]=min;e["maxAge"]=max;e["revision"]=e.I("revision")+1;
                var kinds=j.Strings("requiredDocuments");J.Check(kinds.All(x=>new[]{"consent","medical","insurance","other"}.Contains(x)),"Неизвестный тип документа");e["requiredDocuments"]=System.Text.Json.JsonSerializer.SerializeToNode(kinds);
                result=e.DeepClone();break;
            }
            case "event-status":
            {
                Fed();var e=Find("events");var next=j.S("status");J.Check((e.S("status"),next) is ("draft","open") or ("open","closed") or ("closed","open"),"Недопустимый переход статуса");J.Check(next!="open"||e.Day("deadline")>=J.Today,"Дедлайн истёк");e["status"]=next;e["revision"]=e.I("revision")+1;break;
            }
            case "coach-request":
            {
                Security.Require(a,"coach");var p=d.Get("people",j.S("person"));var disc=j.I("discipline");J.Check(p.S("id")!=me&&HasDisc(p,disc)&&HasDisc(d.Get("people",me),disc),"Дисциплина должна совпадать у тренера и спортсмена");
                J.Check(d.Rows("accounts").Any(x=>x.S("person")==p.S("id")&&x.Strings("roles").Contains("athlete")),"Нужен аккаунт спортсмена");
                J.Check(!d.Rows("links").Any(x=>x.S("coach")==me&&x.S("person")==p.S("id")&&x.I("discipline")==disc&&x.S("status")!="ended"),"Запрос или связь уже существует",409);
                result=d.Add("links",new{coach=me,person=p.S("id"),discipline=disc,status="pending",at=J.Now}).DeepClone();Security.Notify(d,p.S("id"),"Тренер отправил запрос на подтверждение связи");break;
            }
            case "coach-confirm":
            {
                Security.Require(a,"athlete");var l=Find("links");J.Check(l.S("person")==me,"Чужой запрос",403);J.Check(l.S("status")=="pending","Запрос уже обработан",409);l["status"]=j.B("accept")?"active":"ended";l["confirmedAt"]=J.Now;Security.Notify(d,l.S("coach"),"Спортсмен рассмотрел запрос тренера");break;
            }
            case "coach-status":
            {
                var l=Find("links");J.Check(a.Role=="federation"||(a.Role=="coach"&&l.S("coach")==me)||(a.Role=="athlete"&&l.S("person")==me),"Чужая связь",403);
                J.Check(l.S("status") is "active" or "paused","Связь не подтверждена или завершена");J.Check(j.S("status") is "active" or "paused" or "ended","Неизвестный статус");l["status"]=j.S("status");break;
            }
            case "apply":
            {
                var e=d.Get("events",j.S("event"));var p=d.Get("people",j.S("person",me));
                J.Check(Security.CanAthlete(d,a,p.S("id"),e.I("discipline")),"Нет подтверждённой связи с этим спортсменом",403);
                J.Check(e.S("status")=="open"&&e.Day("deadline")>=J.Today,"Приём заявок закрыт");J.Check(j.B("consent"),"Необходимо согласие");
                J.Check(HasDisc(p,e.I("discipline")),"Дисциплина отсутствует в профиле");var age=Age(p.Day("birthDate"),e.Day("date"));J.Check(age>=e.I("minAge")&&age<=e.I("maxAge"),"Возраст не подходит");
                J.Check(!Suspended(p,e.Day("date")),"Спортсмен отстранён");if(age<18)J.Check(d.Rows("consents").Any(x=>x.S("person")==p.S("id")&&x.B("guardian")),"Нет согласия законного представителя");
                J.Check(!d.Rows("applications").Any(x=>x.S("event")==e.S("id")&&x.S("person")==p.S("id")),"Заявка уже существует",409);
                J.Check(d.Rows("applications").Count(x=>x.S("event")==e.S("id")&&x.S("status")!="rejected")<e.I("capacity"),"Все места заняты",409);
                var docs=ValidateDocuments(d,p.S("id"),j.Strings("documentIds"));
                result=d.Add("applications",new{@event=e.S("id"),person=p.S("id"),status="pending",consent=true,documentIds=docs,at=J.Now}).DeepClone();
                d.Add("consents",new{person=p.S("id"),actor=a.Id,@event=e.S("id"),version="application-v1",at=J.Now});Security.NotifyFederation(d,"Новая заявка: "+e.S("title"));break;
            }
            case "application-documents":
            {
                var x=Find("applications");var e=d.Get("events",x.S("event"));J.Check(Security.CanAthlete(d,a,x.S("person"),e.I("discipline")),"Нет доступа",403);J.Check(x.S("status")=="pending","Документы можно менять до рассмотрения");x["documentIds"]=System.Text.Json.JsonSerializer.SerializeToNode(ValidateDocuments(d,x.S("person"),j.Strings("documentIds")));break;
            }
            case "application-review":
            {
                Fed();var x=Find("applications");J.Check(x.S("status")=="pending","Заявка уже рассмотрена",409);var status=j.S("status");J.Check(status is "approved" or "rejected","Неверное решение");
                if(status=="rejected")j.Required("note",2000);else J.Check(MissingDocuments(d,x).Length==0,"Не хватает обязательных документов: "+string.Join(", ",MissingDocuments(d,x)));
                x["status"]=status;x["note"]=j.S("note");x["reviewedAt"]=J.Now;Security.Notify(d,x.S("person"),"Решение по заявке: "+status);break;
            }
            case "assign":
            {
                Fed();var e=d.Get("events",j.S("event"));J.Check(e.S("status")!="published","Протокол уже опубликован");var rows=j["judges"]?.AsArray()??[];
                var ids=new HashSet<string>();foreach(var row in rows){var pid=row!.S("person");J.Check(ids.Add(pid),"Судья указан дважды");var p=d.Get("people",pid);J.Check(d.Rows("accounts").Any(x=>x.S("person")==pid&&x.Strings("roles").Contains("judge")),"У участника нет роли судьи");J.Check(p.S("category")!=""&&DateOnly.TryParse(p.S("categoryUntil"),out var end)&&end>=e.Day("date"),"Категория судьи не действует на дату соревнования");row!.Required("position");}
                var arr=d["assignments"]!.AsArray();foreach(var old in d.Rows("assignments").Where(x=>x.S("event")==e.S("id")).ToArray())arr.Remove(old);
                foreach(var row in rows){d.Add("assignments",new{@event=e.S("id"),person=row!.S("person"),position=row.S("position")});Security.Notify(d,row.S("person"),"Назначение в коллегию: "+e.S("title"));}break;
            }
            case "publish": result=Publish(d,a,j);break;
            case "qualification":
            {
                var type=j.S("type");J.Check(type is "rank" or "category","Неверный тип");var pid=j.S("person",me);var p=d.Get("people",pid);
                if(type=="category"){Security.Require(a,"judge","federation");J.Check(a.Role=="federation"||pid==me,"Чужая категория",403);j.Required("basis",2000);}
                else {var r=d.Get("results",j.S("resultId"));var e=d.Get("events",r.S("event"));J.Check(r.S("person")==pid&&Security.CanAthlete(d,a,pid,e.I("discipline")),"Нет основания или доступа",403);}
                J.Check(!d.Rows("qualifications").Any(x=>x.S("person")==pid&&x.S("type")==type&&x.S("status")=="pending"),"Уже есть заявка на рассмотрении");
                result=d.Add("qualifications",new{person=pid,type,value=j.Required("value"),basis=j.S("basis"),resultId=j.S("resultId"),status="pending",at=J.Now}).DeepClone();Security.NotifyFederation(d,"Заявка на присвоение: "+p.S("name"));break;
            }
            case "qualification-review":
            {
                Fed();var q=Find("qualifications");J.Check(q.S("status")=="pending","Уже рассмотрено",409);var status=j.S("status");J.Check(status is "approved" or "rejected","Неверное решение");
                if(status=="approved"){var from=j.Day("from");var until=j.Day("until");J.Check(until>from&&from<=J.Today,"Проверьте сроки");var order=j.Required("order");var p=d.Get("people",q.S("person"));var prefix=q.S("type");p[prefix]=q.S("value");p[prefix+"Until"]=until.ToString("yyyy-MM-dd");p[prefix+"Order"]=order;q["from"]=from.ToString("yyyy-MM-dd");q["until"]=until.ToString("yyyy-MM-dd");q["order"]=order;}
                else j.Required("note",2000);q["status"]=status;q["note"]=j.S("note");Security.Notify(d,q.S("person"),"Рассмотрена заявка на присвоение");break;
            }
            case "suspension": Fed();var pp=Find("people");pp["suspendedUntil"]=j.S("until")==""?"":j.Day("until").ToString("yyyy-MM-dd");pp["suspensionReason"]=j.Required("reason",2000);break;
            case "roster":
            {
                Fed();var pid=j.Required("person");var year=j.I("year",J.Today.Year);var disc=j.I("discipline");var p=d.Get("people",pid);J.Check(!Suspended(p,J.Today),"Спортсмен отстранён");J.Check(Queries.Rating(d,year,disc).Any(x=>x.S("person")==pid),"Нет результатов в выбранном сезоне и дисциплине");
                var r=d.Rows("roster").FirstOrDefault(x=>x.S("person")==pid&&x.I("year")==year&&x.I("discipline")==disc);if(r!=null)d["roster"]!.AsArray().Remove(r);else d.Add("roster",new{person=pid,year,discipline=disc});break;
            }
            case "rating-rule":
                Fed();J.Check(j.I("place")>0&&j.N("points")>=0&&j.N("points")<=10000,"Неверные баллы");var rule=d.Rows("ratingRules").FirstOrDefault(x=>x.S("level")==j.S("level")&&x.I("place")==j.I("place"));if(rule==null)d.Add("ratingRules",new{level=j.Required("level"),place=j.I("place"),points=j.N("points")});else rule["points"]=j.N("points");break;
            case "notice-read": foreach(var n in d.Rows("notifications").Where(x=>x.S("person")==me))n["read"]=true;break;
            default: result=Extras.Run(d,a,name,j);break;
        }
        Security.Audit(d,a,name,id);return result;
    }
    public static bool HasDisc(JsonObject p,int disc)=>p["disciplines"]!.AsArray().Any(x=>x!.GetValue<int>()==disc);
    public static int Age(DateOnly birth,DateOnly date)=>date.Year-birth.Year-(birth.AddYears(date.Year-birth.Year)>date?1:0);
    public static bool Suspended(JsonObject p,DateOnly date)=>DateOnly.TryParse(p.S("suspendedUntil"),out var end)&&end>=date;
    public static string[] ValidateDocuments(JsonObject d,string person,string[] ids){foreach(var id in ids)J.Check(d.Get("documents",id).S("person")==person,"Документ другого спортсмена",403);return ids.Distinct().ToArray();}
    public static string[] MissingDocuments(JsonObject d,JsonObject app)=>d.Get("events",app.S("event")).Strings("requiredDocuments").Where(kind=>kind=="consent"?!app.B("consent"):!app.Strings("documentIds").Any(id=>d.Rows("documents").Any(x=>x.S("id")==id&&x.S("kind")==kind&&x.S("person")==app.S("person")))).ToArray();
    public static object Publish(JsonObject d,Actor a,JsonObject j)
    {
        Security.Require(a,"federation","judge","integration");var e=d.Get("events",j.S("event"));var eid=e.S("id");
        var assigned=d.Rows("assignments").Where(x=>x.S("event")==eid).ToArray();J.Check(a.Role!="judge"||assigned.Any(x=>x.S("person")==a.Person),"Вы не назначены на это соревнование",403);
        J.Check(e.S("status") is "open" or "closed","Публикация недоступна",409);J.Check(assigned.Length>0,"Назначьте судейскую коллегию");
        foreach(var judge in assigned){var p=d.Get("people",judge.S("person"));J.Check(DateOnly.TryParse(p.S("categoryUntil"),out var end)&&end>=e.Day("date"),"Истекла категория судьи");}
        J.Check(!d.Rows("applications").Any(x=>x.S("event")==eid&&x.S("status")=="pending"),"Рассмотрите все заявки");
        var approved=d.Rows("applications").Where(x=>x.S("event")==eid&&x.S("status")=="approved").Select(x=>x.S("person")).ToHashSet();
        var scores=j["scores"]?.AsArray().Select(x=>x!.AsObject()).ToArray()??[];
        J.Check(approved.Count>0&&scores.Length==approved.Count&&scores.Select(x=>x.S("person")).ToHashSet().SetEquals(approved)&&scores.All(x=>x["score"]!=null&&double.IsFinite(x.N("score"))&&x.N("score")>=0&&x.N("score")<=1000),"Укажите баллы 0–1000 для каждого допущенного спортсмена");
        var sorted=scores.OrderByDescending(x=>x.N("score")).ToArray();int place=1;
        for(int i=0;i<sorted.Length;i++){if(i>0&&sorted[i].N("score")!=sorted[i-1].N("score"))place=i+1;d.Add("results",new{@event=eid,person=sorted[i].S("person"),score=sorted[i].N("score"),place,at=J.Now});Security.Notify(d,sorted[i].S("person"),"Опубликован протокол: "+e.S("title"));}
        e["status"]="published";e["publishedAt"]=J.Now;e["revision"]=e.I("revision")+1;return new{ok=true};
    }
}
