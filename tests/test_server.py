import hashlib, http.client, importlib.util, json, tempfile, threading, time, unittest
from pathlib import Path
spec=importlib.util.spec_from_file_location('robinshare',Path(__file__).parents[1]/'server/robinshare.py')
mod=importlib.util.module_from_spec(spec);spec.loader.exec_module(mod)

class TransferTests(unittest.TestCase):
    def setUp(self):
        self.temp=tempfile.TemporaryDirectory()
        self.store=mod.Store(self.temp.name,max_bytes=20*1024**2,quota=100*1024**2)
        self.server=mod.Server(('127.0.0.1',0),self.store,'https://share.example')
        self.thread=threading.Thread(target=self.server.serve_forever,daemon=True);self.thread.start()
    def tearDown(self):
        self.server.shutdown();self.server.server_close();self.thread.join();self.temp.cleanup()
    def request(self,path,method='GET',data=None,headers=None,raw=False):
        conn=http.client.HTTPConnection('127.0.0.1',self.server.server_port,timeout=10)
        payload=data if isinstance(data,bytes) else json.dumps(data).encode() if data is not None else None
        h=dict(headers or {})
        if data is not None and not isinstance(data,bytes): h['Content-Type']='application/json'
        conn.request(method,path,payload,h);r=conn.getresponse();body=r.read();status=r.status;res_headers=dict(r.getheaders());conn.close()
        return status,body if raw else json.loads(body),res_headers
    def new(self,content=b'Hallo Welt',target=None,identity=None):
        h={'Authorization':'Bearer '+identity['token']} if identity else {}
        status,u,_=self.request('/api/transfers','POST',{'filename':'../../Foto ä.txt','size':len(content),'target_id':target},h)
        self.assertEqual(status,201,u);headers={'Upload-Token':u['upload_token']}
        if content:
            status,r,_=self.request('/api/transfers/'+u['id']+'/content','PUT',content,{**headers,'Upload-Offset':'0'});self.assertEqual(status,200,r)
        status,j,_=self.request('/api/transfers/'+u['id']+'/complete','POST',{},headers);self.assertEqual(status,200,j)
        return u,j
    def device(self,name):
        status,d,_=self.request('/api/devices','POST',{'name':name,'platform':'test'});self.assertEqual(status,201);return d
    def test_roundtrip_checksum_range_ack(self):
        payload=bytes(range(256))*8192
        _,j=self.new(payload);t=j['transfer'];self.assertEqual(t['sha256'],hashlib.sha256(payload).hexdigest());self.assertEqual(t['filename'],'Foto ä.txt')
        status,body,headers=self.request(t['download_url'],raw=True);self.assertEqual((status,body),(200,payload));self.assertEqual(headers['Content-Length'],str(len(payload)))
        status,body,headers=self.request(t['download_url'],headers={'Range':'bytes=100-199'},raw=True);self.assertEqual((status,body),(206,payload[100:200]))
        self.assertEqual(self.request('/api/lookup/pin/'+j['pin'])[0],200)
        self.assertEqual(self.request(t['ack_url'],'POST',{})[0],200)
        self.assertEqual(self.request('/api/lookup/pin/'+j['pin'])[0],404)
    def test_upload_offset_retry_and_authorization(self):
        _,u,_=self.request('/api/transfers','POST',{'filename':'x','size':6});p='/api/transfers/'+u['id'];h={'Upload-Token':u['upload_token']}
        self.assertEqual(self.request(p+'/content','PUT',b'abc',{'Upload-Offset':'0'})[0],403)
        self.assertEqual(self.request(p+'/content','PUT',b'abc',{**h,'Upload-Offset':'0'})[0],200)
        self.assertEqual(self.request(p+'/content','PUT',b'abc',{**h,'Upload-Offset':'0'})[0],409)
        self.assertEqual(self.request(p,headers=h)[1]['offset'],3)
        self.assertEqual(self.request(p+'/complete','POST',{},h)[0],409)
        self.assertEqual(self.request(p+'/content','PUT',b'def',{**h,'Upload-Offset':'3'})[0],200)
        self.assertEqual(self.request(p+'/complete','POST',{},h)[0],200)
    def test_device_pairing_is_one_time_and_inbox_private(self):
        a,b,c=[self.device(n) for n in ['A','B','C']]
        ha={'Authorization':'Bearer '+a['token']};hb={'Authorization':'Bearer '+b['token']};hc={'Authorization':'Bearer '+c['token']}
        self.assertEqual(self.request('/api/devices/pair','POST',{'code':b['pair_code']},ha)[0],200)
        self.assertEqual(self.request('/api/devices/pair','POST',{'code':b['pair_code']},hc)[0],404)
        _,j=self.new(target=b['id'],identity=a)
        self.assertEqual(len(self.request('/api/devices/me',headers=hb)[1]['inbox']),1)
        self.assertEqual(len(self.request('/api/devices/me',headers=hc)[1]['inbox']),0)
        self.assertEqual(self.request('/api/transfers','POST',{'filename':'x','size':0,'target_id':b['id']},hc)[0],403)
        self.assertEqual(self.request('/api/devices/'+b['id'],'DELETE',headers=ha)[0],200)
        self.assertEqual(self.request('/api/transfers','POST',{'filename':'x','size':0,'target_id':b['id']},ha)[0],403)
    def test_empty_file_and_wrong_download_capability(self):
        _,j=self.new(b'');t=j['transfer']
        self.assertEqual(self.request(t['download_url'],raw=True)[1],b'')
        self.assertEqual(self.request(t['download_url'].split('?')[0]+'?access=wrong')[0],403)
    def test_expiration_and_invalid_inputs(self):
        _,j=self.new()
        with self.store.db() as db:db.execute('UPDATE shares SET expires=?',(time.time()-1,))
        self.store.cleanup();self.assertEqual(self.request('/api/lookup/pin/'+j['pin'])[0],404)
        self.assertEqual(self.request('/api/transfers','POST',{'size':-1})[0],413)
        self.assertEqual(self.request('/api/transfers','POST',{'size':1024**3})[0],413)
        self.assertEqual(self.request('/api/transfers','POST',b'not json')[0],400)
        self.assertEqual(self.request('/api/lookup/pin/12abc3')[0],400)
    def test_cross_origin_and_pin_rate_limit(self):
        self.assertEqual(self.request('/api/devices','POST',{'name':'x'}, {'Origin':'https://evil.example'})[0],403)
        for _ in range(20):self.assertEqual(self.request('/api/lookup/pin/000000')[0],404)
        self.assertEqual(self.request('/api/lookup/pin/000000')[0],429)
    def test_html_and_configuration(self):
        self.assertEqual(self.request('/api/config')[1]['port'],self.server.server_port)
        status,html,h=self.request('/',raw=True);self.assertEqual(status,200);self.assertIn(b'lang="de"',html);self.assertIn('Content-Security-Policy',h)
    def test_no_ready_share_before_finalization(self):
        _,u,_=self.request('/api/transfers','POST',{'filename':'test','size':0})
        with self.store.db() as db:r=db.execute('SELECT * FROM shares WHERE id=?',(u['id'],)).fetchone()
        self.assertEqual(self.request('/api/lookup/pin/'+r['pin'])[0],404)
    def test_filename_sanitization(self):
        self.assertEqual(mod.filename('..\\..\\CON.txt'),'_CON.txt');self.assertEqual(mod.filename('\r\n'),'Datei')

if __name__=='__main__':unittest.main()
