#!/usr/bin/env python3
"""Robins Share • Python 3.11+ • Keine Zusatzpakete • Standardport 5055."""
from __future__ import annotations
import argparse, hashlib, hmac, json, mimetypes, os, re, secrets, shutil, sqlite3, threading, time
from collections import defaultdict, deque
from http import HTTPStatus
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from urllib.parse import parse_qs, quote, urlsplit

VERSION = '1.0.0'
CHUNK = 8 * 1024 * 1024
INDEX_HTML = ''  # Wird beim Paketbau eingebettet.

class Problem(Exception):
    def __init__(self, status, message): self.status, self.message = status, message

def filename(value):
    value = str(value or 'Datei').replace('\\', '/').split('/')[-1]
    value = ''.join(c for c in value if ord(c) >= 32 and c not in '<>:"|?*').strip(' .')[:180]
    if not value: value = 'Datei'
    if value.split('.')[0].upper() in {'CON','PRN','AUX','NUL',*[f'COM{i}' for i in range(10)],*[f'LPT{i}' for i in range(10)]}: value = '_' + value
    return value

class Store:
    def __init__(self, root, ttl=3600, max_bytes=100*1024**3, quota=500*1024**3):
        self.root, self.ttl, self.max_bytes, self.quota = Path(root), ttl, max_bytes, quota
        self.root.mkdir(parents=True, exist_ok=True)
        try: self.root.chmod(0o700)
        except OSError: pass
        self.lock = threading.RLock()
        self.limits = defaultdict(deque)
        with self.db() as db:
            db.executescript('''
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS devices(id TEXT PRIMARY KEY, token TEXT UNIQUE, code TEXT UNIQUE, code_until REAL, name TEXT, platform TEXT, seen REAL);
            CREATE TABLE IF NOT EXISTS pairs(a TEXT, b TEXT, PRIMARY KEY(a,b));
            CREATE TABLE IF NOT EXISTS shares(id TEXT PRIMARY KEY, secret TEXT, upload TEXT, pin TEXT UNIQUE, name TEXT, mime TEXT, size INTEGER, offset INTEGER, sha256 TEXT, expires REAL, owner TEXT, target TEXT, ready INTEGER DEFAULT 0);
            CREATE INDEX IF NOT EXISTS shares_target ON shares(target,ready);
            ''')
        self.cleanup()
    def db(self):
        db=sqlite3.connect(self.root/'shares.sqlite3', timeout=30)
        db.row_factory=sqlite3.Row
        return db
    def rate(self, ip, bucket, limit=30):
        with self.lock:
            q=self.limits[(ip,bucket)]; t=time.time()
            while q and q[0] < t-60: q.popleft()
            if len(q)>=limit: raise Problem(429, 'Zu viele Versuche. Bitte eine Minute warten.')
            q.append(t)
            if len(self.limits)>10000:
                self.limits={k:v for k,v in self.limits.items() if v and v[-1]>t-60}
                self.limits=defaultdict(deque,self.limits)
    def cleanup(self):
        with self.lock, self.db() as db:
            old=db.execute('SELECT id FROM shares WHERE expires < ?', (time.time(),)).fetchall()
            for r in old: (self.root/r['id']).unlink(missing_ok=True)
            db.execute('DELETE FROM shares WHERE expires < ?', (time.time(),))
    def device(self, token):
        with self.db() as db:
            d=db.execute('SELECT * FROM devices WHERE token=?',(token,)).fetchone()
            if not d: raise Problem(401,'Gerät bitte erneut verbinden.')
            db.execute('UPDATE devices SET seen=? WHERE id=?',(time.time(),d['id']))
            return d
    def card(self, r):
        return dict(id=r['id'],filename=r['name'],size=r['size'],mime=r['mime'],sha256=r['sha256'],expires_at=int(r['expires']),
                    download_url=f"/api/transfer/{r['id']}/download?access={r['secret']}",
                    ack_url=f"/api/transfer/{r['id']}/ack?access={r['secret']}")
    def share(self, ident):
        with self.db() as db: r=db.execute('SELECT * FROM shares WHERE id=? AND expires>?',(ident,time.time())).fetchone()
        if not r: raise Problem(404,'Datei nicht gefunden oder abgelaufen.')
        return r
    def authorize(self, ident, secret, upload=False):
        r=self.share(ident)
        if not hmac.compare_digest(r['upload'] if upload else r['secret'],secret or ''): raise Problem(403,'Zugriff verweigert.')
        return r

class Handler(BaseHTTPRequestHandler):
    protocol_version='HTTP/1.1'
    server_version='RobinShare/1'
    def log_message(self, fmt, *args):
        # URLs enthalten Freigabeschlüssel: niemals in Logs schreiben.
        if args and str(args[0]).startswith(('GET','POST','PUT','DELETE','HEAD')): return
    @property
    def store(self): return self.server.store
    @property
    def bearer(self): return self.headers.get('Authorization','').removeprefix('Bearer ')
    def respond(self,status=200,data=None,body=None,ctype='application/json; charset=utf-8',extra=None):
        if body is None: body=json.dumps(data or {},ensure_ascii=False).encode()
        self.send_response(status)
        self.send_header('Content-Type',ctype)
        self.send_header('Content-Length',str(len(body)))
        self.send_header('Cache-Control','no-store')
        self.send_header('X-Content-Type-Options','nosniff')
        self.send_header('Referrer-Policy','no-referrer')
        self.send_header('X-Frame-Options','DENY')
        self.send_header('Content-Security-Policy',"default-src 'self'; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline'; img-src 'self' blob: data:; media-src 'self' blob:; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'")
        for k,v in (extra or {}).items(): self.send_header(k,v)
        self.end_headers()
        if self.command!='HEAD': self.wfile.write(body)
    def read_body(self, maximum=CHUNK):
        if self.headers.get('Transfer-Encoding'): raise Problem(411,'Content-Length erforderlich.')
        try: length=int(self.headers.get('Content-Length','0'))
        except ValueError: raise Problem(400,'Ungültige Länge.')
        if length<0 or length>maximum: raise Problem(413,'Anfrage zu groß.')
        data=self.rfile.read(length)
        if len(data)!=length: raise Problem(400,'Übertragung unterbrochen.')
        return data
    def json_body(self):
        try:
            data=json.loads(self.read_body(65536))
            if not isinstance(data,dict): raise ValueError()
            return data
        except (ValueError,UnicodeError): raise Problem(400,'Ungültige JSON-Daten.')
    def do_HEAD(self): self.handle_request()
    def do_GET(self): self.handle_request()
    def do_POST(self): self.handle_request()
    def do_PUT(self): self.handle_request()
    def do_DELETE(self): self.handle_request()
    def handle_request(self):
        self.connection.settimeout(120)
        try:
            self.route()
        except Problem as e:
            self.close_connection=True
            self.respond(e.status,dict(ok=False,error=e.message))
        except (BrokenPipeError,ConnectionResetError,TimeoutError): self.close_connection=True
        except Exception:
            self.close_connection=True
            self.respond(500,dict(ok=False,error='Interner Fehler. Bitte erneut versuchen.'))
    def route(self):
        method=self.command
        url=urlsplit(self.path); path=url.path; qs=parse_qs(url.query)
        store=self.store
        # Mutationen im Browser müssen same-origin sein. Native Clients senden keinen Origin.
        origin=self.headers.get('Origin')
        if origin and method not in ('GET','HEAD'):
            if urlsplit(origin).netloc!=self.headers.get('Host'): raise Problem(403,'Fremde Herkunft nicht erlaubt.')
        if method in ('GET','HEAD') and path=='/':
            html=INDEX_HTML
            if not html:
                source=Path(__file__).parent.parent/'web'/'index.html'
                html=source.read_text() if source.exists() else '<h1>Robins Share</h1>'
            return self.respond(body=html.encode(),ctype='text/html; charset=utf-8')
        if method=='GET' and path=='/api/config':
            return self.respond(data=dict(ok=True,name='Robins Share',version=VERSION,port=self.server.server_port,max_bytes=store.max_bytes,chunk_bytes=CHUNK,ttl_seconds=store.ttl))
        if method=='POST' and path=='/api/devices':
            store.rate(self.client_address[0],'register',10)
            data=self.json_body(); ident=secrets.token_hex(16); token=secrets.token_urlsafe(32)
            with store.lock,store.db() as db:
                code=self.fresh_code(db,'devices','code')
                db.execute('INSERT INTO devices VALUES(?,?,?,?,?,?,?)',(ident,token,code,time.time()+600,str(data.get('name','Gerät'))[:60],str(data.get('platform','web'))[:20],time.time()))
            return self.respond(201,dict(ok=True,id=ident,token=token,pair_code=code))
        if path.startswith('/api/devices/'):
            me=store.device(self.bearer)
            if method=='GET' and path=='/api/devices/me':
                with store.lock,store.db() as db:
                    code=me['code']
                    if me['code_until']<time.time():
                        code=self.fresh_code(db,'devices','code')
                        db.execute('UPDATE devices SET code=?,code_until=? WHERE id=?',(code,time.time()+600,me['id']))
                    peers=db.execute('SELECT id,name,platform,seen FROM devices JOIN pairs ON devices.id=pairs.b WHERE pairs.a=?',(me['id'],)).fetchall()
                    inbox=db.execute('SELECT * FROM shares WHERE target=? AND ready=1 AND expires>?',(me['id'],time.time())).fetchall()
                return self.respond(data=dict(ok=True,id=me['id'],pair_code=code,devices=[dict(p) for p in peers],inbox=[store.card(r) for r in inbox]))
            if method=='POST' and path=='/api/devices/pair':
                store.rate(self.client_address[0],'pair',8)
                code=str(self.json_body().get('code',''))
                with store.lock,store.db() as db:
                    peer=db.execute('SELECT * FROM devices WHERE code=? AND code_until>?',(code,time.time())).fetchone()
                    if not peer or peer['id']==me['id']: raise Problem(404,'Kopplungscode ungültig oder abgelaufen.')
                    for a,b in ((me['id'],peer['id']),(peer['id'],me['id'])): db.execute('INSERT OR IGNORE INTO pairs VALUES(?,?)',(a,b))
                    db.execute('UPDATE devices SET code=?,code_until=? WHERE id=?',(self.fresh_code(db,'devices','code'),time.time()+600,peer['id']))
                return self.respond(data=dict(ok=True))
            if method=='DELETE' and re.fullmatch(r'/api/devices/[a-f0-9]{32}',path):
                peer=path.rsplit('/',1)[-1]
                with store.db() as db:
                    db.execute('DELETE FROM pairs WHERE (a=? AND b=?) OR (a=? AND b=?)',(me['id'],peer,peer,me['id']))
                    db.execute('UPDATE shares SET target=NULL WHERE owner=? AND target=?',(me['id'],peer))
                return self.respond(data=dict(ok=True))
        if method=='POST' and path=='/api/transfers':
            store.rate(self.client_address[0],'create',30); data=self.json_body()
            try: size=int(data['size'])
            except (KeyError,ValueError,TypeError): raise Problem(400,'Dateigröße fehlt.')
            if size<0 or size>store.max_bytes: raise Problem(413,'Datei überschreitet das Serverlimit.')
            owner=store.device(self.bearer)['id'] if self.bearer else None
            target=data.get('target_id')
            with store.lock,store.db() as db:
                if target and (not owner or not db.execute('SELECT 1 FROM pairs WHERE a=? AND b=?',(owner,target)).fetchone()): raise Problem(403,'Zielgerät zuerst koppeln.')
                used=db.execute('SELECT COALESCE(SUM(size),0) FROM shares').fetchone()[0]
                if used+size>store.quota or shutil.disk_usage(store.root).free<size+64*1024**2: raise Problem(507,'Zu wenig Speicherplatz auf dem Server.')
                ident=secrets.token_hex(16); upload=secrets.token_urlsafe(32)
                pin=self.fresh_code(db,'shares','pin')
                db.execute('INSERT INTO shares VALUES(?,?,?,?,?,?,?,?,?,?,?,?,0)',(ident,secrets.token_urlsafe(36),upload,pin,filename(data.get('filename')),str(data.get('mime','application/octet-stream'))[:100],size,0,'',time.time()+store.ttl,owner,target))
                (store.root/ident).touch()
            return self.respond(201,dict(ok=True,id=ident,upload_token=upload,offset=0,chunk_bytes=CHUNK))
        match=re.fullmatch(r'/api/transfers/([a-f0-9]{32})(?:/(content|complete))?',path)
        if match:
            ident,action=match.groups()
            with store.lock:
                r=store.authorize(ident,self.headers.get('Upload-Token',''),True)
                if method=='GET' and not action: return self.respond(data=dict(ok=True,offset=r['offset'],size=r['size'],ready=bool(r['ready'])))
                if method=='DELETE' and not action:
                    self.remove(ident); return self.respond(data=dict(ok=True))
                if method=='PUT' and action=='content':
                    if r['ready']: raise Problem(409,'Übertragung bereits abgeschlossen.')
                    try: offset=int(self.headers.get('Upload-Offset','-1'))
                    except ValueError: raise Problem(400,'Ungültiger Upload-Offset.')
                    if offset!=r['offset']: raise Problem(409,'Upload-Offset stimmt nicht. Status neu abfragen.')
                    data=self.read_body()
                    if not data or offset+len(data)>r['size']: raise Problem(400,'Ungültige Blockgröße.')
                    with (store.root/ident).open('r+b') as f:
                        f.truncate(offset); f.seek(offset); f.write(data); f.flush()
                    with store.db() as db: db.execute('UPDATE shares SET offset=?,expires=? WHERE id=?',(offset+len(data),time.time()+store.ttl,ident))
                    return self.respond(data=dict(ok=True,offset=offset+len(data)))
                if method=='POST' and action=='complete':
                    self.read_body(65536)
                    if r['offset']!=r['size']: raise Problem(409,'Es fehlen noch Dateiblöcke.')
                    if not r['ready']:
                        with (store.root/ident).open('rb') as f: digest=hashlib.file_digest(f,'sha256').hexdigest()
                        with store.db() as db: db.execute('UPDATE shares SET ready=1,sha256=? WHERE id=?',(digest,ident))
                    r=store.share(ident)
                    base=self.server.public_url
                    return self.respond(data=dict(ok=True,pin=r['pin'],link=f"{base}/?share={r['secret']}",transfer=store.card(r)))
        match=re.fullmatch(r'/api/lookup/(pin|link)/([^/]+)',path)
        if method=='GET' and match:
            kind,key=match.groups(); store.rate(self.client_address[0],'lookup',20)
            if kind=='pin' and not re.fullmatch('[0-9]{6}',key): raise Problem(400,'Bitte sechs Ziffern eingeben.')
            with store.db() as db: r=db.execute(f"SELECT * FROM shares WHERE {'pin' if kind=='pin' else 'secret'}=? AND ready=1 AND expires>?",(key,time.time())).fetchone()
            if not r: raise Problem(404,'Code nicht gefunden oder abgelaufen.')
            return self.respond(data=dict(ok=True,transfer=store.card(r)))
        match=re.fullmatch(r'/api/transfer/([a-f0-9]{32})/(download|ack)',path)
        if match:
            ident,action=match.groups(); r=store.authorize(ident,qs.get('access',[''])[0])
            if not r['ready']: raise Problem(409,'Datei wird noch hochgeladen.')
            if action=='ack' and method=='POST':
                self.read_body(65536)
                self.remove(ident)
                return self.respond(data=dict(ok=True))
            if action=='download' and method in ('GET','HEAD'): return self.send_download(r)
        raise Problem(404,'Diese Adresse ist nicht vorhanden.')
    def fresh_code(self,db,table,column):
        for _ in range(100):
            code=f'{secrets.randbelow(1000000):06d}'
            if not db.execute(f'SELECT 1 FROM {table} WHERE {column}=?',(code,)).fetchone(): return code
        raise Problem(503,'Momentan kein Code verfügbar.')
    def remove(self,ident):
        with self.store.lock,self.store.db() as db:
            (self.store.root/ident).unlink(missing_ok=True)
            db.execute('DELETE FROM shares WHERE id=?',(ident,))
    def send_download(self,r):
        start,end=0,r['size']-1; partial=False
        value=self.headers.get('Range')
        if value:
            m=re.fullmatch(r'bytes=(\d+)-(\d*)',value)
            if not m: raise Problem(416,'Dieser Bytebereich ist nicht verfügbar.')
            start=int(m[1]); end=int(m[2]) if m[2] else end
            if start>end or start>=r['size']: raise Problem(416,'Dieser Bytebereich ist nicht verfügbar.')
            end=min(end,r['size']-1); partial=True
        try: f=(self.store.root/r['id']).open('rb')
        except FileNotFoundError: raise Problem(404,'Datei nicht mehr vorhanden.')
        with f:
            length=max(0,end-start+1)
            self.send_response(206 if partial else 200)
            headers={'Content-Type':'application/octet-stream','Content-Length':str(length),'Accept-Ranges':'bytes','ETag':'"'+r['sha256']+'"','Content-Disposition':"attachment; filename*=UTF-8''"+quote(r['name']),'Cache-Control':'no-store','X-Content-Type-Options':'nosniff','Referrer-Policy':'no-referrer'}
            if partial: headers['Content-Range']=f"bytes {start}-{end}/{r['size']}"
            for k,v in headers.items(): self.send_header(k,v)
            self.end_headers()
            if self.command=='HEAD': return
            f.seek(start)
            while length:
                chunk=f.read(min(1024*1024,length))
                if not chunk: break
                self.wfile.write(chunk); length-=len(chunk)

class Server(ThreadingHTTPServer):
    daemon_threads=True
    request_queue_size=128
    def __init__(self, address, store, public_url=''):
        self.store,self.public_url=store,public_url.rstrip('/')
        super().__init__(address,Handler)
    def service_actions(self):
        if time.monotonic()-getattr(self,'_cleaned',0)>30:
            self.store.cleanup(); self._cleaned=time.monotonic()

def main():
    parser=argparse.ArgumentParser(description='Robins Share – Dateiübertragung auf Port 5055')
    parser.add_argument('--host',default=os.getenv('ROBINS_HOST',os.getenv('ROBBINS_DATEISHARE_HOST','0.0.0.0')))
    parser.add_argument('--port',type=int,default=int(os.getenv('ROBINS_PORT',os.getenv('ROBBINS_DATEISHARE_PORT','5055'))))
    parser.add_argument('--data',default=os.getenv('ROBINS_DATA_DIR',str(Path(__file__).resolve().parent/'robins_dateishare_daten')))
    parser.add_argument('--public-url',default=os.getenv('ROBINS_PUBLIC_URL','https://share.rjuhas.eu'))
    args=parser.parse_args()
    store=Store(args.data,ttl=int(os.getenv('ROBINS_TTL_SECONDS','3600')),max_bytes=int(os.getenv('ROBINS_MAX_MB','102400'))*1024**2,quota=int(os.getenv('ROBINS_QUOTA_MB','512000'))*1024**2)
    server=Server((args.host,args.port),store,args.public_url)
    print(f'Robins Share {VERSION} läuft auf http://{args.host}:{args.port} – Daten: {args.data}',flush=True)
    try: server.serve_forever()
    except KeyboardInterrupt: pass
    finally: server.server_close()
if __name__=='__main__': main()
