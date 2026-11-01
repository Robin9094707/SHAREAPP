using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace RobinShare;

public sealed record UploadCheckpoint(string BaseUrl,string FilePath,string? Target,long Size,
    string UploadId,string UploadToken,DateTime CreatedUtc);

/// DPAPI protects resume tokens at rest. Entries only describe staged uploads;
/// the receiver's server remains authoritative about the committed offset.
public static class UploadJournal
{
    private static readonly object Gate=new();
    private static string Location=>Path.Combine(Settings.Folder,"pending-uploads.dat");
    private static List<UploadCheckpoint> Read()
    {
        try
        {
            if(!File.Exists(Location))return [];
            var raw=ProtectedData.Unprotect(File.ReadAllBytes(Location),null,DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<List<UploadCheckpoint>>(raw)??[];
        }
        catch {return [];}
    }
    private static void Save(List<UploadCheckpoint> entries)
    {
        Directory.CreateDirectory(Settings.Folder);
        var raw=JsonSerializer.SerializeToUtf8Bytes(entries);
        var protectedBytes=ProtectedData.Protect(raw,null,DataProtectionScope.CurrentUser);
        var temp=Location+".tmp";
        File.WriteAllBytes(temp,protectedBytes);
        File.Move(temp,Location,true);
    }
    public static UploadCheckpoint? Find(string baseUrl,string path,string? target,long size)
    {
        lock(Gate) return Read().FirstOrDefault(e=>e.BaseUrl==baseUrl &&
            e.FilePath==path && e.Target==target && e.Size==size);
    }
    public static void Record(UploadCheckpoint value)
    {
        lock(Gate)
        {
            var entries=Read();
            entries.RemoveAll(e=>e.BaseUrl==value.BaseUrl && e.FilePath==value.FilePath && e.Target==value.Target);
            entries.Add(value);
            Save(entries);
        }
    }
    public static void Finish(UploadCheckpoint value)
    {
        lock(Gate)
        {
            var entries=Read();
            entries.RemoveAll(e=>e.BaseUrl==value.BaseUrl && e.UploadId==value.UploadId);
            Save(entries);
        }
    }
}
