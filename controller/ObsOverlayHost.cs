using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace HeavierByTheKill.Controller;

internal readonly record struct ObsOverlaySettings(bool Enabled,int Port)
{
    internal static ObsOverlaySettings Load()
    {
        var enabled=true; var port=27361;
        var path=Path.Combine(AppContext.BaseDirectory,"heavier_by_the_kill.ini");
        try
        {
            foreach(var raw in File.ReadLines(path))
            {
                var line=raw.Split('#',2)[0].Trim();
                if(!line.Contains('=')) continue;
                var pair=line.Split('=',2); var key=pair[0].Trim(); var value=pair[1].Trim();
                if(key.Equals("obs_overlay_enabled",StringComparison.OrdinalIgnoreCase))
                    enabled=value is not ("0" or "false" or "off" or "no");
                else if(key.Equals("obs_overlay_port",StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(value,out var configuredPort))
                    port=Math.Clamp(configuredPort,1024,65535);
            }
        }
        catch(IOException) { }
        return new(enabled,port);
    }
}

internal sealed class ObsOverlayHost : IDisposable
{
    static readonly JsonSerializerOptions JsonOptions=new(){PropertyNamingPolicy=JsonNamingPolicy.CamelCase};
    readonly TcpListener? listener;
    readonly Thread? thread;
    readonly string html="";
    volatile bool stopping;
    OverlaySnapshot? snapshot;

    internal string Status { get; }
    internal string? Url { get; }

    internal ObsOverlayHost()
    {
        var settings=ObsOverlaySettings.Load();
        if(!settings.Enabled)
        {
            Status="OBS browser overlay disabled by configuration.";
            return;
        }
        try
        {
            html=File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"obs-overlay.html"));
            listener=new TcpListener(IPAddress.Loopback,settings.Port);
            listener.Start();
            Url=$"http://127.0.0.1:{settings.Port}/";
            Status=$"OBS browser overlay: {Url} (430 x 492)";
            thread=new Thread(Serve){IsBackground=true,Name="Heavier by the Kill OBS overlay"};
            thread.Start();
        }
        catch(Exception error) when(error is IOException or SocketException or UnauthorizedAccessException)
        {
            listener?.Stop();
            Status=$"OBS browser overlay unavailable: {error.Message}";
        }
    }

    internal void Update(OverlaySnapshot value)=>Volatile.Write(ref snapshot,value);

    internal void ClearPrompt()
    {
        var current=Volatile.Read(ref snapshot);
        if(current is not null) Volatile.Write(ref snapshot,current with {Prompt=MenuPrompt.None});
    }

    void Serve()
    {
        while(!stopping&&listener is not null)
        {
            try
            {
                var client=listener.AcceptTcpClient();
                ThreadPool.QueueUserWorkItem(_=>Handle(client));
            }
            catch(SocketException) when(stopping) { break; }
            catch(ObjectDisposedException) when(stopping) { break; }
        }
    }

    void Handle(TcpClient client)
    {
        using(client)
        {
            try
            {
                client.ReceiveTimeout=2000; client.SendTimeout=2000;
                using var stream=client.GetStream();
                var request=ReadRequest(stream);
                var firstLine=request.Split("\r\n",2)[0].Split(' ');
                var path=firstLine.Length>=2?firstLine[1].Split('?',2)[0]:"/";
                if(path=="/"||path=="/index.html")
                    WriteResponse(stream,"200 OK","text/html; charset=utf-8",Encoding.UTF8.GetBytes(html));
                else if(path=="/state")
                    WriteResponse(stream,"200 OK","application/json; charset=utf-8",StateJson());
                else if(path=="/health")
                    WriteResponse(stream,"200 OK","text/plain; charset=utf-8",Encoding.UTF8.GetBytes("ok"));
                else
                    WriteResponse(stream,"404 Not Found","text/plain; charset=utf-8",Encoding.UTF8.GetBytes("not found"));
            }
            catch(IOException) { }
            catch(SocketException) { }
        }
    }

    static string ReadRequest(NetworkStream stream)
    {
        var bytes=new List<byte>(1024); var buffer=new byte[1024];
        while(bytes.Count<8192)
        {
            var read=stream.Read(buffer,0,buffer.Length);
            if(read<=0) break;
            bytes.AddRange(buffer.AsSpan(0,read).ToArray());
            if(bytes.Count>=4&&bytes[^4]==13&&bytes[^3]==10&&bytes[^2]==13&&bytes[^1]==10) break;
        }
        return Encoding.ASCII.GetString(bytes.ToArray());
    }

    byte[] StateJson()
    {
        var state=Volatile.Read(ref snapshot);
        if(state is null) return "{\"ready\":false}"u8.ToArray();
        var reforgeWeight=ReforgeRules.BatchWeight(state.Progress.Temporary);
        var reforgeCost=ReforgeRules.Cost(reforgeWeight);
        var total=Math.Max(0,state.Modifiers.Weight);
        var temporaryFraction=total<=0?0:Math.Clamp(state.Progress.Temporary/total,0,1);
        var permanentFraction=total<=0?0:Math.Clamp(state.Progress.Permanent/total,0,1);
        var payload=new
        {
            ready=true,
            tier=state.Tier,
            weaponType=state.WeaponClass.ToString(),
            total=state.Modifiers.Weight,
            temporary=state.Progress.Temporary,
            permanent=state.Progress.Permanent,
            temporaryFraction,
            permanentFraction,
            damage=state.Modifiers.Damage,
            speed=state.Modifiers.AttackSpeed,
            stamina=state.Modifiers.StaminaCost,
            toast=state.Toast,
            prompt=state.Prompt switch {MenuPrompt.Reforge=>"reforge",MenuPrompt.BonfireRest=>"bonfire",_=>"none"},
            bonfireConsumed=state.BonfireRestConsumed,
            restPercent=MathF.Round(state.RestDecayFraction*100),
            reforgeWeight,
            reforgeCost,
            canAfford=reforgeWeight>0&&state.Souls>=reforgeCost
        };
        return JsonSerializer.SerializeToUtf8Bytes(payload,JsonOptions);
    }

    static void WriteResponse(NetworkStream stream,string status,string contentType,byte[] body)
    {
        var header=Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {status}\r\nContent-Type: {contentType}\r\nContent-Length: {body.Length}\r\nCache-Control: no-store, no-cache, must-revalidate\r\nAccess-Control-Allow-Origin: *\r\nConnection: close\r\n\r\n");
        stream.Write(header); stream.Write(body); stream.Flush();
    }

    public void Dispose()
    {
        stopping=true;
        listener?.Stop();
        if(thread?.IsAlive==true) thread.Join(1000);
    }
}
