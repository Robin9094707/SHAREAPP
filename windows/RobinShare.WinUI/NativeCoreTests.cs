using System.IO;
using System.IO.Compression;

namespace RobinShare;

/// Standalone core checks for the WinUI target. These do not instantiate
/// WPF controls and can be invoked from GitHub Actions with --self-test.
internal static class NativeCoreTests
{
    public static int Run()
    {
        try
        {
            Check(Safety.Filename("../../video.mov")=="video.mov", "Safe filename");
            Check(Safety.Filename("CON.txt")=="_CON.txt", "Reserved Windows filename");
            Check(Safety.Endpoint(Safety.Server("https://example.org"),"/api/data").Host=="example.org",
                "HTTPS same-origin API");
            MustThrow(()=>Safety.Server("http://example.org"), "HTTP rejected");
            MustThrow(()=>Safety.Endpoint(Safety.Server("https://example.org"),"//bad.example/api/data"),
                "Cross-origin rejected");

            var temp=Path.Combine(Path.GetTempPath(),"SHAREAPP-winui-test-"+Guid.NewGuid().ToString("N"));
            var source=Path.Combine(temp,"Documents");
            var nested=Path.Combine(source,"Subfolder");
            Directory.CreateDirectory(nested);
            try
            {
                File.WriteAllText(Path.Combine(source,"hello.txt"),"WinUI test");
                File.WriteAllBytes(Path.Combine(nested,"picture.bin"),[1,2,3]);
                var archive=FolderPackager.Pack(source,Path.Combine(temp,"out"));
                Check(Path.GetFileName(archive)=="Documents.zip", "Natural folder name");
                using var zip=ZipFile.OpenRead(archive);
                Check(zip.Entries.Any(e=>e.FullName=="hello.txt"), "Root file archived");
                Check(zip.Entries.Any(e=>e.FullName=="Subfolder/picture.bin"), "Nested files preserved");
                MustThrow(()=>FolderPackager.Pack(Path.Combine(temp,"missing")), "Invalid directory");
            }
            finally {Directory.Delete(temp,true);}
            return 0;
        }
        catch(Exception exception)
        {
            try { File.WriteAllText(Path.Combine(Path.GetTempPath(),"RobinsShare-winui-test-error.txt"), exception.ToString()); }
            catch {}
            return 1;
        }
    }

    private static void Check(bool valid,string name)
    {
        if(!valid)throw new InvalidDataException(name+" failed");
    }

    private static void MustThrow(Action action,string name)
    {
        try {action();} catch {return;}
        throw new InvalidDataException(name+" did not reject input");
    }
}
