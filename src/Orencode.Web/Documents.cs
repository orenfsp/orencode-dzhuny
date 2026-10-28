using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml;
using System.Xml.Linq;
using QRCoder;
namespace Orencode;
public static class Documents
{
    public static void Map(WebApplication app,Store store)
    {
        app.MapPost("/api/documents",async(HttpContext c)=>
        {
            J.Check(c.Request.ContentLength is >0 and <=11000000,"Размер файла: не более 10 МБ");
            var form=await c.Request.ReadFormAsync();var f=form.Files.GetFile("file");J.Check(f!=null&&f.Length>0&&f.Length<=10000000,"Добавьте файл до 10 МБ");
            var person=form["person"].ToString();var kind=form["kind"].ToString();J.Check(new[]{"medical","insurance","consent","other"}.Contains(kind),"Неизвестный тип документа");
            store.Read(d=>{var a=Security.Current(c,d);J.Check(a.Role!="ministry"&&Security.Visible(d,a,person),"Нет доступа",403);return 0;});
            using var memory=new MemoryStream();await f!.CopyToAsync(memory);var bytes=memory.ToArray();var ext=Path.GetExtension(f.FileName).ToLowerInvariant();
            var valid=ext switch{".pdf"=>bytes.AsSpan().StartsWith("%PDF-"u8),".png"=>bytes.AsSpan().StartsWith(new byte[]{137,80,78,71,13,10,26,10}),".jpg" or ".jpeg"=>bytes.AsSpan().StartsWith(new byte[]{255,216,255}),".docx"=>ValidDocx(bytes),_=>false};J.Check(valid,"Допустимы PDF, PNG, JPEG и DOCX с корректной сигнатурой");
            string id=J.Id();var directory=Path.Combine(store.DataDirectory,"uploads");Directory.CreateDirectory(directory);var path=Path.Combine(directory,id);await File.WriteAllBytesAsync(path,bytes);
            try{return store.Write(d=>{var a=Security.Current(c,d);J.Check(a.Role!="ministry"&&Security.Visible(d,a,person),"Нет доступа",403);var doc=d.Add("documents",new{id,person,kind,name=Path.GetFileName(f.FileName),size=bytes.Length,sha256=Security.Hash(Convert.ToBase64String(bytes)),at=J.Now});Security.Audit(d,a,"document-upload",id);return doc.DeepClone();});}
            catch{File.Delete(path);throw;}
        });
        app.MapGet("/api/documents/{id}",(HttpContext c,string id)=>
        {
            var doc=store.Read(d=>{var a=Security.Current(c,d);var r=d.Get("documents",id);J.Check(Security.Visible(d,a,r.S("person")),"Нет доступа",403);return r;});
            return Results.File(Path.Combine(store.DataDirectory,"uploads",doc.S("id")),"application/octet-stream",doc.S("name"));
        });
        app.MapGet("/api/export/{kind}",(HttpContext c,string kind,string? id,int? year)=>
        {
            var rows=store.Read(d=>Export(d,Security.Current(c,d),kind,id??"",year??J.Today.Year));bool xlsx=kind=="census"||kind=="registry";
            var bytes=xlsx?Xlsx(rows):Docx(rows);return Results.File(bytes,xlsx?"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet":"application/vnd.openxmlformats-officedocument.wordprocessingml.document",kind+(xlsx?".xlsx":".docx"));
        });
        app.MapGet("/api/certificates/{id}/print",(HttpContext c,string id)=>
        {
            var html=store.Read(d=>{var a=Security.Current(c,d);var cert=d.Get("certificates",id);J.Check(a.Role=="federation"||cert.S("person")==a.Person,"Нет доступа",403);var r=d.Get("results",cert.S("result"));var e=d.Get("events",r.S("event"));var url=(app.Configuration["PublicUrl"]??"http://localhost:5080").TrimEnd('/')+"/verify/"+cert.S("token");using var qr=QRCodeGenerator.GenerateQrCode(url,QRCodeGenerator.ECCLevel.M);using var svg=new SvgQRCode(qr);return Page("Справка об участии","<p>№ "+H(cert.S("number"))+"</p><h2>"+H(d.Get("people",r.S("person")).S("name"))+"</h2><p>"+H(e.S("title"))+" · "+H(e.S("date"))+"</p><p>Место: "+r.I("place")+" · Баллы: "+r.N("score")+"</p><div class='qr'>"+svg.GetGraphic(4)+"</div><p><a href='"+H(url)+"'>Проверить подлинность</a></p><p>Печать / PDF: Ctrl+P</p>"+(cert.B("revoked")?"<strong>СПРАВКА ОТОЗВАНА</strong>":""));});return Results.Content(html,"text/html; charset=utf-8");
        });
        app.MapGet("/verify/{token}",(string token)=>store.Read(d=>
        {
            var cert=d.Rows("certificates").FirstOrDefault(x=>x.S("token")==token);if(cert==null)return Results.NotFound();
            var r=d.Get("results",cert.S("result"));var p=d.Get("people",r.S("person"));var initials=string.Join(" ",p.S("name").Split(' ',StringSplitOptions.RemoveEmptyEntries).Take(3).Select(x=>x[0]+"."));
            return Results.Content(Page(cert.B("revoked")?"Справка отозвана":"Справка подтверждена","<p>№ "+H(cert.S("number"))+"</p><p>"+H(initials)+"</p><p>"+H(d.Get("events",r.S("event")).S("title"))+"</p>"),"text/html; charset=utf-8");
        }));
    }
    private static bool ValidDocx(byte[] bytes){try{using var z=new ZipArchive(new MemoryStream(bytes));return z.GetEntry("word/document.xml")!=null&&z.GetEntry("[Content_Types].xml")!=null;}catch(InvalidDataException){return false;}}
    public static string H(string s)=>System.Net.WebUtility.HtmlEncode(s);
    public static string Page(string title,string body)=>"<!doctype html><html lang='ru'><meta charset='utf-8'><meta name='viewport' content='width=device-width,initial-scale=1'><link rel='stylesheet' href='/certificate.css'><title>"+H(title)+"</title><main><h1>"+H(title)+"</h1>"+body+"<footer>КОД / СПОРТ · ФСП Оренбургской области</footer></main></html>";
    public static List<string[]> Export(JsonObject d,Actor a,string kind,string id,int year)
    {
        string Person(string pid)=>d.Get("people",pid).S("name");var rows=new List<string[]>();
        switch(kind)
        {
            case "protocol":case "regulation":case "judges":
                var e=d.Get("events",id);rows.Add([e.S("title"),e.S("date")]);rows.Add([Seed.Disciplines[e.I("discipline")],e.S("city")]);
                if(kind=="regulation"){rows.Add([e.S("regulation")]);break;}
                if(kind=="judges"){J.Check(a.Role=="federation"||d.Rows("assignments").Any(x=>x.S("event")==id&&x.S("person")==a.Person),"Нет доступа",403);rows.Add(["Судья","Должность"]);foreach(var r in d.Rows("assignments").Where(x=>x.S("event")==id))rows.Add([Person(r.S("person")),r.S("position")]);break;}
                J.Check(e.S("status")=="published","Протокол ещё не опубликован");rows.Add(["Место","Спортсмен","Баллы"]);foreach(var r in d.Rows("results").Where(x=>x.S("event")==id).OrderBy(x=>x.I("place")))rows.Add([r.I("place").ToString(),Person(r.S("person")),r.N("score").ToString(System.Globalization.CultureInfo.InvariantCulture)]);break;
            case "history":
                var pid=id==""?a.Person:id;J.Check(Security.Visible(d,a,pid),"Нет доступа",403);rows.Add(["История выступлений",Person(pid)]);rows.Add(["Соревнование","Дата","Место","Баллы"]);foreach(var r in d.Rows("results").Where(x=>x.S("person")==pid)){var ev=d.Get("events",r.S("event"));rows.Add([ev.S("title"),ev.S("date"),r.I("place").ToString(),r.N("score").ToString()]);}break;
            case "judge-history":
                Security.Require(a,"judge","federation");var judge=a.Role=="judge"?a.Person:id;rows.Add(["История судейства",Person(judge)]);foreach(var r in d.Rows("assignments").Where(x=>x.S("person")==judge)){var ev=d.Get("events",r.S("event"));rows.Add([ev.S("title"),ev.S("date"),r.S("position")]);}break;
            case "census":
                Security.Require(a,"federation","ministry");rows.Add(["Год","Организация","Город","Дисциплина","Возраст","Численность"]);foreach(var r in Queries.Census(d,year)){var org=d.Rows("organizations").FirstOrDefault(x=>x.S("id")==r!.S("org"))?.S("name")??"Не указана";rows.Add([r!.I("year").ToString(),org,r.S("city"),Seed.Disciplines[r.I("discipline")],r.S("age"),r.I("count").ToString()]);}break;
            case "registry":
                Security.Require(a,"federation");rows.Add(["ФИО","Город","Организация","Разряд","Категория"]);foreach(var p in d.Rows("people"))rows.Add([p.S("name"),p.S("city"),p.S("org"),p.S("rank"),p.S("category")]);break;
            case "roster":case "order":
                Security.Require(a,"federation","ministry");rows.Add([kind=="order"?"ПРОЕКТ ПРИКАЗА — требует утверждения":"Проект состава сборной",year.ToString()]);rows.Add(["Спортсмен","Дисциплина"]);foreach(var r in d.Rows("roster").Where(x=>x.I("year")==year))rows.Add([Person(r.S("person")),Seed.Disciplines[r.I("discipline")]]);break;
            case "prize":
                Security.Require(a,"federation");var petition=d.Get("petitions",id);rows.Add(["Ходатайство на премии",petition.I("year").ToString()]);rows.Add(["Статус",petition.S("status")]);rows.Add(["Спортсмен","Сумма","Основание"]);foreach(var row in petition["candidates"]!.AsArray())rows.Add([Person(row!.S("person")),row.N("amount").ToString(),row!["rule"]!.S("source")]);break;
            default:throw new DomainError("Неизвестный документ",404);
        }
        return rows;
    }
    private static void Entry(ZipArchive zip,string path,XElement root){using var writer=new StreamWriter(zip.CreateEntry(path).Open(),new UTF8Encoding(false));writer.Write("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");writer.Write(root.ToString(SaveOptions.DisableFormatting));}
    public static byte[] Docx(List<string[]> rows)
    {
        using var stream=new MemoryStream();using(var zip=new ZipArchive(stream,ZipArchiveMode.Create,true))
        {
            XNamespace w="http://schemas.openxmlformats.org/wordprocessingml/2006/main",ct="http://schemas.openxmlformats.org/package/2006/content-types",rel="http://schemas.openxmlformats.org/package/2006/relationships";
            Entry(zip,"[Content_Types].xml",new XElement(ct+"Types",new XElement(ct+"Default",new XAttribute("Extension","rels"),new XAttribute("ContentType","application/vnd.openxmlformats-package.relationships+xml")),new XElement(ct+"Override",new XAttribute("PartName","/word/document.xml"),new XAttribute("ContentType","application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"))));
            Entry(zip,"_rels/.rels",new XElement(rel+"Relationships",new XElement(rel+"Relationship",new XAttribute("Id","rId1"),new XAttribute("Type","http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument"),new XAttribute("Target","word/document.xml"))));
            Entry(zip,"word/document.xml",new XElement(w+"document",new XElement(w+"body",new XElement(w+"p",new XElement(w+"r",new XElement(w+"t","КОД / СПОРТ · ФСП Оренбургской области"))),new XElement(w+"tbl",rows.Select(row=>new XElement(w+"tr",row.Select(cell=>new XElement(w+"tc",new XElement(w+"p",new XElement(w+"r",new XElement(w+"t",new XAttribute(XNamespace.Xml+"space","preserve"),cell)))))))),new XElement(w+"sectPr",new XElement(w+"pgSz",new XAttribute(w+"w","11906"),new XAttribute(w+"h","16838"))))));
        }return stream.ToArray();
    }
    public static byte[] Xlsx(List<string[]> rows)
    {
        using var stream=new MemoryStream();using(var zip=new ZipArchive(stream,ZipArchiveMode.Create,true))
        {
            XNamespace s="http://schemas.openxmlformats.org/spreadsheetml/2006/main",r="http://schemas.openxmlformats.org/officeDocument/2006/relationships",rel="http://schemas.openxmlformats.org/package/2006/relationships",ct="http://schemas.openxmlformats.org/package/2006/content-types";
            Entry(zip,"[Content_Types].xml",new XElement(ct+"Types",new XElement(ct+"Default",new XAttribute("Extension","rels"),new XAttribute("ContentType","application/vnd.openxmlformats-package.relationships+xml")),new XElement(ct+"Override",new XAttribute("PartName","/xl/workbook.xml"),new XAttribute("ContentType","application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml")),new XElement(ct+"Override",new XAttribute("PartName","/xl/worksheets/sheet1.xml"),new XAttribute("ContentType","application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"))));
            XElement Relationship(string type,string target)=>new(rel+"Relationships",new XElement(rel+"Relationship",new XAttribute("Id","rId1"),new XAttribute("Type",r.NamespaceName+"/"+type),new XAttribute("Target",target)));
            Entry(zip,"_rels/.rels",Relationship("officeDocument","xl/workbook.xml"));Entry(zip,"xl/_rels/workbook.xml.rels",Relationship("worksheet","worksheets/sheet1.xml"));
            Entry(zip,"xl/workbook.xml",new XElement(s+"workbook",new XElement(s+"sheets",new XElement(s+"sheet",new XAttribute("name","Отчёт"),new XAttribute("sheetId",1),new XAttribute(r+"id","rId1")))));
            Entry(zip,"xl/worksheets/sheet1.xml",new XElement(s+"worksheet",new XElement(s+"sheetData",rows.Select((row,i)=>new XElement(s+"row",new XAttribute("r",i+1),row.Select(cell=>new XElement(s+"c",new XAttribute("t","inlineStr"),new XElement(s+"is",new XElement(s+"t",new XAttribute(XNamespace.Xml+"space","preserve"),cell)))))))));
        }return stream.ToArray();
    }
}
