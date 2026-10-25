using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RobinShare;
public record Identity(string Id,string Token);
public record Device(string Id,string Name,string Platform);
public record Transfer(string Id,string Filename,long Size,string Mime,string Sha256,long ExpiresAt,string DownloadUrl,string AckUrl);
public record DeviceState(string PairCode,Device[] Devices,Transfer[] Inbox);
public record Upload(string Id,string UploadToken,long Offset,int? ChunkBytes);
public record ShareResult(string Pin,string Link,Transfer Transfer);
public record Lookup(Transfer Transfer);
public record Offset(long Value);
public record ServerConfig(string Name,int ChunkBytes,long MaxBytes);
public record Ok(bool Success);
public static class Json
{
    public static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy=JsonNamingPolicy.SnakeCaseLower,PropertyNameCaseInsensitive=true };
}
public static class Safety
{
    public static Uri Server(string value)
    {
        if (!Uri.TryCreate(value.Trim(),UriKind.Absolute,out var uri) || uri.Scheme!="https" || string.IsNullOrEmpty(uri.Host) || uri.UserInfo!="" || uri.AbsolutePath!="/" || uri.Query!="" || uri.Fragment!="") throw new Exception("Bitte eine HTTPS-Serveradresse ohne Pfad eingeben.");
        return uri;
    }
    public static Uri Endpoint(Uri server,string path)
    {
        if (!path.StartsWith("/api/",StringComparison.Ordinal)) throw new Exception("Ungültige Downloadadresse.");
        var url = new Uri(server,path);
        if (url.Authority!=server.Authority || url.Scheme!=server.Scheme) throw new Exception("Fremde Downloadadresse.");
        return url;
    }
    public static string Filename(string value)
    {
        value=Path.GetFileName(value.Replace('\\','/'));
        value=new string(value.Where(c=>!Path.GetInvalidFileNameChars().Contains(c)).ToArray()).Trim(' ','.');
        if (string.IsNullOrEmpty(value)) value="Datei";
        var stem=Path.GetFileNameWithoutExtension(value).ToUpperInvariant();
        if (new[]{"CON","PRN","AUX","NUL","COM1","COM2","COM3","COM4","LPT1","LPT2","LPT3"}.Contains(stem)) value="_"+value;
        return value[..Math.Min(180,value.Length)];
    }
    public static async Task<string> Hash(string path,CancellationToken ct=default)
    {
        await using var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read,1024*1024,true);
        return Convert.ToHexString(await SHA256.HashDataAsync(file,ct)).ToLowerInvariant();
    }
}
public sealed class API : IDisposable
{
    public Uri Base { get; }
    public string Token { get; }
    private readonly HttpClient http;
    public API(string server,string token="")
    {
        Base=Safety.Server(server); Token=token;
        http=new HttpClient(new HttpClientHandler{AllowAutoRedirect=false}){Timeout=TimeSpan.FromMinutes(30)};
    }
    public async Task<T> Call<T>(string path,HttpMethod? method=null,object? body=null,Dictionary<string,string>? headers=null,CancellationToken ct=default)
    {
        using var request=new HttpRequestMessage(method??HttpMethod.Get,Safety.Endpoint(Base,path));
        if (Token!="") request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",Token);
        if (body!=null) request.Content=new StringContent(JsonSerializer.Serialize(body,Json.Options),Encoding.UTF8,"application/json");
        if (headers!=null) foreach(var kv in headers) request.Headers.TryAddWithoutValidation(kv.Key,kv.Value);
        using var response=await http.SendAsync(request,ct);
        var data=await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            var error="Der Server hat die Anfrage abgelehnt.";
            try { error=JsonDocument.Parse(data).RootElement.GetProperty("error").GetString()??error; } catch { }
            throw new HttpRequestException(error,null,response.StatusCode);
        }
        return JsonSerializer.Deserialize<T>(data,Json.Options)??throw new Exception("Ungültige Serverantwort.");
    }
    public async Task<ShareResult> Send(string path,string? target,IProgress<double> progress,CancellationToken ct)
    {
        var file=new FileInfo(path); var config=await Call<ServerConfig>("/api/config",ct:ct);
        if(file.Length>config.MaxBytes) throw new Exception("Die Datei überschreitet das Serverlimit.");
        var upload=await Call<Upload>("/api/transfers",HttpMethod.Post,new{filename=file.Name,size=file.Length,mime="application/octet-stream",target_id=target},ct:ct);
        var headers=new Dictionary<string,string>{{"Upload-Token",upload.UploadToken}};
        long offset=0;
        try
        {
            await using var source=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read,1024*1024,true);
            var buffer=new byte[Math.Clamp(config.ChunkBytes,65536,8*1024*1024)];
            while(offset<file.Length)
            {
                for(var attempt=0;;attempt++)
                {
                    try
                    {
                        ct.ThrowIfCancellationRequested(); source.Position=offset;
                        var read=await source.ReadAsync(buffer.AsMemory(0,(int)Math.Min(buffer.Length,file.Length-offset)),ct);
                        if(read==0) throw new Exception("Die Quelldatei wurde verändert.");
                        using var r=new HttpRequestMessage(HttpMethod.Put,Safety.Endpoint(Base,$"/api/transfers/{upload.Id}/content"));
                        r.Headers.TryAddWithoutValidation("Upload-Token",upload.UploadToken);
                        r.Headers.TryAddWithoutValidation("Upload-Offset",offset.ToString());
                        if(Token!="")r.Headers.Authorization=new AuthenticationHeaderValue("Bearer",Token);
                        r.Content=new ByteArrayContent(buffer,0,read); r.Content.Headers.ContentType=new MediaTypeHeaderValue("application/octet-stream");
                        using var response=await http.SendAsync(r,ct);
                        response.EnsureSuccessStatusCode();
                        var data=JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                        offset=data.RootElement.GetProperty("offset").GetInt64();
                        progress.Report(file.Length==0?1:(double)offset/file.Length); break;
                    }
                    catch when(attempt<3 && !ct.IsCancellationRequested)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(attempt+1),ct);
                        var state=await Call<JsonElement>($"/api/transfers/{upload.Id}",headers:headers,ct:ct);
                        offset=state.GetProperty("offset").GetInt64();
                    }
                }
            }
            var result=await Call<ShareResult>($"/api/transfers/{upload.Id}/complete",HttpMethod.Post,new{},headers,ct);
            progress.Report(1); return result;
        }
        catch
        {
            try{await Call<JsonElement>($"/api/transfers/{upload.Id}",HttpMethod.Delete,headers:headers,ct:CancellationToken.None);}catch{}
            throw;
        }
    }
    public async Task<string> Download(Transfer t,string directory,IProgress<double> progress,CancellationToken ct)
    {
        Directory.CreateDirectory(directory);
        var name=Safety.Filename(t.Filename);
        var destination=Path.Combine(directory,name);
        if(File.Exists(destination))destination=Path.Combine(directory,Guid.NewGuid().ToString("N")[..8]+"-"+name);
        var temporary=Path.Combine(directory,"."+t.Id+".partial");
        var offset=File.Exists(temporary)?new FileInfo(temporary).Length:0;
        if(offset>t.Size){File.Delete(temporary);offset=0;}
        using var request=new HttpRequestMessage(HttpMethod.Get,Safety.Endpoint(Base,t.DownloadUrl));
        if(offset>0)request.Headers.Range=new RangeHeaderValue(offset,null);
        if(Token!="")request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",Token);
        using var response=await http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,ct);
        response.EnsureSuccessStatusCode();
        if(offset>0 && response.StatusCode!=System.Net.HttpStatusCode.PartialContent){offset=0;File.Delete(temporary);}
        await using(var output=new FileStream(temporary,FileMode.Append,FileAccess.Write,FileShare.None,1024*1024,true))
        await using(var source=await response.Content.ReadAsStreamAsync(ct))
        {
            var buffer=new byte[1024*1024];int n;
            while((n=await source.ReadAsync(buffer,ct))>0){await output.WriteAsync(buffer.AsMemory(0,n),ct);offset+=n;progress.Report(t.Size==0?1:(double)offset/t.Size);}
        }
        if(new FileInfo(temporary).Length!=t.Size || !string.Equals(await Safety.Hash(temporary,ct),t.Sha256,StringComparison.OrdinalIgnoreCase)) {File.Delete(temporary);throw new Exception("Prüfsumme stimmt nicht. Die Serverkopie bleibt erhalten.");}
        File.Move(temporary,destination);
        try{await Call<JsonElement>(t.AckUrl,HttpMethod.Post,new{},ct:ct);}catch{}
        progress.Report(1); return destination;
    }
    public void Dispose()=>http.Dispose();
}
