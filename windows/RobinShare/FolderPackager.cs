using System.IO;
using System.IO.Compression;

namespace RobinShare;

/// <summary>Package a folder into a standard ZIP without following reparse-point links.</summary>
public static class FolderPackager
{
    public const long MaxSourceBytes = 100L * 1024 * 1024 * 1024;
    public const int MaxFileCount = 100_000;

    public static string Pack(string directory, string? destinationFolder = null)
    {
        if(!Directory.Exists(directory)) throw new DirectoryNotFoundException("Der Ordner existiert nicht.");
        var root = Path.GetFullPath(directory);
        if((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Verknüpfte Ordner werden aus Sicherheitsgründen nicht archiviert.");
        var destination = destinationFolder ?? Path.Combine(Settings.Folder, "PreparedFolders");
        Directory.CreateDirectory(destination);
        var stem = Safety.Filename(new DirectoryInfo(root).Name);
        var privateFolder = Path.Combine(destination, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(privateFolder);
        var zipPath = Path.Combine(privateFolder, stem + ".zip");
        try
        {
            using var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create);
            var options = new EnumerationOptions {
                RecurseSubdirectories = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
                IgnoreInaccessible = false,
                ReturnSpecialDirectories = false
            };
            long total = 0;
            var count = 0;
            foreach(var path in Directory.EnumerateFiles(root, "*", options))
            {
                var file = new FileInfo(path);
                if((file.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                total = checked(total + file.Length);
                if(total > MaxSourceBytes) throw new IOException("Der Ordner überschreitet das 100-GiB-Limit.");
                if(++count > MaxFileCount) throw new IOException("Zu viele Dateien in diesem Ordner.");

                var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
                if(relative.StartsWith("../", StringComparison.Ordinal) || relative.StartsWith("/", StringComparison.Ordinal))
                    throw new IOException("Ungültiger relativer Dateipfad.");
                archive.CreateEntryFromFile(path, relative, CompressionLevel.Fastest);
            }
            if(count == 0)
                archive.CreateEntry("Leerer Ordner.txt");
            return zipPath;
        }
        catch { try { Directory.Delete(privateFolder, true); } catch { } throw; }
    }
}
