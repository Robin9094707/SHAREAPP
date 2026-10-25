using System.IO;
using System.Net;
namespace RobinShare;
internal static class CoreTests
{
    public static int Run()
    {
        try
        {
            using(var iconStream=System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/RobinShare.ico"))!.Stream)
            {
                var decoder=System.Windows.Media.Imaging.BitmapDecoder.Create(iconStream,System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat,System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
                Assert(decoder.Frames.Count>=1,"WPF kann das tatsächlich eingebettete Icon dekodieren");
            }
            Assert(Safety.Filename("../../video.mov")=="video.mov","Pfadbereinigung");
            Assert(Safety.Filename("CON.txt")=="_CON.txt","Reservierte Dateinamen");
            Throws(()=>Safety.Server("http://example.org"));
            Throws(()=>Safety.Server("https://user:pass@example.org"));
            Throws(()=>Safety.Endpoint(Safety.Server("https://example.org"),"//evil.example/api/file"));
            Assert(Safety.Endpoint(Safety.Server("https://example.org"),"/api/test").Host=="example.org","Gleicher Server");
            Assert(!Discovery.IsQuery([]),"Kurze DNS-Pakete");
            var hostile=new byte[18];hostile[5]=1;hostile[12]=0xc0;hostile[13]=12;Assert(!Discovery.IsQuery(hostile),"Zyklische DNS-Zeiger");
            Assert(!Discovery.IsQuery(Discovery.Build("PC",IPAddress.Parse("192.168.1.5"),new string('a',64))),"Antworten sind keine Anfragen");
            var json="{\"id\":\"x\",\"filename\":\"video\",\"size\":53687091200,\"mime\":\"file\",\"sha256\":\"abc\",\"expires_at\":1800000000,\"download_url\":\"/api/x\",\"ack_url\":\"/api/x\"}";
            var t=System.Text.Json.JsonSerializer.Deserialize<Transfer>(json,Json.Options)!;Assert(t.Size==50L*1024*1024*1024,"64-Bit-Dateigröße");
            var file=Path.GetTempFileName();try{File.WriteAllText(file,"abc");Assert(Safety.Hash(file).GetAwaiter().GetResult()=="ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad","Streaming-Prüfsumme");}finally{File.Delete(file);}
            LocalRoundTrip().GetAwaiter().GetResult();
            return 0;
        }
        catch(Exception e){File.WriteAllText(Path.Combine(Path.GetTempPath(),"RobinsShare-test-error.txt"),e.ToString());return 1;}
    }
    private static async Task LocalRoundTrip()
    {
        var settings=new Settings();await using var server=new LocalServer(settings);await server.Start();
        using var handler=new System.Net.Http.HttpClientHandler{AllowAutoRedirect=false,ServerCertificateCustomValidationCallback=(_,cert,_,_)=>cert!=null && Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(cert.RawData)).ToLowerInvariant()==server.Fingerprint};
        using var client=new System.Net.Http.HttpClient(handler){BaseAddress=new Uri("https://127.0.0.1:"+LocalServer.Port)};
        async Task<System.Text.Json.JsonElement> Post(string path,object body)
        {
            using var content=new System.Net.Http.StringContent(System.Text.Json.JsonSerializer.Serialize(body,Json.Options),System.Text.Encoding.UTF8,"application/json");
            using var response=await client.PostAsync(path,content);var text=await response.Content.ReadAsStringAsync();
            if(!response.IsSuccessStatusCode)throw new Exception(path+": "+response.StatusCode+" "+text);
            return System.Text.Json.JsonDocument.Parse(text).RootElement.Clone();
        }
        using(var denied=await client.GetAsync("/api/local/inbox"))Assert(denied.StatusCode==System.Net.HttpStatusCode.Unauthorized,"Lokaler Posteingang braucht Kopplung");
        var paired=await Post("/api/local/pair",new{code=server.PairCode,name="Test-iPhone"});var token=paired.GetProperty("token").GetString()!;
        client.DefaultRequestHeaders.Authorization=new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer",token);
        var upload=await Post("/api/transfers",new{filename="test.txt",size=3});var id=upload.GetProperty("id").GetString();var uploadToken=upload.GetProperty("upload_token").GetString();
        using(var request=new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Put,$"/api/transfers/{id}/content"))
        {
            request.Headers.Add("Upload-Token",uploadToken);request.Headers.Add("Upload-Offset","0");request.Content=new System.Net.Http.ByteArrayContent("abc"u8.ToArray());
            using var response=await client.SendAsync(request);Assert(response.IsSuccessStatusCode,"Lokaler Blockupload");
        }
        using(var request=new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Post,$"/api/transfers/{id}/complete"))
        {
            request.Headers.Add("Upload-Token",uploadToken);request.Content=new System.Net.Http.StringContent("{}");using var response=await client.SendAsync(request);Assert(response.IsSuccessStatusCode,"Lokaler Uploadabschluss");
        }
        Assert(server.Pending.Count()==1,"Lokale Sendung braucht Annahme");
        var temp=Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString("N"));Directory.CreateDirectory(temp);
        try
        {
            var saved=await server.Accept(id!,temp);Assert(File.ReadAllText(saved)=="abc","iPhone zu PC korrekt gespeichert");
            await server.QueueOutgoing(saved,token,default);
            using var response=await client.GetAsync("/api/local/inbox");response.EnsureSuccessStatusCode();
            var inbox=System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("inbox");Assert(inbox.GetArrayLength()==1,"PC-Datei im iPhone-Posteingang");
            var item=inbox[0];var payload=await client.GetByteArrayAsync(item.GetProperty("download_url").GetString());Assert(payload.SequenceEqual("abc"u8.ToArray()),"PC zu iPhone korrekt übertragen");
            await Post(item.GetProperty("ack_url").GetString()!,new{});Assert(File.Exists(saved),"Originaldatei nach Quittung erhalten");
        }
        finally{Directory.Delete(temp,true);}
    }
    private static void Assert(bool value,string test){if(!value)throw new Exception(test+" fehlgeschlagen");}
    private static void Throws(Action action){try{action();}catch{return;}throw new Exception("Ungültige Eingabe wurde akzeptiert.");}
}
