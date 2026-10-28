using System.Text.Json.Nodes;
namespace Orencode;
public static class Seed
{
    public static readonly string[] Disciplines=["Алгоритмическое программирование","Продуктовое программирование","Программирование систем ИБ","Программирование систем ИИ","Программирование робототехники","Программирование БАС"];
    public static void Run(Store store,IConfiguration config)
    {
        store.Write(d=>
        {
            if(d.Rows("accounts").Any())return 0;
            bool demo=config.GetValue<bool>("Demo:Enabled");
            var password=config[demo?"Demo:Password":"Bootstrap:Password"]??"";
            J.Check(password.Length>=12,"Для первого запуска задайте пароль от 12 символов: Demo__Password или Bootstrap__Password");
            void Person(string id,string name,string email,string[] roles,int disc=0,string sex="male")
            {
                d.Add("people",new{id,name,city="Оренбург",org="o1",disciplines=new[]{disc},sex,birthDate="2000-01-15",startYear=2024,stage=0,rank="Без разряда",category="",suspendedUntil=""});
                d.Add("accounts",new{id="a-"+id,person=id,email,passwordHash=Security.Password(password),roles,disabled=false,failures=0});
                d.Add("consents",new{person=id,version="registration-v1",at=J.Now,guardian=false});
            }
            Person("admin",demo?"Сотрудник федерации (демо)":"Администратор",config["Bootstrap:Email"]??"federation@example.invalid",["federation"]);
            d.Add("organizations",new{id="o1",name="Демо-клуб «Код»",city="Оренбург",license="ДЕМОНСТРАЦИОННЫЕ ДАННЫЕ",programs="6 дисциплин"});
            foreach(var (level,points) in new[]{("national",new[]{100,80,60}),("youth",new[]{80,60,40}),("interregional",new[]{60,45,30}),("regional",new[]{40,30,20})})
                for(int i=0;i<3;i++)d.Add("ratingRules",new{level,place=i+1,points=points[i]});
            if(!demo)return 0;
            Person("p1","Алексей Демин (демо)","athlete@example.invalid",["athlete","judge"]);
            Person("p2","Мария Соколова (демо)","maria@example.invalid",["athlete"],sex:"female");
            Person("p3","Илья Ким (демо)","ilya@example.invalid",["athlete"],1);
            Person("c1","Ольга Данилова (демо)","coach@example.invalid",["coach","athlete"],sex:"female");
            Person("j1","Елена Тихонова (демо)","judge@example.invalid",["judge"],sex:"female");
            Person("ministry","Сотрудник Минспорта (демо)","ministry@example.invalid",["ministry"]);
            foreach(var id in new[]{"p1","j1"}){var p=d.Get("people",id);p["category"]="III категория";p["categoryUntil"]=J.Today.AddDays(id=="p1"?14:365).ToString("yyyy-MM-dd");}
            var p1=d.Get("people","p1");p1["rank"]="III разряд";p1["rankUntil"]=J.Today.AddDays(20).ToString("yyyy-MM-dd");
            foreach(var (id,title,offset,disc) in new[]{("e0","Осенний алгоритмический турнир",-14,0),("e1","Кубок Оренбургской области",14,0),("e2","Продуктовый хакатон",21,1)})
            {
                d.Add("events",new{id,title,date=J.Today.AddDays(offset).ToString("yyyy-MM-dd"),deadline=J.Today.AddDays(offset-3).ToString("yyyy-MM-dd"),discipline=disc,city="Оренбург",venue="Демоарена «Код»",format="Очный",level="regional",type="Кубок",minAge=14,maxAge=99,capacity=40,status=id=="e0"?"published":"open",revision=1,calendarPlan="Региональный",calendarNumber="ДЕМО-2026",regulation="Демонстрационное положение. Индивидуальный зачёт. Победитель определяется по сумме баллов.",requiredDocuments=new[]{"consent"}});
                if(id!="e2")d.Add("assignments",new{@event=id,person="j1",position="Главный судья"});
            }
            foreach(var (person,score,place) in new[]{("p1",210,2),("p2",270,1)})
            {
                d.Add("applications",new{@event="e0",person,status="approved",consent=true,documentIds=Array.Empty<string>(),at=J.Now});
                d.Add("results",new{@event="e0",person,score,place,at=J.Now});
            }
            d.Add("applications",new{@event="e1",person="p2",status="pending",consent=true,documentIds=Array.Empty<string>(),at=J.Now});
            d.Add("links",new{coach="c1",person="p2",discipline=0,status="active",at=J.Now});
            d.Add("groups",new{id="g1",name="Алгоритмы · начальная подготовка",coach="c1",org="o1",discipline=0,stage=0,members=new[]{"p2"}});
            d.Add("standards",new{name="Скорость печати (учебный пример)",discipline=0,stage=0,sex="female",unit="зн/мин",threshold=100,comparison="gte",source="Демонстрационное значение — требуется сверка с приказом № 445",verified=false});
            d.Add("prizeRules",new{level="regional",place=1,amount=10000,source="Демонстрационное значение — требуется сверка с Указом № 93-ук",verified=false});
            d.Add("clients",new{id="demo-client",name="Тестовый клиент ФСП ID",redirectUri=(config["PublicUrl"]??"http://localhost:5080")+"/oidc-demo.html",disabled=false});
            Security.Audit(d,new("a-admin","admin","federation"),"Загружены фиктивные демонстрационные данные");
            return 0;
        });
    }
}
