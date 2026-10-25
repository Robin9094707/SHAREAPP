using System.IO;
using System.Net;
namespace RobinShare;
internal static class CoreTests
{
    public static int Run()
    {
        try
        {
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
            return 0;
        }
        catch(Exception e){File.WriteAllText(Path.Combine(Path.GetTempPath(),"RobinsShare-test-error.txt"),e.ToString());return 1;}
    }
    private static void Assert(bool value,string test){if(!value)throw new Exception(test+" fehlgeschlagen");}
    private static void Throws(Action action){try{action();}catch{return;}throw new Exception("Ungültige Eingabe wurde akzeptiert.");}
}
