using System.Net;
using System.Net.Mail;
namespace Orencode;
public sealed class DeliveryWorker(Store store,IConfiguration config,ILogger<DeliveryWorker> log):BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while(!stoppingToken.IsCancellationRequested)
        {
            try
            {
                Reminders();
                var queued=store.Read(d=>d.Rows("outbox").Where(x=>x.S("status")=="pending"&&x.I("attempts")<5).Select(x=>x.DeepClone().AsObject()).Take(20).ToArray());
                foreach(var item in queued)
                {
                    try
                    {
                        var mode=config["Email:Mode"]??"pickup";
                        using var smtp=new SmtpClient();smtp.Timeout=10000;
                        if(mode=="smtp")
                        {
                            smtp.Host=config["Email:Host"]??throw new InvalidOperationException("Email:Host required");smtp.Port=config.GetValue("Email:Port",587);smtp.EnableSsl=config.GetValue("Email:EnableSsl",true);
                            if(config["Email:User"] is string user)smtp.Credentials=new NetworkCredential(user,config["Email:Password"]);
                        }
                        else {var dir=Path.Combine(store.DataDirectory,"mail");Directory.CreateDirectory(dir);smtp.DeliveryMethod=SmtpDeliveryMethod.SpecifiedPickupDirectory;smtp.PickupDirectoryLocation=dir;}
                        using var message=new MailMessage(config["Email:From"]??"orencode@example.invalid",item.S("email"),"КОД / СПОРТ · уведомление",item.S("text"));
                        await smtp.SendMailAsync(message,stoppingToken);
                        store.Write(d=>{var x=d.Get("outbox",item.S("id"));x["status"]=mode=="smtp"?"sent":"prepared";x["deliveredAt"]=J.Now;return 0;});
                    }
                    catch(Exception e) when(!stoppingToken.IsCancellationRequested){log.LogWarning("Email delivery failed: {Type}",e.GetType().Name);store.Write(d=>{var x=d.Get("outbox",item.S("id"));x["attempts"]=x.I("attempts")+1;if(x.I("attempts")>=5)x["status"]="failed";return 0;});}
                }
            }
            catch(Exception e) when(!stoppingToken.IsCancellationRequested){log.LogError(e,"Delivery worker iteration failed");}
            try{await Task.Delay(TimeSpan.FromSeconds(10),stoppingToken);}catch(OperationCanceledException){break;}
        }
    }
    private void Reminders()=>store.Write(d=>
    {
        var key="reminders:"+J.Today.ToString("yyyy-MM-dd");if(d.Rows("logs").Any(x=>x.S("text")==key))return 0;
        foreach(var risk in Queries.Risks(d)){var person=risk!.S("person");if(person!="")Security.Notify(d,person,risk.S("text"));else Security.NotifyFederation(d,risk.S("text"));}
        if(J.Today.Month==1)Security.NotifyFederation(d,"До 1 февраля: подготовьте ходатайства на премии за прошлый год");
        Security.Audit(d,new("system","","system"),key);return 0;
    });
}
