using System.IO;
using System.Runtime.InteropServices;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using WinRT;

namespace RobinShare;

/// <summary>
/// The real Windows system share sheet, using the supported HWND interop for
/// unpackaged WPF desktop applications. Windows Nearby Sharing appears here
/// if enabled by the user; this does not emulate its private protocol.
/// </summary>
public sealed class WindowsShareBridge
{
    [ComImport]
    [Guid("3A3DCD6C-3EAB-43DC-BCDE-45671CE800C8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDataTransferManagerInterop
    {
        IntPtr GetForWindow([In] IntPtr window, [In] ref Guid iid);
        void ShowShareUIForWindow(IntPtr window);
    }

    private static readonly Guid DataManagerID = new("a5caee9b-8708-49d1-8d36-67d25a8da00c");
    private readonly IDataTransferManagerInterop interop;
    private readonly DataTransferManager manager;
    private readonly IntPtr window;
    private IReadOnlyList<string> files = [];

    public WindowsShareBridge(IntPtr window)
    {
        this.window = window;
        interop = DataTransferManager.As<IDataTransferManagerInterop>();
        var iid = DataManagerID;
        var pointer = interop.GetForWindow(window, ref iid);
        manager = WinRT.MarshalInterface<DataTransferManager>.FromAbi(pointer);
        manager.DataRequested += OnDataRequested;
    }

    public void Show(IEnumerable<string> selected)
    {
        files = selected.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if(files.Count == 0) throw new InvalidOperationException("Bitte zuerst Dateien auswählen.");
        interop.ShowShareUIForWindow(window);
    }

    private async void OnDataRequested(DataTransferManager sender, DataRequestedEventArgs args)
    {
        var request = args.Request;
        var deferral = request.GetDeferral();
        try
        {
            var snapshot = files.ToArray();
            if(snapshot.Length == 0) throw new IOException("Keine Dateien ausgewählt.");
            var storage = new List<IStorageItem>(snapshot.Length);
            foreach(var path in snapshot)
                storage.Add(await StorageFile.GetFileFromPathAsync(path));

            request.Data.Properties.Title = snapshot.Length == 1
                ? Path.GetFileName(snapshot[0]) : $"{snapshot.Length} Dateien aus SHAREAPP";
            request.Data.Properties.Description = "Mit Windows teilen – inklusive Umgebungsfreigabe, sofern aktiviert.";
            request.Data.SetStorageItems(storage);
        }
        catch(Exception error)
        {
            request.FailWithDisplayText("Die Dateien konnten nicht zur Windows-Freigabe hinzugefügt werden: " + error.Message);
        }
        finally { deferral.Complete(); }
    }
}
