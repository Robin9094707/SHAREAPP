using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32;
namespace RobinShare;
public sealed class Settings
{
    public static string Folder { get; }=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"RobinsShare");
    public string Server { get;set; }="https://share.rjuhas.eu";
    public string Name { get;set; }=Environment.MachineName;
    public Dictionary<string,Identity> Identities { get;set; }=[];
    public Dictionary<string,string> LocalTokens { get;set; }=[];
    public bool AutoStart {get;set;}
    public bool ContextMenu {get;set;}
    public static Settings Load()
    {
        try {return JsonSerializer.Deserialize<Settings>(ProtectedData.Unprotect(File.ReadAllBytes(Path.Combine(Folder,"settings.bin")),null,DataProtectionScope.CurrentUser))??new();}catch{return new();}
    }
    public void Save()
    {
        Directory.CreateDirectory(Folder);
        var bytes=ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(this),null,DataProtectionScope.CurrentUser);
        File.WriteAllBytes(Path.Combine(Folder,"settings.bin.tmp"),bytes);
        File.Move(Path.Combine(Folder,"settings.bin.tmp"),Path.Combine(Folder,"settings.bin"),true);
    }
    public void ApplyIntegration()
    {
        var exe=Environment.ProcessPath??throw new Exception("Programmpfad fehlt.");
        using var run=Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        if(AutoStart)run.SetValue("RobinsShare",$"\"{exe}\" --background");else run.DeleteValue("RobinsShare",false);
        const string key=@"Software\Classes\*\shell\RobinsShare";
        if(ContextMenu)
        {
            using var menu=Registry.CurrentUser.CreateSubKey(key);
            menu.SetValue("","Mit Robins Share an iPhone senden");menu.SetValue("Icon",exe);
            using var command=menu.CreateSubKey("command");command.SetValue("",$"\"{exe}\" --send \"%1\"");
        }
        else Registry.CurrentUser.DeleteSubKeyTree(key,false);
    }
}
