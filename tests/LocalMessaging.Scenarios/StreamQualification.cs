using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using LocalMessaging.Contracts;
using LocalMessaging.Client;
using LocalMessaging.Host;
using Microsoft.Data.Sqlite;
using HostSettings=LocalMessaging.Host.HostOptions;

public static partial class ScenarioProgram
{
    private static async Task LostWakeChecks(HostRuntime host,HostSettings options,string caller,string channel,string session)
    {
        using var client=new MessagingClient(new Uri(options.FrontendBaseUrl));
        using var http=new HttpClient(new HttpClientHandler{UseProxy=false}) { BaseAddress=new Uri(options.FrontendBaseUrl) };
        http.DefaultRequestHeaders.Add(Wire.ClientHeader,Wire.ClientHeaderValue);
        using var statusResponse=await http.GetAsync("status");
        var head=JsonNode.Parse(await statusResponse.Content.ReadAsStringAsync())!["headCursor"]!.GetValue<string>();
        var subscription=await client.CreateSubscriptionAsync(Guid.Parse(caller),head);
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(10));
        host.AfterInitialStreamReadForTest=async ct=>{ entered.TrySetResult();await release.Task.WaitAsync(ct); };
        try
        {
            await using var stream=client.ReadStreamAsync(subscription["id"]!.GetValue<string>(),head,deadline.Token).GetAsyncEnumerator();
            var receiving=NextDomain(stream);
            await entered.Task.WaitAsync(deadline.Token);
            using var committed=await http.PostAsync("requests",new StringContent(Message(caller,channel,session,"commit between empty read and wait").ToJsonString(),Encoding.UTF8,"application/json"),deadline.Token);
            committed.EnsureSuccessStatusCode();
            var id=Id(JsonNode.Parse(await committed.Content.ReadAsStringAsync(deadline.Token))!.AsObject(),"message");
            // The wake has already happened before the stream captures its next wake token.
            release.TrySetResult();
            var frame=await receiving.WaitAsync(deadline.Token);
            Check(JsonNode.Parse(frame.Data)!["dataObject"]!["id"]!.GetValue<string>()==id,"http.commit_between_empty_read_and_wait_recovers_by_poll");
        }
        finally { release.TrySetResult();host.AfterInitialStreamReadForTest=null; }
    }

    private static async Task Http3BoundaryChecks(HostSettings options,string pin)
    {
        using var handler=new HttpClientHandler { UseProxy=false,
            ServerCertificateCustomValidationCallback=(_,certificate,_,errors)=>CertificateProvisioner.ValidatePeer(certificate,errors,pin) };
        using var http=new HttpClient(handler) { Timeout=TimeSpan.FromSeconds(10) };
        foreach (var path in new[]{"/api/v1/status","/index.html","/messaging/v1/host/stop"})
        {
            using var request=new HttpRequestMessage(HttpMethod.Get,$"https://localhost:{options.MessagingPort}"+path)
            { Version=HttpVersion.Version30,VersionPolicy=HttpVersionPolicy.RequestVersionExact };
            request.Headers.Add(Wire.ClientHeader,Wire.ClientHeaderValue);
            using var response=await http.SendAsync(request);
            Check(response.Version==HttpVersion.Version30&&response.StatusCode==HttpStatusCode.NotFound,"windows.h3_surface_isolation_"+path.Replace('/','_'));
        }
        using var status=new HttpRequestMessage(HttpMethod.Get,options.MessagingBaseUrl+"status")
        { Version=HttpVersion.Version30,VersionPolicy=HttpVersionPolicy.RequestVersionExact };
        status.Headers.Add(Wire.ClientHeader,Wire.ClientHeaderValue);
        using var actual=await http.SendAsync(status);
        Check(actual.Version==HttpVersion.Version30&&actual.IsSuccessStatusCode,"windows.actual_response_version_3_0");
        using var invalid=new HttpClient(new HttpClientHandler { UseProxy=false,
            ServerCertificateCustomValidationCallback=(_,certificate,_,errors)=>CertificateProvisioner.ValidatePeer(certificate,errors,new string('0',64)) });
        using var rejected=new HttpRequestMessage(HttpMethod.Get,options.MessagingBaseUrl+"status")
        { Version=HttpVersion.Version30,VersionPolicy=HttpVersionPolicy.RequestVersionExact };
        rejected.Headers.Add(Wire.ClientHeader,Wire.ClientHeaderValue);
        try { using var response=await invalid.SendAsync(rejected); throw new InvalidOperationException("An invalid certificate pin was accepted."); }
        catch(HttpRequestException) { Check(true,"windows.invalid_pin_rejected"); }
    }

    private static async Task SlowStreamChecks(HostSettings options,string caller,string channel,string session)
    {
        using var http=new HttpClient(new HttpClientHandler { UseProxy=false }) { BaseAddress=new Uri(options.FrontendBaseUrl),Timeout=TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.Add(Wire.ClientHeader,Wire.ClientHeaderValue);
        async Task<JsonObject> Get(string route)
        {
            using var response=await http.GetAsync(route);response.EnsureSuccessStatusCode();
            return JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
        }
        var head=(await Get("status"))["headCursor"]!.GetValue<string>();
        var request=new JsonObject { ["contractVersion"]=1,["requestId"]=Guid.NewGuid().ToString(),["callerUserId"]=caller,
            ["filter"]=new JsonObject { ["allMessages"]=true },["start"]=new JsonObject { ["mode"]="after",["cursor"]=head } };
        using var created=await http.PostAsync("subscriptions",new StringContent(request.ToJsonString(),Encoding.UTF8,"application/json"));
        created.EnsureSuccessStatusCode();
        var subscription=JsonNode.Parse(await created.Content.ReadAsStringAsync())!["subscription"]!["id"]!.GetValue<string>();
        using var stalledHandler=new SocketsHttpHandler { UseProxy=false,ConnectCallback=async (context,ct)=>
        {
            var socket=new Socket(SocketType.Stream,ProtocolType.Tcp) { ReceiveBufferSize=1024,NoDelay=true };
            try { await socket.ConnectAsync(context.DnsEndPoint,ct);return new NetworkStream(socket,ownsSocket:true); }
            catch { socket.Dispose();throw; }
        } };
        using var stalled=new HttpClient(stalledHandler) { Timeout=Timeout.InfiniteTimeSpan };
        stalled.DefaultRequestHeaders.Add(Wire.ClientHeader,Wire.ClientHeaderValue);
        using var lifetime=new CancellationTokenSource(TimeSpan.FromSeconds(options.Limits.StreamWriteSeconds+90));
        using var stream=await stalled.GetAsync(options.FrontendBaseUrl+"subscriptions/"+subscription+"/stream?after="+Uri.EscapeDataString(head),HttpCompletionOption.ResponseHeadersRead,lifetime.Token);
        stream.EnsureSuccessStatusCode();
        // Leave response bytes unread. The small real socket receive window makes backpressure observable.
        var published=new HashSet<string>(StringComparer.Ordinal);
        var watch=Stopwatch.StartNew();
        for(var i=0;i<500;i++)
        {
            var payload=Message(caller,channel,session,"stalled-"+i.ToString(System.Globalization.CultureInfo.InvariantCulture)+" "+new string('x',16000));
            using var response=await http.PostAsync("requests",new StringContent(payload.ToJsonString(),Encoding.UTF8,"application/json"));
            response.EnsureSuccessStatusCode();
            published.Add(Id(JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject(),"message"));
            if(i==49)
            {
                await using var connection=new SqliteConnection(new SqliteConnectionStringBuilder {
                    DataSource=Path.Combine(options.EffectiveDataDirectory,"messaging.db"),Mode=SqliteOpenMode.ReadWrite,Pooling=false }.ToString());
                await connection.OpenAsync();
                await using var checkpoint=connection.CreateCommand();checkpoint.CommandTimeout=5;checkpoint.CommandText="PRAGMA wal_checkpoint(TRUNCATE);";
                await using var reader=await checkpoint.ExecuteReaderAsync();await reader.ReadAsync();
                Check(reader.GetInt64(0)==0,"http.stalled_stream_allows_wal_checkpoint");
            }
        }
        Check(published.Count==500&&watch.Elapsed<TimeSpan.FromSeconds(60),"http.stalled_stream_publishers_progress");
        var closeWatch=Stopwatch.StartNew();
        while((await Get("subscriptions/"+subscription))["activeConnectionCount"]!.GetValue<int>()!=0
            &&closeWatch.Elapsed<TimeSpan.FromSeconds(options.Limits.StreamWriteSeconds+8)) await Task.Delay(100);
        Check((await Get("subscriptions/"+subscription))["activeConnectionCount"]!.GetValue<int>()==0,"http.stalled_stream_write_deadline_closes");
        var recovered=new HashSet<string>(StringComparer.Ordinal);var cursor=head;
        for(var page=0;page<100;page++)
        {
            var events=await Get("subscriptions/"+subscription+"/events?limit=200&after="+Uri.EscapeDataString(cursor));
            foreach(var item in events["items"]!.AsArray())recovered.Add(item!["dataObject"]!["id"]!.GetValue<string>());
            cursor=events["nextCursor"]!.GetValue<string>();
            if(!events["hasMore"]!.GetValue<bool>())break;
        }
        Check(published.SetEquals(recovered),"http.stalled_stream_replay_recovers_all");
        Console.WriteLine("METRIC slow-consumer publishers="+published.Count+" elapsedMs="+watch.ElapsedMilliseconds+"; received after finite replay="+recovered.Count);
    }
}
