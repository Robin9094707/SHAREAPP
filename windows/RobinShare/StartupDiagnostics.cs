using System.IO;
using System.Text;
using System.Text.Json;
namespace RobinShare;
internal static class StartupDiagnostics
{
    internal static string LogFile=>Path.Combine(Settings.Folder,"Logs","startup.log");
    internal static string? ResultFile {get;set;}
    internal static void Log(string stage,Exception? error=null)
    {
        try{Directory.CreateDirectory(Path.GetDirectoryName(LogFile)!);File.AppendAllText(LogFile,$"{DateTimeOffset.Now:O} {stage}\n{error}\n",Encoding.UTF8);}catch{}
    }
    internal static void Result(bool ok,string stage,Exception? error=null)
    {
        if(ResultFile==null)return;
        try{Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(ResultFile))!);File.WriteAllText(ResultFile,JsonSerializer.Serialize(new{ok,stage,error=error?.ToString(),log=LogFile}));}catch{}
    }
}
