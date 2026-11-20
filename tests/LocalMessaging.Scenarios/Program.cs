using Microsoft.Extensions.Hosting;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LocalMessaging.Client;
using LocalMessaging.Contracts;
using LocalMessaging.Host;
using LocalMessaging.Infrastructure;
using LocalMessaging.Platform.Windows;
using HostSettings=LocalMessaging.Host.HostOptions;

public static partial class ScenarioProgram
{
    private static readonly List<object> Checks=[];
    private static int _checks;
    private static string _suite="transport";
    private static bool _publishedMode;
    public static async Task<int> Main(string[] args)
    {
        var values=new Dictionary<string,string>();
        for(var i=0;i<args.Length;i++)
        {
            if(args[i]=="--serve-fixture") { values["--serve-fixture"]="true"; continue; }
            if(i+1>=args.Length || !args[i].StartsWith("--",StringComparison.Ordinal)) { Console.Error.WriteLine("Unknown/incomplete argument."); return 2; }
            values[args[i]]=args[++i];
        }
        string[] allowed=["--suite","--evidence","--port","--serve-fixture","--data-dir","--product"];
        if(values.Keys.Any(x=>!allowed.Contains(x))) { Console.Error.WriteLine("Unknown scenario argument."); return 2; }
        _suite=values.GetValueOrDefault("--suite","transport");
        if(_suite is not ("transport" or "contracts" or "windows")) { Console.Error.WriteLine("Unknown suite."); return 2; }
        var evidence=Path.GetFullPath(values.GetValueOrDefault("--evidence",Path.Combine("evidence","runtime",_suite+".json")));
        if (values.TryGetValue("--product",out var product)) return await RunPublishedAsync(Path.GetFullPath(product),evidence);
        if(_suite=="windows"&&!OperatingSystem.IsWindows())
        {
            await Report(evidence,"blocked",["Windows 11, real WIC, current-user certificate and Windows QUIC execution are unavailable on this OS."]);
            return 3;
        }
        try
        {
            if(_suite=="contracts") { ContractChecks(); await Report(evidence,"passed",[]); return 0; }
            var directory=values.TryGetValue("--data-dir",out var given)?Path.GetFullPath(given):Path.Combine(Path.GetTempPath(),"LocalMessaging-scenarios-"+Guid.NewGuid().ToString("N"));
            var port=values.TryGetValue("--port",out var specified)?int.Parse(specified,System.Globalization.CultureInfo.InvariantCulture):FreePort();
            var limits=new ProductLimits { HeartbeatSeconds=1,ClientIdleSeconds=4,StreamWriteSeconds=2,ActiveStreams=4 };
            var options=new HostSettings { StateDirectory=directory,FrontendPort=port,MessagingPort=FreePort(),
                WebRoot=FindWebRoot(),Limits=limits };
            await using var store=new MessagingStore(new StoreOptions(options.EffectiveDataDirectory,Limits:limits));
            await store.InitializeAsync();
            X509Certificate2? certificate=null;
            IImageValidator validator=new FixtureImageValidator();
            if(_suite=="windows"&&OperatingSystem.IsWindows())
            {
                certificate=CertificateProvisioner.LoadOrCreate(options);
                validator=new WicImageValidator(limits);
            }
            try
            {
                await using var host=new HostRuntime(options,store,validator,certificate,requireHttp3:_suite=="windows");
                await host.StartAsync();
                if(values.ContainsKey("--serve-fixture"))
                {
                    Console.WriteLine(JsonSerializer.Serialize(new { mode="test_fixture",frontend=options.FrontendOrigin,processId=Environment.ProcessId,dataDirectory=directory,windowsQualified=false,imageDecoder="exact known fixture only" }));
                    await host.App.WaitForShutdownAsync();
                    return 0;
                }
                ContractChecks();
                await TransportChecks(host.InstanceId,options,certificate?.GetCertHashString(HashAlgorithmName.SHA256),host);
                await RegistryChecks();
                await Report(evidence,"passed",_suite=="windows"?[]:["Portable HTTP/1 fixture only; Windows HTTP/3, real WIC, WinUI and clean-machine qualification are not passed by this suite."]);
            }
            finally
            {
                if(validator is IDisposable disposable) disposable.Dispose();
                if(certificate is not null && OperatingSystem.IsWindows())
                {
                    // Only remove the certificate this isolated scenario created.
                    using var certificates=new X509Store(StoreName.My,StoreLocation.CurrentUser);
                    certificates.Open(OpenFlags.ReadWrite);
                    certificates.Remove(certificate);
                    certificate.Dispose();
                }
            }
            return 0;
        }
        catch(Exception e)
        {
            Console.Error.WriteLine(e);
            await Report(evidence,"failed",[e.GetType().Name+": "+e.Message]);
            return 1;
        }
    }
    private static void Check(bool condition,string name)
    {
        if(!condition) throw new InvalidOperationException(name);
        _checks++;
        Checks.Add(new { id=name,result="passed" });
        Console.WriteLine("PASS "+name);
    }
    private static async Task Report(string path,string result,string[] limitations)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path,JsonSerializer.Serialize(new { suite=_suite,result,atUtc=DateTime.UtcNow,
            os=System.Runtime.InteropServices.RuntimeInformation.OSDescription,runtime=Environment.Version.ToString(),checks=_checks,results=Checks,limitations },
            new JsonSerializerOptions { WriteIndented=true }));
        Console.WriteLine(JsonSerializer.Serialize(new { suite=_suite,result,checks=_checks,evidence=path }));
    }
    private static void ContractChecks()
    {
        var user=Envelope(null,new JsonObject{["type"]="user",["handle"]="ALICE",["displayName"]="Alice"});
        var typed=Wire.ReadRequest<CreateRequest>(Element(user));
        Check(typed.DataObject is CreateUser,"contracts.discriminated_user");
        var normalized=JsonSerializer.SerializeToElement(typed,Wire.RequestOptions);
        Check(Wire.ReadRequest<CreateRequest>(normalized).RequestId==typed.RequestId,"contracts.round_trip");
        var copy=(JsonObject)user.DeepClone(); copy["unknown"]=true;
        ExpectContractError(copy,"contracts.unknown_property_rejected");
        copy=(JsonObject)user.DeepClone(); copy["dataObject"]!["type"]="not_a_type";
        ExpectContractError(copy,"contracts.unknown_discriminator_rejected");
        copy=(JsonObject)user.DeepClone(); copy.Remove("requestId");
        ExpectContractError(copy,"contracts.required_request_id");
        copy=(JsonObject)user.DeepClone(); copy["requestId"]="not-uuid";
        ExpectContractError(copy,"contracts.uuid_type");
    }
    private static void ExpectContractError(JsonObject request,string name)
    {
        try { Wire.ReadRequest<CreateRequest>(Element(request)); throw new InvalidOperationException("Expected contract rejection."); }
        catch(ApiException e) { Check(e.Status==400&&e.Code=="invalid_contract",name); }
    }
    private static JsonElement Element(JsonNode node)=>JsonSerializer.SerializeToElement(node);
    private static JsonObject Envelope(string? caller,JsonObject data)=>new(){["contractVersion"]=1,["requestId"]=Guid.NewGuid().ToString("D"),["callerUserId"]=caller,["dataObject"]=data};
    private static string Id(JsonObject receipt,string type)=>receipt["dataObjects"]![type]!["id"]!.GetValue<string>();
    private static JsonObject Message(string caller,string channel,string session,string text,string? parent=null,params string[] targets)=>
        Envelope(caller,new JsonObject{["type"]="message",["channel"]=new JsonObject{["mode"]="existing",["id"]=channel},
            ["session"]=new JsonObject{["mode"]="existing",["id"]=session},["content"]=new JsonObject{["type"]="text",["text"]=text},
            ["parentMessageId"]=parent,["targetUserIds"]=new JsonArray(targets.Select(x=>(JsonNode?)JsonValue.Create(x)).ToArray())});

    private static async Task TransportChecks(Guid instanceId,HostSettings options,string? certificatePin,HostRuntime? fixtureHost=null)
    {
        using var handler=new HttpClientHandler{UseProxy=false};
        using var http=new HttpClient(handler){BaseAddress=new Uri(options.FrontendBaseUrl),Timeout=TimeSpan.FromSeconds(15)};
        http.DefaultRequestHeaders.Add(Wire.ClientHeader,Wire.ClientHeaderValue);
        using var native=new MessagingClient(new Uri(options.FrontendBaseUrl));
        async Task<(HttpStatusCode Status,JsonObject Body)> Send(string route,JsonObject data)
        {
            using var response=await http.PostAsync(route,new StringContent(data.ToJsonString(),Encoding.UTF8,"application/json"));
            var body=JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
            return(response.StatusCode,body);
        }
        async Task<JsonObject> Get(string route)
        {
            using var response=await http.GetAsync(route);
            var text=await response.Content.ReadAsStringAsync();
            if(!response.IsSuccessStatusCode) throw new InvalidOperationException(route+" failed "+response.StatusCode+": "+text);
            return JsonNode.Parse(text)!.AsObject();
        }
        var status=await Get("status");
        Check(status["ready"]!.GetValue<bool>()&&status["instanceId"]!.GetValue<string>()==instanceId.ToString(),"http.status_real_instance");
        Check(status["startupProbe"]!["result"]!.GetValue<string>()==(_suite=="windows"?"http3_exact_verified":"not_run_test_fixture_http1_only"),"http.probe_is_truthful");
        using(var response=await http.GetAsync(options.FrontendOrigin+"/"))
            Check(response.StatusCode==HttpStatusCode.OK&&response.Headers.Contains("Content-Security-Policy"),"http.static_with_csp");
        using(var response=await http.GetAsync(options.FrontendOrigin+"/messaging/v1/status"))
            Check(response.StatusCode==HttpStatusCode.NotFound,"http.wrong_surface_404");
        using(var request=new HttpRequestMessage(HttpMethod.Get,"status"))
        {
            request.Headers.Host="evil.example:"+options.FrontendPort;
            using var response=await http.SendAsync(request);
            Check(response.StatusCode==HttpStatusCode.BadRequest,"http.host_spoof_400");
        }
        using(var naked=new HttpClient(new HttpClientHandler{UseProxy=false}))
        using(var response=await naked.GetAsync(options.FrontendBaseUrl+"users"))
            Check(response.StatusCode==HttpStatusCode.BadRequest,"http.client_header_required");
        using(var request=new HttpRequestMessage(HttpMethod.Post,"requests"))
        {
            request.Headers.Add("Origin","https://unrelated.invalid");
            request.Content=new StringContent(Envelope(null,new JsonObject{["type"]="user",["handle"]="evil",["displayName"]="Evil"}).ToJsonString(),Encoding.UTF8,"application/json");
            using var response=await http.SendAsync(request);
            Check(response.StatusCode==HttpStatusCode.Forbidden,"http.origin_rejected_before_write");
        }
        using(var request=new HttpRequestMessage(HttpMethod.Get,"users"))
        {
            request.Headers.Add("Sec-Fetch-Site","cross-site");
            using var response=await http.SendAsync(request);
            Check(response.StatusCode==HttpStatusCode.Forbidden,"http.cross_site_read_rejected");
        }
        var aRequest=Envelope(null,new JsonObject{["type"]="user",["handle"]="ALICE",["displayName"]="Alice"});
        var a=await Send("requests",aRequest); var aid=Id(a.Body,"user");
        Check(a.Status==HttpStatusCode.Created&&a.Body["dataObjects"]!["user"]!["handle"]!.GetValue<string>()=="alice","http.user_normalization");
        Check(a.Body["dataObjects"]!["user"]!["createdAtUtc"]!.GetValue<string>().EndsWith('Z'),"http.utc_timestamp");
        var retry=await Send("requests",aRequest);
        Check(retry.Status==HttpStatusCode.OK&&retry.Body["replayed"]!.GetValue<bool>()&&Id(retry.Body,"user")==aid,"http.idempotent_retry");
        var conflict=(JsonObject)aRequest.DeepClone(); conflict["dataObject"]!["displayName"]="Different";
        var conflicting=await Send("requests",conflict);
        Check(conflicting.Status==HttpStatusCode.Conflict&&conflicting.Body["code"]!.GetValue<string>()=="idempotency_conflict","http.conflicting_retry");
        var b=await Send("requests",Envelope(null,new JsonObject{["type"]="user",["handle"]="bryan",["displayName"]="Bryan"}));var bid=Id(b.Body,"user");
        var channel=await Send("requests",Envelope(aid,new JsonObject{["type"]="channel",["name"]="Architecture"}));
        var cid=Id(channel.Body,"channel"); var initial=Id(channel.Body,"session");
        Check(channel.Body["dataObjects"]!["channel"]!["ownerUserId"]!.GetValue<string>()==aid&&channel.Body["dataObjects"]!["session"]!["ownerUserId"]!.GetValue<string>()==aid,"http.channel_initial_owner");
        var session=await Send("requests",Envelope(bid,new JsonObject{["type"]="session",["channel"]=new JsonObject{["mode"]="existing",["id"]=cid},["title"]="Bryan review"}));
        var sid=Id(session.Body,"session");
        Check(session.Body["dataObjects"]!["session"]!["ownerUserId"]!.GetValue<string>()==bid&&session.Body["dataObjects"]!["channel"]!["ownerUserId"]!.GetValue<string>()==aid,"http.session_independent_owner");
        var root=await Send("requests",Message(aid,cid,sid,"<script>alert(1)</script> Hello @BRYAN",null,bid));
        var mid=Id(root.Body,"message");
        Check(root.Body["dataObjects"]!["message"]!["targetUserIds"]!.AsArray().Count==1,"http.targets_union");
        var child=await Send("requests",Message(bid,cid,sid,"reply",mid));var childId=Id(child.Body,"message");
        var grand=await Send("requests",Message(aid,cid,sid,"nested",childId));var grandId=Id(grand.Body,"message");
        Check(grand.Body["dataObjects"]!["message"]!["depth"]!.GetValue<int>()==3,"http.three_levels");
        Check(child.Body["dataObjects"]!["message"]!["targetUserIds"]!.AsArray().Count==0,"http.no_inherited_targets");
        var tooDeep=await Send("requests",Message(aid,cid,sid,"four",grandId));
        Check(tooDeep.Status==HttpStatusCode.UnprocessableEntity,"http.level_four_rejected");
        var badParent=await Send("requests",Message(aid,cid,initial,"wrong session",mid));
        Check(badParent.Status==HttpStatusCode.UnprocessableEntity,"http.cross_session_parent_rejected");
        var unknown=await Send("requests",Message(aid,cid,sid,"@unknown_person"));
        Check(unknown.Status==HttpStatusCode.UnprocessableEntity&&unknown.Body["code"]!.GetValue<string>()=="unknown_mention","http.unknown_mention");
        var literal=await Send("requests",Message(aid,cid,sid,@"person@bryan.org \@bryan @ab @2bryan"));
        Check(literal.Status==HttpStatusCode.Created&&literal.Body["dataObjects"]!["message"]!["targetUserIds"]!.AsArray().Count==0,"http.mention_grammar");
        var thread=await Get("messages/"+grandId+"/thread?limit=1");
        Check(thread["root"]!["id"]!.GetValue<string>()==mid&&thread["items"]!.AsArray().Count==1,"http.thread_root_and_page");
        var mentions=await Get("users/"+bid+"/mentions");
        Check(mentions["items"]!.AsArray().Count==1,"http.mentions_feed");
        var roots=await Get("sessions/"+sid+"/messages?view=roots");
        Check(roots["items"]!.AsArray().All(x=>x!["depth"]!.GetValue<int>()==1),"http.root_history");
        var flat=await Get("sessions/"+sid+"/messages?view=flat");
        Check(flat["items"]!.AsArray().Count==4,"http.flat_history");
        using(var response=await http.DeleteAsync("messages/"+mid))Check(response.StatusCode==HttpStatusCode.MethodNotAllowed,"http.no_domain_delete");
        var h=(await Get("status"))["headCursor"]!.GetValue<string>();
        var future=await Send("requests",Envelope(aid,new JsonObject{["type"]="channel",["name"]="Created after H"}));
        var historical=await Get("channels?asOf="+Uri.EscapeDataString(h));
        Check(historical["items"]!.AsArray().Count==1,"http.snapshot_excludes_future");
        var saved=await Send("subscriptions",new JsonObject{["contractVersion"]=1,["requestId"]=Guid.NewGuid().ToString(),["callerUserId"]=aid,
            ["filter"]=new JsonObject{["catalog"]=true,["allMessages"]=true},["start"]=new JsonObject{["mode"]="after",["cursor"]=h}});
        var sub=saved.Body["subscription"]!["id"]!.GetValue<string>();
        Check(saved.Status==HttpStatusCode.Created&&saved.Body["operation"]!.GetValue<string>()=="createSubscription","http.saved_subscription_receipt");
        var replay=await Get("subscriptions/"+sub+"/events");
        Check(replay["items"]!.AsArray().Count==2,"http.snapshot_plus_replay");
        using(var response=await http.GetAsync("subscriptions/"+sub+"/events?after="+Uri.EscapeDataString(Guid.NewGuid()+":0")))
            Check(response.StatusCode==HttpStatusCode.Conflict,"http.stale_epoch");
        var overlap=await Send("subscriptions",new JsonObject{["contractVersion"]=1,["requestId"]=Guid.NewGuid().ToString(),["callerUserId"]=aid,
            ["filter"]=new JsonObject{["channelIds"]=new JsonArray(cid),["targetUserId"]=bid},["start"]=new JsonObject{["mode"]="beginning"}});
        var overlapId=overlap.Body["subscription"]!["id"]!.GetValue<string>();
        var events=await Get("subscriptions/"+overlapId+"/events");
        Check(events["items"]!.AsArray().Count(x=>x!["dataObject"]!["id"]!.GetValue<string>()==mid)==1,"http.overlap_one_event");
        await ImageChecks(http,aid,cid,sid);
        var openapi=await Get("openapi.json");
        Check(openapi["openapi"]!.GetValue<string>()=="3.1.0"&&openapi["paths"]!["/subscriptions/{id}/stream"] is not null,"http.generated_openapi");
        var streamStart=(await Get("status"))["headCursor"]!.GetValue<string>();
        var streaming=await native.CreateSubscriptionAsync(Guid.Parse(aid),streamStart); var streamId=streaming["id"]!.GetValue<string>();
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var c1=native.ReadStreamAsync(streamId,streamStart,deadline.Token).GetAsyncEnumerator();
        await using var c2=native.ReadStreamAsync(streamId,streamStart,deadline.Token).GetAsyncEnumerator();
        var first=NextDomain(c1);var second=NextDomain(c2);
        await Until(async()=> (await Get("subscriptions/"+streamId))["activeConnectionCount"]!.GetValue<int>()==2);
        Check(true,"http.two_streams_attached");
        var live=await Send("requests",Message(aid,cid,sid,"live message"));
        var frames=await Task.WhenAll(first,second);
        Check(frames.All(f=>JsonNode.Parse(f.Data)!["dataObject"]!["id"]!.GetValue<string>()==Id(live.Body,"message")),"http.broadcast_independent_consumers");
        Check(frames.All(f=>f.Id==live.Body["afterRequestCursor"]!.GetValue<string>()),"http.event_commit_cursor");
        var close=await native.CloseSubscriptionAsync(Guid.Parse(aid),streamId,deadline.Token);
        Check(close["state"]!.GetValue<string>()=="committed"&&close["subscriptionState"]!.GetValue<string>()=="closed","http.close_receipt_states");
        await Until(async()=> (await Get("subscriptions/"+streamId))["activeConnectionCount"]!.GetValue<int>()==0);
        Check(true,"http.close_cancels_all");
        using(var response=await http.GetAsync("subscriptions/"+streamId+"/events"))
            Check(response.StatusCode==HttpStatusCode.Gone,"http.closed_finite_read");
        using(var response=await http.GetAsync("subscriptions/"+streamId+"/stream"))
            Check(response.StatusCode==HttpStatusCode.Gone,"http.closed_attachment");
        var storedReceipt=await Get("requests/"+live.Body["requestId"]!.GetValue<string>());
        Check(storedReceipt["state"]!.GetValue<string>()=="committed","http.queryable_receipt");
        await PublicProfileChecks(native,native,aid,cid,sid,mid,childId,grandId,"http");
        if(certificatePin is not null)
        {
            using var h3=new MessagingClient(new Uri(options.MessagingBaseUrl),certificatePin,true);
            var h3View=new MessagingClient(new Uri(options.FrontendBaseUrl));
            await PublicProfileChecks(h3,h3View,bid,cid,sid,childId,grandId,mid,"windows.h3");
            var actual=await h3.SendAsync(FrozenCommand.Create(Guid.Parse(aid),Message(aid,cid,sid,"actual H3")["dataObject"]!.AsObject()));
            Check(actual["state"]!.GetValue<string>()=="committed","windows.actual_h3_persisted_publish");
            var descriptor=await h3.CreateSubscriptionAsync(Guid.Parse(aid),streamStart);
            using var h3deadline=new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await using var h3stream=h3.ReadStreamAsync(descriptor["id"]!.GetValue<string>(),streamStart,h3deadline.Token).GetAsyncEnumerator();
            Check((await NextDomain(h3stream)).Event=="message.created","windows.actual_h3_durable_sse");
            await Http3BoundaryChecks(options,certificatePin);
        }
        if (fixtureHost is not null) await LostWakeChecks(fixtureHost,options,aid,cid,sid);
        await SlowStreamChecks(options,aid,cid,sid);
        if (!_publishedMode) await HostReviewChecks.RunAsync(options,Check);
        using(var response=await http.PostAsync("host/stop",new StringContent("{}",Encoding.UTF8,"application/json")))
            Check(response.StatusCode==HttpStatusCode.OK,"http.controlled_stop");
    }

    private static async Task ThrowsApi(Func<Task> action,string name)
    {
        try { await action(); }
        catch(Exception ex) when (ex is MessagingApiException or HttpRequestException) { return; }
        throw new InvalidOperationException(name+" did not fail.");
    }

    private static async Task PublicProfileChecks(MessagingClient native,MessagingClient fallback,string alice,string channel,string session,string firstId,string unusedId,string missingId,string prefix)
    {
        Task<JsonObject> Ack(string user,string message)=>native.SendAsync(FrozenCommand.Create(Guid.Parse(user),new JsonObject{["type"]="acknowledgement",["messageId"]=message}));
        var frozen=FrozenCommand.CreateAcknowledgement(Guid.Parse(alice),Guid.Parse(firstId));
        var ack=await native.SendAsync(frozen);
        Check(ack["operation"]!.GetValue<string>()=="createObject"&&ack["created"]!.AsArray().Any(x=>x!["type"]!.GetValue<string>()=="acknowledgement"),prefix+".acknowledge_committed");
        var retry=await native.SendAsync(frozen);
        Check(retry["replayed"]!.GetValue<bool>()&&retry["dataObjects"]!.ToJsonString()==ack["dataObjects"]!.ToJsonString(),prefix+".acknowledge_retry_replays");
        var acks=await fallback.GetAsync("messages/"+firstId+"/acknowledgements?limit=1");
        Check(acks["items"]!.AsArray().Count==1&&acks["items"]![0]!["profile"]!["id"]!.GetValue<string>()==alice&&acks["snapshotCursor"] is not null&&acks["nextPageToken"] is null,prefix+".acknowledgements_public_page");
        var saved=await fallback.GetAsync("messages/"+unusedId+"/acknowledgements");
        Check(saved["items"]!.AsArray().Count==0,prefix+".acknowledgements_visible_without_delivery");
        await native.SendAsync(FrozenCommand.Create(Guid.Parse(alice),new JsonObject{["type"]="participation",["channelId"]=channel,["joined"]=true}));
        Task<JsonObject> Scope(JsonObject action)=>native.SendAsync(FrozenCommand.Create(Guid.Parse(alice),action));
        var joins=await Scope(new JsonObject{["type"]="participation",["sessionId"]=session,["joined"]=true});
        Check(joins["operation"]!.GetValue<string>()=="createObject"&&joins["dataObjects"]!["participation"]!["joined"]!.GetValue<bool>(),prefix+".join_session_committed");
        var channelLeave=await Scope(new JsonObject{["type"]="participation",["channelId"]=channel,["joined"]=false});
        var state=channelLeave["dataObjects"]!["participation"]!;
        var channelJoined=state["channelJoined"] is not null ? state["channelJoined"]!.GetValue<bool>() : !state["joined"]!.GetValue<bool>();
        Check(!state["joined"]!.GetValue<bool>()&&channelJoined,prefix+".channel_leave_keeps_session");
        var roster=await fallback.GetAsync("channels/"+channel+"/participants");
        Check(roster["items"]!.AsArray().Any(x=>x!["profile"]!["id"]!.GetValue<string>()==alice)&&roster["snapshotCursor"] is not null,prefix+".roster_public_and_paginated");
        var sessionRoster=await fallback.GetAsync("sessions/"+session+"/participants");
        Check(sessionRoster["items"]!.AsArray().Any(x=>x!["profile"]!["id"]!.GetValue<string>()==alice),prefix+".session_roster_matches_parent");
        var leave=await native.SendAsync(FrozenCommand.Create(Guid.Parse(alice),new JsonObject{["type"]="participation",["channelId"]=channel,["joined"]=false}));
        Check(leave["created"]!.AsArray().Count==0,prefix+".repeated_leave_is_noop");
        await ThrowsApi(()=>fallback.GetAsync("channels/"+Guid.NewGuid()+"/participants"),prefix+".unknown_roster_404");
        await ThrowsApi(()=>native.SendAsync(FrozenCommand.Create(Guid.Parse(alice),new JsonObject{["type"]="participation",["joined"]=true})),prefix+".empty_scope_rejected");
        await ThrowsApi(()=>Ack(alice,Guid.NewGuid().ToString()),prefix+".unknown_message_404");
        var status=await fallback.GetAsync("status");
        Check(status["capabilities"]!["profileAcknowledgements"]!.GetValue<bool>()&&status["capabilities"]!["participantRosters"]!.GetValue<bool>(),prefix+".capabilities_advertise_public_profiles");
    }

    private static async Task ImageChecks(HttpClient http,string caller,string channel,string session)
    {
        var data=Message(caller,channel,session,"unused");
        data["dataObject"]!["content"]=new JsonObject{["type"]="image",["caption"]="Image @bryan"};
        var bytes=await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory,"fixtures","sample.png"));
        async Task<(HttpStatusCode Status,JsonObject Body)> Submit(byte[] payload,string filename,string boundary,string contentType="image/png")
        {
            using var multipart=new MultipartFormDataContent(boundary);
            multipart.Add(new StringContent(data.ToJsonString(),Encoding.UTF8,"application/json"),"request");
            var part=new ByteArrayContent(payload);part.Headers.ContentType=new MediaTypeHeaderValue(contentType);multipart.Add(part,"image",filename);
            using var response=await http.PostAsync("requests",multipart);
            return(response.StatusCode,JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject());
        }
        var image=await Submit(bytes,"first.png","first-boundary");
        Check(image.Status==HttpStatusCode.Created,"http.multipart_image_transaction");
        var descriptor=image.Body["dataObjects"]!["message"]!["content"]!["image"]!;
        using(var response=await http.GetAsync(descriptor["contentPath"]!.GetValue<string>()))
        {
            var stored=await response.Content.ReadAsByteArrayAsync();
            Check(response.StatusCode==HttpStatusCode.OK&&stored.SequenceEqual(bytes),"http.image_bytes_roundtrip");
            Check(response.Headers.ETag is not null&&response.Headers.Contains("X-Content-Type-Options"),"http.image_headers");
        }
        var retry=await Submit(bytes,"different.png","second-boundary");
        Check(retry.Status==HttpStatusCode.OK&&retry.Body["replayed"]!.GetValue<bool>(),"http.multipart_identity_semantic");
        Check(!image.Body.ToJsonString().Contains(Convert.ToBase64String(bytes),StringComparison.Ordinal),"http.image_descriptor_only");
        data["requestId"]=Guid.NewGuid().ToString();
        var damaged=bytes.ToArray();damaged[^5]^=1;
        var invalid=await Submit(damaged,"bad.png","third-boundary");
        Check(invalid.Status==HttpStatusCode.UnprocessableEntity,"http.invalid_image_no_commit");
        using(var receipt=await http.GetAsync("requests/"+data["requestId"]!.GetValue<string>()))
            Check(receipt.StatusCode==HttpStatusCode.NotFound,"http.invalid_image_no_receipt");
        var spoof=await Submit(bytes,"spoof.jpg","fourth-boundary","image/jpeg");
        Check(spoof.Status==HttpStatusCode.UnsupportedMediaType,"http.image_declared_type_mismatch");
        var oversized=await Submit(new byte[8*1024*1024+1],"oversize.png","fifth-boundary");
        Check(oversized.Status==HttpStatusCode.RequestEntityTooLarge,"http.image_byte_bound");
        if(_suite=="windows")
        {
            data["requestId"]=Guid.NewGuid().ToString();
            var jpegBytes=await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory,"fixtures","sample.jpg"));
            var jpeg=await Submit(jpegBytes,"photo.jpg","jpeg-boundary","image/jpeg");
            Check(jpeg.Status==HttpStatusCode.Created&&jpeg.Body["dataObjects"]!["message"]!["content"]!["image"]!["contentType"]!.GetValue<string>()=="image/jpeg","windows.real_jpeg_multipart");
            using var jpegContent=await http.GetAsync(jpeg.Body["dataObjects"]!["message"]!["content"]!["image"]!["contentPath"]!.GetValue<string>());
            Check((await jpegContent.Content.ReadAsByteArrayAsync()).SequenceEqual(jpegBytes),"windows.jpeg_bytes_roundtrip");
        }
    }
    private static async Task<SseFrame> NextDomain(IAsyncEnumerator<SseFrame> enumerator)
    {
        while(await enumerator.MoveNextAsync()) if(enumerator.Current.Event.EndsWith(".created",StringComparison.Ordinal))return enumerator.Current;
        throw new InvalidOperationException("Stream ended before a domain event.");
    }
    private static async Task Until(Func<Task<bool>> condition)
    {
        var timer=Stopwatch.StartNew();
        while(timer.Elapsed<TimeSpan.FromSeconds(5))
        {
            if(await condition())return;
            await Task.Delay(25);
        }
        throw new TimeoutException("Expected observable state was not reached.");
    }
    private static async Task RegistryChecks()
    {
        using var registry=new StreamRegistry(1);
        var id=Guid.NewGuid();var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var closed=false;
        var registration=registry.RegisterAsync(id,async()=>{
            if(closed)throw new ApiException(410,"subscription_closed","Closed.");
            entered.TrySetResult();await release.Task;
        },CancellationToken.None);
        await entered.Task;closed=true;registry.Close(id);release.SetResult();
        try { using var lease=await registration; throw new InvalidOperationException("Raced attachment should fail."); }
        catch(ApiException e) { Check(e.Status==410&&registry.Count==0,"registry.close_attach_race"); }
        using var live=await registry.RegisterAsync(Guid.NewGuid(),()=>Task.CompletedTask,CancellationToken.None);
        try { using var excess=await registry.RegisterAsync(Guid.NewGuid(),()=>Task.CompletedTask,CancellationToken.None); throw new InvalidOperationException("Expected limit."); }
        catch(ApiException e){Check(e.Status==429,"registry.stream_bound");}
        var first=registry.CaptureWake();var second=registry.CaptureWake();registry.Pulse();
        await Task.WhenAll(first,second);Check(true,"registry.broadcast_wakes_all");
    }
    private static int FreePort()
    {
        var socket=new TcpListener(IPAddress.Loopback,0);socket.Start();var port=((IPEndPoint)socket.LocalEndpoint).Port;socket.Stop();return port;
    }
    private static string FindWebRoot()
    {
        var p=new DirectoryInfo(Environment.CurrentDirectory);
        while(p is not null)
        {
            if(File.Exists(Path.Combine(p.FullName,"global.json"))&&Directory.Exists(Path.Combine(p.FullName,"src","LocalMessaging.Web")))return Path.Combine(p.FullName,"src","LocalMessaging.Web");
            p=p.Parent;
        }
        var bundled=Path.Combine(AppContext.BaseDirectory,"wwwroot");
        if(File.Exists(Path.Combine(bundled,"index.html")))return bundled;
        throw new DirectoryNotFoundException("Scenario web assets are missing from the published verifier.");
    }
    private sealed class FixtureImageValidator : IImageValidator
    {
        public async Task<ValidatedImage> ValidateAsync(byte[] bytes,CancellationToken ct=default)
        {
            var fixture=await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory,"fixtures","sample.png"),ct);
            if(!bytes.SequenceEqual(fixture))throw new ApiException(422,"invalid_image","The portable protocol fixture accepts only its exact known test image; real WIC is a separate Windows gate.");
            return new ValidatedImage(bytes,"image/png",2,2,Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
        }
    }
}
