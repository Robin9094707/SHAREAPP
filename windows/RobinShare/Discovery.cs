using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;
namespace RobinShare;

public sealed class Discovery : IDisposable
{
    public const string ServiceId="9ee79c20-ed71-4f54-acf6-050550550001";
    public const string EndpointId="9ee79c20-ed71-4f54-acf6-050550550002";
    private UdpClient? mdns;
    private CancellationTokenSource stop=new();
    private GattServiceProvider? provider;
    private string endpoint="";
    private byte[] packet=[];
    public string Status{get;private set;}="";
    public async Task Start(string name,string url,string fingerprint)
    {
        var ip=IPAddress.Parse(new Uri(url).Host);
        if(!IPAddress.IsLoopback(ip))
        {
            try
            {
                mdns=new UdpClient(AddressFamily.InterNetwork);mdns.Client.SetSocketOption(SocketOptionLevel.Socket,SocketOptionName.ReuseAddress,true);mdns.Client.Bind(new IPEndPoint(IPAddress.Any,5353));mdns.JoinMulticastGroup(IPAddress.Parse("224.0.0.251"),ip);mdns.MulticastLoopback=false;mdns.Ttl=255;
                packet=Build(name,ip,fingerprint);
                _=Listen();_=Announce();Status="WLAN-Erkennung bereit.";
            }catch{mdns?.Dispose();mdns=null;Status="WLAN-Erkennung blockiert. Manuelle Kopplung ist möglich.";}
        }
        endpoint=JsonSerializer.Serialize(new{name,url,fingerprint,token=""});
        try
        {
            var adapter=await BluetoothAdapter.GetDefaultAsync();
            if(adapter==null || !adapter.IsPeripheralRoleSupported){Status+=" Bluetooth-Werbung wird von diesem PC nicht unterstützt.";return;}
            var result=await GattServiceProvider.CreateAsync(Guid.Parse(ServiceId));
            if(result.Error!=BluetoothError.Success){Status+=" Bluetooth ist nicht verfügbar.";return;}
            provider=result.ServiceProvider;
            using var writer=new DataWriter();writer.WriteString(endpoint);
            var parameters=new GattLocalCharacteristicParameters {CharacteristicProperties=GattCharacteristicProperties.Read,ReadProtectionLevel=GattProtectionLevel.Plain,StaticValue=writer.DetachBuffer()};
            var characteristic=await provider.Service.CreateCharacteristicAsync(Guid.Parse(EndpointId),parameters);
            if(characteristic.Error==BluetoothError.Success){provider.StartAdvertising(new GattServiceProviderAdvertisingParameters{IsConnectable=true,IsDiscoverable=true});Status+=" Bluetooth-Erkennung aktiv.";}
            else Status+=" Bluetooth-Erkennung konnte nicht gestartet werden.";
        }catch{Status+=" Bluetooth-Erkennung nicht verfügbar; WLAN bleibt nutzbar.";}
    }
    private async Task Listen()
    {
        while(!stop.IsCancellationRequested && mdns!=null)
        {
            try{var incoming=await mdns.ReceiveAsync(stop.Token);if(IsQuery(incoming.Buffer))await mdns.SendAsync(packet,new IPEndPoint(IPAddress.Parse("224.0.0.251"),5353),stop.Token);}catch(OperationCanceledException){break;}catch{break;}
        }
    }
    private async Task Announce()
    {
        while(!stop.IsCancellationRequested && mdns!=null)
        {
            try{await mdns.SendAsync(packet,new IPEndPoint(IPAddress.Parse("224.0.0.251"),5353),stop.Token);await Task.Delay(TimeSpan.FromSeconds(60),stop.Token);}catch{break;}
        }
    }
    public static bool IsQuery(byte[] data)
    {
        if(data.Length<12 || (data[2]&0x80)!=0 || data.Length>9000)return false;
        var count=(data[4]<<8)|data[5];if(count>64)return false;
        var offset=12;
        try{for(var i=0;i<count;i++){var name=ReadName(data,ref offset);if(offset+4>data.Length)return false;offset+=4;if(name.EndsWith("_robinshare._tcp.local",StringComparison.OrdinalIgnoreCase))return true;}}catch{}
        return false;
    }
    private static string ReadName(byte[] data,ref int offset)
    {
        var labels=new List<string>();var at=offset;var jumped=false;var budget=128;
        while(budget-->0)
        {
            if(at>=data.Length)throw new InvalidDataException();var n=data[at++];
            if(n==0){if(!jumped)offset=at;return string.Join('.',labels);}
            if((n&0xc0)==0xc0){if(at>=data.Length)throw new InvalidDataException();if(!jumped)offset=at+1;jumped=true;at=((n&63)<<8)|data[at];continue;}
            if(n>63 || at+n>data.Length)throw new InvalidDataException();labels.Add(Encoding.UTF8.GetString(data,at,n));at+=n;
        }
        throw new InvalidDataException();
    }
    public static byte[] Build(string name,IPAddress ip,string fingerprint)
    {
        var label=new string(name.Where(c=>char.IsAsciiLetterOrDigit(c)||c=='-').Take(40).ToArray());if(label=="")label="Robins-PC";
        var service="_robinshare._tcp.local";var instance=label+"."+service;var host=label+".local";
        using var stream=new MemoryStream();using var writer=new BinaryWriter(stream);
        void U16(int value){writer.Write((byte)(value>>8));writer.Write((byte)value);}
        void U32(uint value){writer.Write((byte)(value>>24));writer.Write((byte)(value>>16));writer.Write((byte)(value>>8));writer.Write((byte)value);}
        byte[] Name(string value){using var s=new MemoryStream();foreach(var part in value.Split('.')){var b=Encoding.UTF8.GetBytes(part);s.WriteByte((byte)b.Length);s.Write(b);}s.WriteByte(0);return s.ToArray();}
        void Record(string owner,int type,byte[] content,bool flush=true){writer.Write(Name(owner));U16(type);U16(flush?0x8001:1);U32(120);U16(content.Length);writer.Write(content);}
        U16(0);U16(0x8400);U16(0);U16(4);U16(0);U16(0);
        Record(service,12,Name(instance),false);
        Record(instance,33,new byte[]{0,0,0,0,(byte)(LocalServer.Port>>8),(byte)(LocalServer.Port&255)}.Concat(Name(host)).ToArray());
        var txt=Encoding.UTF8.GetBytes("fp="+fingerprint);Record(instance,16,new[]{(byte)txt.Length}.Concat(txt).ToArray());Record(host,1,ip.GetAddressBytes());
        return stream.ToArray();
    }
    public void Dispose(){stop.Cancel();mdns?.Dispose();provider?.StopAdvertising();}
}
