#!/usr/bin/env python3
"""HTTP integration tests, isolated SQLite, real Kestrel and local SMTP sink. Python 3.10+."""
import base64, concurrent.futures, hashlib, html, http.cookiejar, io, json, os, pathlib, re, socketserver, subprocess, tempfile, threading, time, urllib.error, urllib.parse, urllib.request, zipfile
from datetime import date, timedelta

ROOT=pathlib.Path(__file__).resolve().parents[1]
DOTNET=os.environ.get('DOTNET','dotnet')
PORT=int(os.environ.get('TEST_PORT','5081'))
BASE=f'http://localhost:{PORT}'
PASSWORD='Test-Orencode-2026!'
checks=[]
def check(ok,label):
    if not ok: raise AssertionError(label)
    checks.append(label)

def day(n=0):return (date.today()+timedelta(days=n)).isoformat()
class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self,*args):return None
class Client:
    def __init__(self,email=None):
        self.jar=http.cookiejar.CookieJar();self.open=urllib.request.build_opener(urllib.request.ProxyHandler({}),urllib.request.HTTPCookieProcessor(self.jar),NoRedirect());self.csrf=''
        self.refresh()
        if email:
            self.req('/api/auth/login',{'email':email+'@example.invalid','password':PASSWORD})
            self.refresh()
    def refresh(self):self.csrf=self.req('/api/auth/csrf')['token']
    def req(self,path,data=None,status=200,headers=None,raw=False,method=None):
        h={'X-CSRF':self.csrf};h.update(headers or {})
        if isinstance(data,dict):data=json.dumps(data).encode();h['Content-Type']='application/json'
        req=urllib.request.Request(BASE+path,data=data,headers=h,method=method)
        try:r=self.open.open(req,timeout=15)
        except urllib.error.HTTPError as e:r=e
        body=r.read();actual=r.status
        if actual!=status:raise AssertionError(f'{path}: expected {status}, got {actual}: {body[:500]!r}')
        if raw:return body,r.headers
        return json.loads(body) if body else None
    def cmd(self,name,data,status=200):return self.req('/api/commands/'+name,data,status)
    def state(self):return self.req('/api/state')

MAIL=[]
class SMTP(socketserver.StreamRequestHandler):
    def handle(self):
        self.wfile.write(b'220 localhost test SMTP\r\n');data=False;message=[]
        while line:=self.rfile.readline():
            if data:
                if line==b'.\r\n':MAIL.append(b''.join(message));message=[];data=False;self.wfile.write(b'250 accepted\r\n')
                else:message.append(line)
            elif line.upper().startswith((b'EHLO',b'HELO')):self.wfile.write(b'250 localhost\r\n')
            elif line.upper().startswith(b'DATA'):data=True;self.wfile.write(b'354 data\r\n')
            elif line.upper().startswith(b'QUIT'):self.wfile.write(b'221 bye\r\n');break
            else:self.wfile.write(b'250 OK\r\n')
            self.wfile.flush()

class Server:
    def __init__(self,folder,smtpport):
        self.folder=folder;self.log=open(pathlib.Path(folder)/'server.log','w+');self.env={**os.environ,'ASPNETCORE_ENVIRONMENT':'Development','ASPNETCORE_URLS':BASE,'PublicUrl':BASE,'Database__Path':str(pathlib.Path(folder)/'orencode.db'),'Demo__Enabled':'true','Demo__Password':PASSWORD,'Email__Mode':'smtp','Email__Host':'127.0.0.1','Email__Port':str(smtpport),'Email__EnableSsl':'false'}
        self.proc=None
    def start(self):
        self.proc=subprocess.Popen([DOTNET,'run','--no-build','--project',str(ROOT/'src/Orencode.Web')],env=self.env,stdout=self.log,stderr=self.log)
        for _ in range(120):
            try:
                r=urllib.request.build_opener(urllib.request.ProxyHandler({})).open(BASE+'/health',timeout=1)
                if r.status==200:return
            except Exception:pass
            if self.proc.poll() is not None:break
            time.sleep(.25)
        self.log.flush();raise RuntimeError(pathlib.Path(self.folder,'server.log').read_text())
    def stop(self):
        if self.proc and self.proc.poll() is None:
            self.proc.terminate()
            try:self.proc.wait(timeout=10)
            except subprocess.TimeoutExpired:self.proc.kill();self.proc.wait()

def run(server):
    anon=Client();anon.req('/api/state',status=401)
    admin=Client('federation');ath=Client('athlete');coach=Client('coach');judge=Client('judge');maria=Client('maria');ministry=Client('ministry')
    check('passwordHash' not in json.dumps(admin.state()),'Password hashes never exposed in state')
    check(not ath.state()['accounts'],'Athlete cannot list account emails')
    ath.req('/api/auth/role',{'role':'federation'},403)
    ministry.cmd('event-save',{},403)
    admin.req('/api/commands/organization',{'name':'Forged'},400,headers={'X-CSRF':'invalid'})
    check(True,'Role escalation, ministry writes and forged CSRF rejected')
    e=admin.cmd('event-save',{'title':'Integration Cup','date':day(7),'deadline':day(5),'discipline':0,'capacity':10,'minAge':18,'maxAge':99,'city':'Оренбург','venue':'Тестовая арена','format':'Очный','level':'regional','type':'Кубок','calendarPlan':'Региональный','calendarNumber':'TEST','regulation':'Фиктивный регламент для тестов','requiredDocuments':['consent','medical'],'revision':0})
    eid=e['id'];admin.cmd('event-status',{'id':eid,'status':'open'})
    coach.cmd('apply',{'event':eid,'person':'p1','consent':True},403)
    link=coach.cmd('coach-request',{'person':'p1','discipline':0})
    maria.cmd('coach-confirm',{'id':link['id'],'accept':True},403)
    ath.cmd('coach-confirm',{'id':link['id'],'accept':True})
    ath.cmd('coach-confirm',{'id':link['id'],'accept':True},409)
    check(True,'Only target athlete can confirm coach; duplicate confirmation rejected')
    application=coach.cmd('apply',{'event':eid,'person':'p1','consent':True})
    ath.cmd('apply',{'event':eid,'person':'p1','consent':True},409)
    admin.cmd('application-review',{'id':application['id'],'status':'approved'},400)
    check(any(x.get('application')==application['id'] for x in admin.state()['risks']),'Incomplete application appears in federation risk radar')
    boundary='OrencodeBoundary'
    body=(f'--{boundary}\r\nContent-Disposition: form-data; name="person"\r\n\r\np1\r\n--{boundary}\r\nContent-Disposition: form-data; name="kind"\r\n\r\nmedical\r\n--{boundary}\r\nContent-Disposition: form-data; name="file"; filename="fixture.pdf"\r\nContent-Type: application/pdf\r\n\r\n%PDF-1.4\nfixture only\n%%EOF\r\n--{boundary}--\r\n').encode()
    doc=ath.req('/api/documents',body,headers={'Content-Type':'multipart/form-data; boundary='+boundary})
    maria.req('/api/documents/'+doc['id'],status=403)
    ath.cmd('application-documents',{'id':application['id'],'documentIds':[doc['id']]})
    admin.cmd('application-review',{'id':application['id'],'status':'approved'})
    check(True,'Document ownership enforced; complete application approved')
    # Judges cannot publish before assignment; expired categories rejected.
    judge.cmd('publish',{'event':eid,'scores':[{'person':'p1','score':300}]},403)
    admin.cmd('assign',{'event':eid,'judges':[{'person':'j1','position':'Главный судья'}]})
    check(any(x['id']==application['id'] for x in judge.state()['applications']),'Assigned judge sees admitted participants')
    judge.cmd('publish',{'event':eid,'scores':[{'person':'p1','score':1001}]},400)
    judge.cmd('publish',{'event':eid,'scores':[{'person':'p1','score':300}]})
    judge.cmd('publish',{'event':eid,'scores':[{'person':'p1','score':300}]},409)
    results=[x for x in ath.state()['results'] if x['event']==eid]
    check(len(results)==1 and results[0]['place']==1,'Published protocol persisted exactly once with calculated place')
    rating=next(x for x in admin.state()['rating'] if x['person']=='p1')
    check(rating['points']==84 and rating['coefficient']==1.2,'Rating uses place-level points and two-podium coefficient')
    admin.cmd('roster',{'person':'p1','discipline':0,'year':date.today().year})
    check(any(x['person']=='p1' for x in ministry.state()['roster']),'Ministry can read regional roster')
    for kind,ident,entry in [('protocol',eid,'word/document.xml'),('history','p1','word/document.xml'),('judges',eid,'word/document.xml'),('census','','xl/worksheets/sheet1.xml'),('roster','','word/document.xml')]:
        content,_=admin.req('/api/export/'+kind+'?id='+ident,raw=True)
        z=zipfile.ZipFile(io.BytesIO(content));check(entry in z.namelist(),kind+' export is valid OpenXML archive')
        import xml.etree.ElementTree as ET
        for file in z.namelist():ET.fromstring(z.read(file))
    maria.req('/api/export/history?id=p1',status=403)
    certificate=ath.cmd('certificate',{'result':results[0]['id']})
    public,_=anon.req('/verify/'+certificate['token'],raw=True)
    check('Алексей'.encode() not in public and 'Справка подтверждена'.encode() in public,'QR verification is public and minimizes personal data')
    printed,_=ath.req('/api/certificates/'+certificate['id']+'/print',raw=True)
    check(b'<svg' in printed,'Certificate contains generated QR SVG')
    maria.req('/api/certificates/'+certificate['id']+'/print',status=403)
    q=ath.cmd('qualification',{'person':'p1','type':'rank','value':'II разряд','resultId':results[0]['id']})
    admin.cmd('qualification-review',{'id':q['id'],'status':'approved','from':day(),'until':day(365),'order':'ДЕМО-1'})
    check(next(x for x in ath.state()['people'] if x['id']=='p1')['rank']=='II разряд','Approved rank updates profile with expiry and order')
    group=coach.cmd('group',{'name':'Test group','org':'o1','discipline':0,'stage':0,'members':['p1']})
    coach.cmd('lesson',{'group':group['id'],'date':day(),'topic':'Алгоритмы','format':'online','evidence':'http://insecure.invalid','present':['p1']},400)
    coach.cmd('lesson',{'group':group['id'],'date':day(),'topic':'Алгоритмы','format':'online','evidence':'https://example.invalid/recording','present':['p1']})
    standard=admin.cmd('standard',{'name':'Учебный тест','discipline':0,'stage':0,'sex':'male','unit':'зн/мин','threshold':100,'comparison':'gte','source':'Test fixture, not an official rule','verified':False})
    coach.cmd('assessment',{'standard':standard['id'],'person':'p1','value':120})
    coach.cmd('promote',{'person':'p1','discipline':0},400)
    standard['verified']=True;admin.cmd('standard',standard)
    coach.cmd('promote',{'person':'p1','discipline':0},400)
    coach.cmd('assessment',{'standard':standard['id'],'person':'p1','value':120})
    coach.cmd('promote',{'person':'p1','discipline':0})
    check(next(x for x in ath.state()['people'] if x['id']=='p1')['stage']==1,'Promotion requires verified rules and current assessment snapshots')
    prize=admin.state()['prizeRules'][0];prize['verified']=True;prize['source']='Test fixture, not official amounts';admin.cmd('prize-rule',prize)
    petition=admin.cmd('petition',{'year':date.today().year});check(len(petition['candidates'])>=1,'Prize candidates calculated from published results')
    admin.cmd('petition-status',{'id':petition['id'],'status':'awarded','reference':'test'},400)
    admin.cmd('petition-status',{'id':petition['id'],'status':'sent','reference':'test'})
    check(True,'Petition state transitions enforced')
    token=admin.cmd('api-token',{'name':'Read only','until':day(5),'scopes':['events:read']})
    anon.req('/api/integrations/events',headers={'Authorization':'Bearer '+token['token']})
    anon.req('/api/integrations/results',headers={'Authorization':'Bearer '+token['token']},status=403)
    admin.cmd('token-revoke',{'id':token['id']});anon.req('/api/integrations/events',headers={'Authorization':'Bearer '+token['token']},status=401)
    check(True,'Integration token scopes and revocation enforced')
    # OIDC consent -> code -> PKCE -> signed ID token -> userinfo; replay is rejected.
    verifier='a'*64;challenge=base64.urlsafe_b64encode(hashlib.sha256(verifier.encode()).digest()).decode().rstrip('=')
    params={'client_id':'demo-client','redirect_uri':BASE+'/oidc-demo.html','response_type':'code','scope':'openid profile','code_challenge_method':'S256','code_challenge':challenge,'state':'test-state','nonce':'test-nonce'}
    consent,_=ath.req('/connect/authorize?'+urllib.parse.urlencode(params),raw=True)
    check('Разрешить'.encode() in consent,'OIDC displays explicit consent')
    bad=dict(params,redirect_uri='https://evil.invalid/callback');ath.req('/connect/authorize?'+urllib.parse.urlencode(bad),status=400)
    _,h=ath.req('/connect/authorize',urllib.parse.urlencode(dict(params,decision='allow')).encode(),status=302,headers={'Content-Type':'application/x-www-form-urlencoded'},raw=True)
    code=urllib.parse.parse_qs(urllib.parse.urlparse(h['Location']).query)['code'][0]
    tokenform={'grant_type':'authorization_code','client_id':'demo-client','redirect_uri':params['redirect_uri'],'code':code,'code_verifier':'b'*64}
    anon.req('/connect/token',urllib.parse.urlencode(tokenform).encode(),status=400,headers={'Content-Type':'application/x-www-form-urlencoded'})
    tokenform['code_verifier']=verifier
    oidc=anon.req('/connect/token',urllib.parse.urlencode(tokenform).encode(),headers={'Content-Type':'application/x-www-form-urlencoded'})
    anon.req('/connect/token',urllib.parse.urlencode(tokenform).encode(),status=400,headers={'Content-Type':'application/x-www-form-urlencoded'})
    parts=oidc['id_token'].split('.');decode=lambda x:base64.urlsafe_b64decode(x+'='*(-len(x)%4));claims=json.loads(decode(parts[1]));check(claims['nonce']=='test-nonce' and claims['aud']=='demo-client' and claims['iss']==BASE,'OIDC ID token has expected issuer, audience and nonce')
    jwk=anon.req('/.well-known/jwks.json')['keys'][0];n=int.from_bytes(decode(jwk['n']),'big');ee=int.from_bytes(decode(jwk['e']),'big');sig=int.from_bytes(decode(parts[2]),'big');em=pow(sig,ee,n).to_bytes((n.bit_length()+7)//8,'big');digest=hashlib.sha256((parts[0]+'.'+parts[1]).encode()).digest();tail=bytes.fromhex('3031300d060960864801650304020105000420')+digest
    check(em==b'\0\1'+b'\xff'*(len(em)-len(tail)-3)+b'\0'+tail,'OIDC RS256 signature cryptographically verified')
    info=anon.req('/connect/userinfo',headers={'Authorization':'Bearer '+oidc['access_token']});check(info['sub']=='p1' and 'athlete' in info['roles'],'OIDC userinfo matches authenticated athlete')
    # Capacity under concurrent requests, using separate real sessions.
    capacity=dict(e);capacity.pop('id');capacity.update(title='Last seat',revision=0,capacity=1,requiredDocuments=['consent']);limited=admin.cmd('event-save',capacity);admin.cmd('event-status',{'id':limited['id'],'status':'open'})
    def apply(c,p):
        try:c.cmd('apply',{'event':limited['id'],'person':p,'consent':True});return 200
        except AssertionError as err:
            if 'got 409' in str(err):return 409
            raise
    with concurrent.futures.ThreadPoolExecutor(2) as pool:statuses=list(pool.map(lambda args:apply(*args),[(ath,'p1'),(maria,'p2')]))
    check(sorted(statuses)==[200,409],'Concurrent requests cannot overbook last seat')
    # Persistence including encrypted auth cookie keys survives application restart.
    server.stop();server.start();persisted=ath.state()
    check(any(x['event']==eid for x in persisted['results']),'SQLite results and login cookie survive process restart')
    for _ in range(80):
        if MAIL:break
        time.sleep(.25)
    check(bool(MAIL),'External email channel delivered a message to a real SMTP sink')
    check(any(x['status']=='sent' for x in admin.state()['outbox']),'SMTP delivery status recorded after successful send')
    admin.cmd('certificate-revoke',{'id':certificate['id']});public,_=anon.req('/verify/'+certificate['token'],raw=True)
    check('Справка отозвана'.encode() in public,'Revoked certificate is visibly invalid')
    # Pause removes coach access immediately.
    ath.cmd('coach-status',{'id':link['id'],'status':'paused'});coach.cmd('assessment',{'standard':standard['id'],'person':'p1','value':130},403)
    check(True,'Paused relationship removes coach write access')

if __name__=='__main__':
    smtp=socketserver.ThreadingTCPServer(('127.0.0.1',0),SMTP);threading.Thread(target=smtp.serve_forever,daemon=True).start()
    with tempfile.TemporaryDirectory(prefix='orencode-test-') as temp:
        server=Server(temp,smtp.server_address[1])
        try:
            server.start();run(server)
            print('\n'.join('PASS '+c for c in checks));print(f'\n{len(checks)} integration checks passed')
        except Exception:
            server.log.flush();print(pathlib.Path(temp,'server.log').read_text()[-7000:]);raise
        finally:server.stop();smtp.shutdown();smtp.server_close()
