using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Logging;
namespace RobinShare;

public sealed class LocalServer : IAsyncDisposable
{
    public const int Port=5057;
    private readonly Settings settings;
    private WebApplication? app;
    private readonly string root=Path.Combine(Settings.Folder,"Incoming");
    private readonly ConcurrentDictionary<string,LocalUpload> uploads=new();
    private readonly ConcurrentDictionary<string,Outgoing> outgoing=new();
    private readonly ConcurrentDictionary<string,Queue<DateTime>> pairingAttempts=new();
    private readonly object pairingLock=new();
    private readonly SemaphoreSlim createLock=new(1,1);
    private X509Certificate2? certificate;
    private System.Threading.Timer? cleanup;
    public string PairCode {get;private set;}=Code();
    private DateTime codeExpires=DateTime.UtcNow.AddMinutes(10);
    public string Fingerprint {get;private set;}="";
    public string Address {get;private set;}="";
    public string Status {get;private set;}="Lokale Verbindung wird gestartet …";
    public event Action? Changed;
    public LocalServer(Settings settings)=>this.settings=settings;
    public IEnumerable<LocalUpload> Pending=>uploads.Values.Where(u=>u.Complete).ToArray();
    public static string Code()=>RandomNumberGenerator.GetInt32(1000000).ToString("D6");
    public void RenewCode(){lock(pairingLock){PairCode=Code();codeExpires=DateTime.UtcNow.AddMinutes(10);}Changed?.Invoke();}
    public async Task Start()
    {
        Directory.CreateDirectory(root);
        foreach(var file in Directory.EnumerateFiles(root,"*.partial"))try{File.Delete(file);}catch{}
        certificate=Certificate();
        Fingerprint=Convert.ToHexString(SHA256.HashData(certificate.RawData)).ToLowerInvariant();
        var ip=NetworkInterface.GetAllNetworkInterfaces().Where(n=>n.OperationalStatus==OperationalStatus.Up && n.NetworkInterfaceType!=NetworkInterfaceType.Loopback)
            .SelectMany(n=>n.GetIPProperties().UnicastAddresses).Select(a=>a.Address).FirstOrDefault(a=>a.AddressFamily==System.Net.Sockets.AddressFamily.InterNetwork && !a.ToString().StartsWith("169.254."));
        Address=$"https://{ip??IPAddress.Loopback}:{Port}";
        var builder=WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k=>{k.Limits.MaxRequestBodySize=8*1024*1024;k.Limits.RequestHeadersTimeout=TimeSpan.FromSeconds(15);k.Limits.MaxConcurrentConnections=24;k.ListenAnyIP(Port,l=>l.UseHttps(certificate));});
        app=builder.Build();
        app.Use(async(ctx,next)=>{
            ctx.Response.Headers["Cache-Control"]="no-store";ctx.Response.Headers["X-Content-Type-Options"]="nosniff";
            if(ctx.Request.Headers.ContainsKey("Origin")){await Error(ctx,403,"Bitte die native App verwenden.");return;}
            try{await next(ctx);}catch(OperationCanceledException){}catch{if(!ctx.Response.HasStarted)await Error(ctx,500,"Lokale Anfrage fehlgeschlagen.");}
        });
        app.MapGet("/api/config",()=>Results.Json(new{ok=true,name=settings.Name,chunk_bytes=8*1024*1024,max_bytes=100L*1024*1024*1024},Json.Options));
        app.MapPost("/api/local/pair",async(HttpContext ctx)=>{
            var ip=ctx.Connection.RemoteIpAddress?.ToString()??"unknown";
            lock(pairingLock){var q=pairingAttempts.GetOrAdd(ip,_=>new());while(q.Count>0 && q.Peek()<DateTime.UtcNow.AddMinutes(-1))q.Dequeue();if(q.Count>=5){ctx.Response.StatusCode=429;return Results.Json(new{ok=false,error="Bitte eine Minute warten."});}q.Enqueue(DateTime.UtcNow);}
            var input=await ctx.Request.ReadFromJsonAsync<PairInput>(Json.Options);
            lock(pairingLock)
            {
                if(input==null || DateTime.UtcNow>codeExpires || !CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(input.Code??""),System.Text.Encoding.UTF8.GetBytes(PairCode)))return Results.Json(new{ok=false,error="Kopplungscode ungültig oder abgelaufen."},statusCode:403);
                var token=Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
                settings.LocalTokens[token]=(input.Name??"iPhone")[..Math.Min(60,(input.Name??"iPhone").Length)];settings.Save();
                PairCode=Code();codeExpires=DateTime.UtcNow.AddMinutes(10);Changed?.Invoke();
                return Results.Json(new{ok=true,token});
            }
        });
        app.Use(async(ctx,next)=>{
            if(ctx.Request.Path=="/api/config" || ctx.Request.Path=="/api/local/pair"){await next(ctx);return;}
            var token=Bearer(ctx);
            lock(pairingLock){if(!settings.LocalTokens.ContainsKey(token))token="";}
            if(token==""){await Error(ctx,401,"PC zuerst koppeln.");return;}
            ctx.Items["token"]=token;await next(ctx);
        });
        app.MapGet("/api/local/inbox",(HttpContext ctx)=>Results.Json(new{ok=true,inbox=outgoing.Values.Where(o=>o.Target==Bearer(ctx)).Select(o=>o.Card())},Json.Options));
        app.MapPost("/api/transfers",async(HttpContext ctx)=>{
            var input=await ctx.Request.ReadFromJsonAsync<TransferInput>(Json.Options);
            if(input==null || input.Size<0 || input.Size>100L*1024*1024*1024)return Results.Json(new{ok=false,error="Dateigröße ungültig."},statusCode:413);
            await createLock.WaitAsync(ctx.RequestAborted);
            try{
                var free=new DriveInfo(Path.GetPathRoot(root)!).AvailableFreeSpace;
                if(uploads.Values.Sum(u=>u.Size-u.Offset)+input.Size+64*1024*1024>free || uploads.Count>=20)return Results.Json(new{ok=false,error="Zu wenig freier Speicher oder zu viele Sendungen."},statusCode:507);
                var id=Guid.NewGuid().ToString("N");var upload=new LocalUpload(id,Safety.Filename(input.Filename),input.Size,Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),Bearer(ctx),Path.Combine(root,id+".partial"));
                using(File.Create(upload.Path)){}uploads[id]=upload;
                return Results.Json(new{ok=true,id,upload_token=upload.UploadToken,offset=0,chunk_bytes=8*1024*1024},statusCode:201);
            }finally{createLock.Release();}
        });
        app.MapGet("/api/transfers/{id}",(HttpContext ctx,string id)=>{
            if(!AuthorizedUpload(ctx,id,out var u))return Denied();
            return Results.Json(new{ok=true,offset=u!.Offset,size=u.Size,ready=u.Complete});
        });
        app.MapDelete("/api/transfers/{id}",async(HttpContext ctx,string id)=>{
            if(!AuthorizedUpload(ctx,id,out var u))return Denied();
            await u!.Gate.WaitAsync();try{uploads.TryRemove(id,out _);File.Delete(u.Path);}finally{u.Gate.Release();}Changed?.Invoke();return Results.Json(new{ok=true});
        });
        app.MapPut("/api/transfers/{id}/content",async(HttpContext ctx,string id)=>{
            if(!AuthorizedUpload(ctx,id,out var u))return Denied();
            await u!.Gate.WaitAsync(ctx.RequestAborted);
            try{
                if(u.Complete || !long.TryParse(ctx.Request.Headers["Upload-Offset"],out var offset) || offset!=u.Offset)return Results.Json(new{ok=false,error="Offset stimmt nicht."},statusCode:409);
                var length=ctx.Request.ContentLength;
                if(length==null || length<1 || length>8*1024*1024 || offset+length>u.Size)return Results.Json(new{ok=false,error="Ungültiger Block."},statusCode:400);
                await using var file=new FileStream(u.Path,FileMode.Open,FileAccess.Write,FileShare.None,1024*1024,true);file.SetLength(offset);file.Position=offset;
                await ctx.Request.Body.CopyToAsync(file,ctx.RequestAborted);await file.FlushAsync(ctx.RequestAborted);
                if(file.Position!=offset+length)return Results.Json(new{ok=false,error="Block unvollständig."},statusCode:400);
                u.Offset=file.Position;u.Seen=DateTime.UtcNow;return Results.Json(new{ok=true,offset=u.Offset});
            }finally{u.Gate.Release();}
        });
        app.MapPost("/api/transfers/{id}/complete",async(HttpContext ctx,string id)=>{
            if(!AuthorizedUpload(ctx,id,out var u))return Denied();
            await u!.Gate.WaitAsync(ctx.RequestAborted);
            try{
                if(u.Offset!=u.Size)return Results.Json(new{ok=false,error="Es fehlen Dateiblöcke."},statusCode:409);
                if(!u.Complete){u.Hash=await Safety.Hash(u.Path,ctx.RequestAborted);u.Complete=true;Changed?.Invoke();}
                return Results.Json(new{ok=true,pin="Lokal",link=Address,transfer=new Transfer(u.Id,u.Name,u.Size,"application/octet-stream",u.Hash,DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds(),"/api/transfer/"+u.Id+"/download","/api/transfer/"+u.Id+"/ack")},Json.Options);
            }finally{u.Gate.Release();}
        });
        app.MapGet("/api/transfer/{id}/download",(HttpContext ctx,string id)=>{
            if(!outgoing.TryGetValue(id,out var o) || o.Target!=Bearer(ctx) || ctx.Request.Query["access"]!=o.Secret)return Denied();
            if(!File.Exists(o.Path))return Results.NotFound();
            return Results.File(o.Path,"application/octet-stream",o.Name,enableRangeProcessing:true);
        });
        app.MapPost("/api/transfer/{id}/ack",(HttpContext ctx,string id)=>{
            if(!outgoing.TryGetValue(id,out var o) || o.Target!=Bearer(ctx) || ctx.Request.Query["access"]!=o.Secret)return Denied();
            outgoing.TryRemove(id,out _);Changed?.Invoke();return Results.Json(new{ok=true});
        });
        await app.StartAsync();
        cleanup=new System.Threading.Timer(timerState=>{
            foreach(var u in uploads.Values.Where(u=>u.Seen<DateTime.UtcNow.AddHours(-1))){if(u.Gate.Wait(0)){try{uploads.TryRemove(u.Id,out _);File.Delete(u.Path);}catch{}finally{u.Gate.Release();}}}
            foreach(var o in outgoing.Values.Where(o=>o.Expires<DateTimeOffset.UtcNow.ToUnixTimeSeconds()))outgoing.TryRemove(o.Id,out _);
        },null,TimeSpan.FromMinutes(1),TimeSpan.FromMinutes(1));
        Status="Direktverbindung bereit · HTTPS · Port 5057";Changed?.Invoke();
    }
    public async Task QueueOutgoing(string path,string token,CancellationToken ct)
    {
        var hash=await Safety.Hash(path,ct);var f=new FileInfo(path);var id=Guid.NewGuid().ToString("N");
        outgoing[id]=new(id,path,f.Name,f.Length,hash,token,Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds());Changed?.Invoke();
    }
    public async Task<string> Accept(string id,string directory)
    {
        if(!uploads.TryGetValue(id,out var u))throw new Exception("Sendung nicht mehr vorhanden.");
        await u.Gate.WaitAsync();
        try{
            if(!u.Complete)throw new Exception("Datei ist noch nicht vollständig.");
            if(await Safety.Hash(u.Path)!=u.Hash)throw new Exception("Datei beschädigt.");
            Directory.CreateDirectory(directory);var path=Path.Combine(directory,u.Name);if(File.Exists(path))path=Path.Combine(directory,Guid.NewGuid().ToString("N")[..8]+"-"+u.Name);
            File.Move(u.Path,path);uploads.TryRemove(id,out _);Changed?.Invoke();return path;
        }finally{u.Gate.Release();}
    }
    public async Task Reject(string id){if(uploads.TryGetValue(id,out var u)){await u.Gate.WaitAsync();try{uploads.TryRemove(id,out _);File.Delete(u.Path);}finally{u.Gate.Release();}Changed?.Invoke();}}
    private bool AuthorizedUpload(HttpContext ctx,string id,out LocalUpload? u)=>uploads.TryGetValue(id,out u) && u.Owner==Bearer(ctx) && ctx.Request.Headers["Upload-Token"]==u.UploadToken;
    private static string Bearer(HttpContext ctx)=>ctx.Request.Headers.Authorization.ToString().Replace("Bearer ","",StringComparison.Ordinal);
    private static IResult Denied()=>Results.Json(new{ok=false,error="Zugriff verweigert."},statusCode:403);
    private static Task Error(HttpContext c,int status,string text){c.Response.StatusCode=status;return c.Response.WriteAsJsonAsync(new{ok=false,error=text});}
    private static X509Certificate2 Certificate()
    {
        Directory.CreateDirectory(Settings.Folder);var path=Path.Combine(Settings.Folder,"local-cert.bin");
        if(File.Exists(path))return X509CertificateLoader.LoadPkcs12(ProtectedData.Unprotect(File.ReadAllBytes(path),null,DataProtectionScope.CurrentUser),null,X509KeyStorageFlags.Exportable);
        using var key=RSA.Create(2048);var request=new CertificateRequest("CN=Robins Share",key,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature|X509KeyUsageFlags.KeyEncipherment,true));
        var eku=new System.Security.Cryptography.OidCollection{new("1.3.6.1.5.5.7.3.1")};request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(eku,true));
        using var cert=request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1),DateTimeOffset.UtcNow.AddYears(5));
        var pfx=cert.Export(X509ContentType.Pfx);File.WriteAllBytes(path,ProtectedData.Protect(pfx,null,DataProtectionScope.CurrentUser));
        return X509CertificateLoader.LoadPkcs12(pfx,null,X509KeyStorageFlags.Exportable);
    }
    public async ValueTask DisposeAsync(){cleanup?.Dispose();if(app!=null)await app.DisposeAsync();certificate?.Dispose();}
    private record PairInput(string Code,string Name);
    private record TransferInput(string Filename,long Size);
    private record Outgoing(string Id,string Path,string Name,long Size,string Hash,string Target,string Secret,long Expires)
    {
        public Transfer Card()=>new(Id,Name,Size,"application/octet-stream",Hash,Expires,$"/api/transfer/{Id}/download?access={Secret}",$"/api/transfer/{Id}/ack?access={Secret}");
    }
}
public sealed class LocalUpload(string id,string name,long size,string uploadToken,string owner,string path)
{
    public string Id{get;}=id;public string Name{get;}=name;public long Size{get;}=size;public string UploadToken{get;}=uploadToken;public string Owner{get;}=owner;public string Path{get;}=path;
    public long Offset{get;set;} public string Hash{get;set;}="";public bool Complete{get;set;}public DateTime Seen{get;set;}=DateTime.UtcNow;public SemaphoreSlim Gate{get;}=new(1,1);
}
