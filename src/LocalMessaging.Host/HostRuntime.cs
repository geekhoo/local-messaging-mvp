using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LocalMessaging.Contracts;
using LocalMessaging.Infrastructure;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.FileProviders;
using Microsoft.Net.Http.Headers;

namespace LocalMessaging.Host;

internal sealed class HostRuntime : IAsyncDisposable
{
    private readonly MessagingStore _store;
    private readonly HostOptions _options;
    private readonly IImageValidator _images;
    private readonly X509Certificate2? _certificate;
    private readonly bool _requireHttp3;
    private readonly StreamRegistry _streams;
    private readonly SemaphoreSlim _imageResponses;
    private readonly SemaphoreSlim _imageCommands;
    private readonly SemaphoreSlim _commands = new(64,64);
    private volatile bool _ready;
    private volatile bool _stopping;
    private bool _disposed;
    private string _probeResult = "not_run";
    public Guid InstanceId { get; } = Guid.NewGuid();
    public WebApplication App { get; }
    public bool Ready => _ready;
    // Friend scenario assembly only: deterministic empty-read/wait interleaving, never a public route/configuration.
    internal Func<CancellationToken,Task>? AfterInitialStreamReadForTest { get; set; }

    internal HostRuntime(HostOptions options, MessagingStore store, IImageValidator images,
        X509Certificate2? certificate, bool requireHttp3)
    {
        options.Validate();
        _options=options; _store=store; _images=images; _certificate=certificate; _requireHttp3=requireHttp3;
        _streams=new(options.Limits.ActiveStreams);
        _imageResponses=new(options.Limits.ImageResponses,options.Limits.ImageResponses);
        _imageCommands=new(options.Limits.ImageValidations,options.Limits.ImageValidations);
        var webRoot = options.WebRoot ?? Path.Combine(AppContext.BaseDirectory,"wwwroot");
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args=[], ApplicationName=typeof(HostRuntime).Assembly.FullName,
            ContentRootPath=AppContext.BaseDirectory
        });
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(new BoundedLogProvider(Path.Combine(options.StateDirectory,"logs")));
        builder.Logging.AddFilter("Microsoft.AspNetCore",LogLevel.Warning);
        builder.Logging.AddFilter("Microsoft.EntityFrameworkCore",LogLevel.Warning);
        builder.Services.Configure<Microsoft.Extensions.Hosting.HostOptions>(o => o.ShutdownTimeout=TimeSpan.FromSeconds(options.Limits.DrainSeconds));
        builder.WebHost.ConfigureKestrel(server =>
        {
            server.AddServerHeader=false;
            server.Limits.MaxRequestBodySize=options.Limits.MultipartBytes;
            server.Limits.MaxRequestHeadersTotalSize=16*1024;
            server.Limits.MaxConcurrentConnections=128;
            server.Limits.RequestHeadersTimeout=TimeSpan.FromSeconds(15);
            server.Listen(IPAddress.Loopback,options.FrontendPort,l=>l.Protocols=HttpProtocols.Http1);
            if (requireHttp3)
            {
                if (certificate is null) throw new ArgumentException("A server certificate is required.");
                CertificateProvisioner.ValidateServer(certificate);
                void Configure(ListenOptions listener)
                {
                    listener.Protocols=HttpProtocols.Http3;
                    listener.UseHttps(https => { https.ServerCertificate=certificate; https.SslProtocols=SslProtocols.Tls13; });
                }
                server.Listen(IPAddress.Loopback,options.MessagingPort,Configure);
                if (Socket.OSSupportsIPv6) server.Listen(IPAddress.IPv6Loopback,options.MessagingPort,Configure);
            }
        });
        App=builder.Build();
        _store.Committed += _streams.Pulse;
        _store.SubscriptionClosed += _streams.Close;
        App.Lifetime.ApplicationStopping.Register(() => { _stopping=true; _ready=false; _streams.Stop(); });
        App.Use(async (context,next) =>
        {
            try { await next(context); }
            catch (ApiException e) { await ProblemAsync(context,e); }
            catch (Microsoft.AspNetCore.Http.BadHttpRequestException e)
            {
                await ProblemAsync(context,new ApiException(e.StatusCode,e.StatusCode==413?"request_too_large":"invalid_contract","The HTTP request is invalid or exceeds the configured limit."));
            }
            catch (Microsoft.Data.Sqlite.SqliteException e) when (e.SqliteErrorCode is 5 or 6) { await ProblemAsync(context,new ApiException(503,"database_busy","SQLite exceeded the finite busy timeout.")); }
            catch (Microsoft.Data.Sqlite.SqliteException e) when (e.SqliteErrorCode is 10 or 13 or 14) { await ProblemAsync(context,new ApiException(503,"storage_unavailable","Local storage is unavailable.")); }
            catch (JsonException) { await ProblemAsync(context,new ApiException(400,"invalid_contract","Malformed or incompatible JSON.")); }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested || _stopping) { context.Items["commandAborted"]=true; context.Abort(); }
            catch (Exception e)
            {
                App.Logger.LogError(new EventId(500),e,"Unhandled request failure. Trace {TraceId}",context.TraceIdentifier);
                await ProblemAsync(context,new ApiException(500,"internal_error","The request failed. See its diagnostic trace ID."));
            }
            finally
            {
                if (context.Items["commandTimer"] is Stopwatch timer)
                    App.Logger.LogInformation("Command outcome. Trace {TraceId}; requestId {RequestId}; status {Status}; elapsedMs {ElapsedMs}",
                        context.TraceIdentifier,context.Items["requestId"],context.Items.ContainsKey("commandAborted")?499:context.Response.StatusCode,timer.ElapsedMilliseconds);
            }
        });
        App.Use(async (context,next) =>
        {
            context.Response.Headers.XContentTypeOptions="nosniff";
            context.Response.Headers["Referrer-Policy"]="no-referrer";
            var port=context.Connection.LocalPort;
            var frontend=port==options.FrontendPort;
            var messaging=requireHttp3 && port==options.MessagingPort;
            if (!frontend && !messaging) { context.Response.StatusCode=404; return; }
            var authority=context.Request.Host;
            var expectedHost=frontend?"127.0.0.1":"localhost";
            var expectedPort=frontend?options.FrontendPort:options.MessagingPort;
            if (!string.Equals(authority.Host,expectedHost,StringComparison.OrdinalIgnoreCase)
                || (authority.Port ?? (frontend?80:443)) != expectedPort)
                throw new ApiException(400,"invalid_authority","Use the configured local endpoint authority.");
            if (context.Connection.RemoteIpAddress is { } remote && !IPAddress.IsLoopback(remote))
                throw new ApiException(403,"origin_not_allowed","Only local loopback clients are supported.");
            var path=context.Request.Path;
            var api=frontend?path.StartsWithSegments("/api/v1"):path.StartsWithSegments("/messaging/v1");
            if ((messaging && !api) || (frontend && path.StartsWithSegments("/messaging"))
                || (messaging && path.StartsWithSegments("/messaging/v1/host")))
            { context.Response.StatusCode=404; return; }
            if (api)
            {
                var origins=context.Request.Headers.Origin;
                if (origins.Count>1 || (origins.Count==1 && origins[0]!=options.FrontendOrigin))
                    throw new ApiException(403,"origin_not_allowed","Browser API requests must use the exact frontend origin.");
                var fetchSite=context.Request.Headers["Sec-Fetch-Site"];
                if (fetchSite.Count>1 || fetchSite.Any(v=>string.Equals(v,"cross-site",StringComparison.OrdinalIgnoreCase)))
                    throw new ApiException(403,"origin_not_allowed","Cross-site browser requests are not accepted.");
                if (context.Request.Headers[Wire.ClientHeader].Count!=1 || context.Request.Headers[Wire.ClientHeader][0]!=Wire.ClientHeaderValue)
                    throw new ApiException(400,"invalid_contract",$"API requests require {Wire.ClientHeader}: {Wire.ClientHeaderValue}.");
                context.Response.Headers.CacheControl="no-store";
                if (!_ready && !path.Value!.EndsWith("/status",StringComparison.Ordinal))
                    throw new ApiException(503,"host_not_ready","The host is initializing or stopping.");
            }
            else if (frontend)
            {
                context.Response.Headers["Content-Security-Policy"]="default-src 'none'; script-src 'self'; style-src 'self'; connect-src 'self'; img-src 'self' blob:; font-src 'self'; object-src 'none'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";
            }
            await next(context);
        });
        MapSurface("/api/v1",frontend:true);
        MapSurface("/messaging/v1",frontend:false);
        if (Directory.Exists(webRoot))
        {
            var files=new PhysicalFileProvider(Path.GetFullPath(webRoot));
            App.UseWhen(context=>context.Connection.LocalPort==options.FrontendPort && !context.Request.Path.StartsWithSegments("/api"),branch=>
            {
                branch.UseDefaultFiles(new DefaultFilesOptions { FileProvider=files });
                branch.UseStaticFiles(new StaticFileOptions { FileProvider=files, ServeUnknownFileTypes=false });
            });
            App.Lifetime.ApplicationStopped.Register(files.Dispose);
        }
    }

    public async Task StartAsync(CancellationToken ct=default)
    {
        await App.StartAsync(ct);
        try
        {
            if (_requireHttp3)
            {
                using var handler=new HttpClientHandler { UseProxy=false,
                    ServerCertificateCustomValidationCallback=(_,cert,_,errors)=>CertificateProvisioner.ValidatePeer(cert,errors,_certificate!.GetCertHashString(HashAlgorithmName.SHA256)) };
                using var client=new HttpClient(handler) { Timeout=TimeSpan.FromSeconds(10) };
                using var request=new HttpRequestMessage(HttpMethod.Get,_options.MessagingBaseUrl+"status")
                { Version=HttpVersion.Version30,VersionPolicy=HttpVersionPolicy.RequestVersionExact };
                request.Headers.Add(Wire.ClientHeader,Wire.ClientHeaderValue);
                using var response=await client.SendAsync(request,ct);
                var body=JsonNode.Parse(await response.Content.ReadAsStringAsync(ct))?.AsObject();
                if (response.Version!=HttpVersion.Version30 || body?["instanceId"]?.GetValue<string>()!=InstanceId.ToString("D")
                    || (response.StatusCode!=HttpStatusCode.ServiceUnavailable && !response.IsSuccessStatusCode))
                    throw new InvalidOperationException("quic_unavailable: exact HTTP/3 self-probe did not verify this host instance.");
                _probeResult="http3_exact_verified";
            }
            else _probeResult="not_run_test_fixture_http1_only";
            _ready=true;
            if (_requireHttp3)
            {
                var descriptor=new EndpointDescriptor(InstanceId,Environment.ProcessId,Guid.Parse(_store.StoreEpoch),
                    _options.MessagingBaseUrl,_options.FrontendBaseUrl,_certificate!.GetCertHashString(HashAlgorithmName.SHA256),1,true);
                CertificateProvisioner.AtomicWrite(_options.DescriptorPath,JsonSerializer.Serialize(descriptor,Wire.ResponseOptions));
            }
            App.Logger.LogInformation("Host ready. Instance {InstanceId}; transport {Transport}",InstanceId,_probeResult);
        }
        catch
        {
            _ready=false;
            await App.StopAsync(CancellationToken.None);
            throw;
        }
    }

    private void MapSurface(string prefix,bool frontend)
    {
        var group=App.MapGroup(prefix);
        group.MapGet("/status",async (HttpContext context) =>
        {
            var result=await _store.GetStatusAsync(context.RequestAborted);
            result["instanceId"]=InstanceId.ToString("D");
            result["version"]="1.0.0";
            result["ready"]=_ready;
            result["activeStreamCount"]=_streams.Count;
            result["limits"]=JsonSerializer.SerializeToNode(_options.Limits,Wire.ResponseOptions);
            result["startupProbe"]=new JsonObject { ["result"]=_probeResult,["httpVersion"]=_probeResult=="http3_exact_verified"?"3.0":null };
            result["capabilities"]=new JsonObject { ["contractVersion"]=1,["authentication"]=false,["privateMessages"]=false,
                ["createOnly"]=true,["maxThreadDepth"]=3,["messageTypes"]=new JsonArray("text","image"),["http3Required"]=_requireHttp3,
                ["profileAcknowledgements"]=true,["participantRosters"]=true,["liveAcknowledgementSummaries"]=true };
            result["endpoints"]=new JsonObject { ["messaging"]=_options.MessagingBaseUrl,["frontend"]=_options.FrontendBaseUrl };
            await JsonAsync(context,result,_ready?200:503);
        });
        group.MapPost("/requests",async (HttpContext context) =>
        {
            await WithCommandSlot(context,async () =>
            {
                var multipart=context.Request.ContentType?.StartsWith("multipart/form-data",StringComparison.OrdinalIgnoreCase)==true;
                if (multipart)
                {
                    if (!await _imageCommands.WaitAsync(0,context.RequestAborted))
                        throw new ApiException(429,"resource_limit_reached","Image command capacity is busy. Retry the same command later.");
                    try
                    {
                        var (request,bytes,declaredType)=await ReadMultipartAsync(context);
                        var typed=Wire.ReadRequest<CreateRequest>(request);
                        if (typed.DataObject is not CreateMessage { Content: ImageInput })
                            throw new ApiException(400,"invalid_contract","Multipart is only valid for one image message.");
                        var image=await _images.ValidateAsync(bytes,context.RequestAborted);
                        if (!string.Equals(image.ContentType,declaredType,StringComparison.OrdinalIgnoreCase))
                            throw new ApiException(415,"unsupported_image_type","Declared image content type does not match the decoded PNG/JPEG.");
                        var receipt=await _store.ExecuteAsync(request,image,context.RequestAborted);
                        await ReceiptAsync(context,receipt,prefix);
                    }
                    finally { _imageCommands.Release(); }
                }
                else
                {
                    var request=await ReadJsonAsync(context);
                    var typed=Wire.ReadRequest<CreateRequest>(request);
                    if (typed.DataObject is CreateMessage { Content: ImageInput })
                        throw new ApiException(400,"invalid_contract","Image messages require exactly one multipart image part.");
                    var receipt=await _store.ExecuteAsync(request,null,context.RequestAborted);
                    await ReceiptAsync(context,receipt,prefix);
                }
            });
        });
        group.MapGet("/requests/{id:guid}",async (HttpContext context,Guid id)=>
            await JsonAsync(context,await _store.GetReceiptAsync(id,context.RequestAborted)));
        MapQuery(group,"/users","users",false);
        MapQuery(group,"/users/{id:guid}","users",true);
        MapQuery(group,"/users/{id:guid}/mentions","mentions",true);
        MapQuery(group,"/channels","channels",false);
        MapQuery(group,"/channels/{id:guid}","channels",true);
        MapQuery(group,"/channels/{id:guid}/sessions","channelSessions",true);
        MapQuery(group,"/sessions/{id:guid}","sessions",true);
        MapQuery(group,"/sessions/{id:guid}/messages","sessionMessages",true);
        MapQuery(group,"/messages/{id:guid}","messages",true);
        MapQuery(group,"/messages/{id:guid}/thread","thread",true);
        MapQuery(group,"/messages/{id:guid}/acknowledgements","acknowledgements",true,standardPageOnly:true);
        MapQuery(group,"/channels/{id:guid}/participants","channelParticipants",true,standardPageOnly:true);
        MapQuery(group,"/sessions/{id:guid}/participants","sessionParticipants",true,standardPageOnly:true);
        MapQuery(group,"/subscriptions","subscriptions",false);
        group.MapGet("/subscriptions/{id:guid}",async (HttpContext context,Guid id)=>
            await JsonAsync(context,WithConnections(await _store.GetSubscriptionAsync(id,context.RequestAborted))));
        group.MapPost("/subscriptions",async (HttpContext context)=>await WithCommandSlot(context,async () =>
        {
            var request=await ReadJsonAsync(context);
            Wire.ReadRequest<CreateSubscriptionRequest>(request);
            var receipt=await _store.CreateSubscriptionAsync(request,context.RequestAborted);
            await ReceiptAsync(context,receipt,prefix);
        }));
        group.MapPost("/subscriptions/{id:guid}/close",async (HttpContext context,Guid id)=>await WithCommandSlot(context,async () =>
        {
            var request=await ReadJsonAsync(context);
            Wire.ReadRequest<CloseSubscriptionRequest>(request);
            var receipt=await _store.CloseSubscriptionAsync(id,request,context.RequestAborted);
            // Engine signals cancellation before completing the close call; repeated close also terminates any raced consumers.
            _streams.Close(id);
            await JsonAsync(context,receipt,200);
        }));
        group.MapGet("/subscriptions/{id:guid}/events",async (HttpContext context,Guid id)=>
        {
            CheckQuery(context,["after","limit"]);
            var after=CursorFrom(context);
            var result=await _store.ReadEventsAsync(id,after,QueryInt(context,"limit",100,_options.Limits.PageSize),context.RequestAborted);
            await JsonAsync(context,result);
        });
        group.MapGet("/subscriptions/{id:guid}/stream",StreamAsync);
        group.MapGet("/images/{id:guid}/content",async (HttpContext context,Guid id)=>
        {
            if (!await _imageResponses.WaitAsync(0,context.RequestAborted))
                throw new ApiException(429,"resource_limit_reached","Image response capacity is busy.");
            try
            {
                var image=await _store.GetImageAsync(id,context.RequestAborted);
                var tag="\""+image.Sha256+"\"";
                context.Response.Headers.ETag=tag;
                context.Response.Headers.CacheControl="private, max-age=31536000, immutable";
                if (context.Request.Headers.IfNoneMatch.Any(x=>x==tag)) { context.Response.StatusCode=304; return; }
                context.Response.ContentType=image.ContentType;
                context.Response.ContentLength=image.Bytes.Length;
                using var deadline=CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
                deadline.CancelAfter(TimeSpan.FromSeconds(_options.Limits.StreamWriteSeconds));
                await context.Response.Body.WriteAsync(image.Bytes,deadline.Token);
            }
            finally { _imageResponses.Release(); }
        });
        group.MapGet("/openapi.json",async (HttpContext context)=>await JsonAsync(context,OpenApiDocument.Create(_options)));
        if (frontend)
        {
            group.MapPost("/host/stop",async (HttpContext context)=>
            {
                _ready=false;
                context.Response.OnCompleted(() => { App.Lifetime.StopApplication(); return Task.CompletedTask; });
                await JsonAsync(context,new JsonObject { ["state"]="stopping" });
            });
        }
    }

    private void MapQuery(RouteGroupBuilder group,string route,string resource,bool hasId,bool standardPageOnly=false)
    {
        async Task Run(HttpContext context,Guid? id)
        {
            CheckQuery(context,standardPageOnly ? ["limit","pageToken","asOf"] : ["limit","pageToken","asOf","view","handlePrefix","ownerUserId"]);
            Guid? owner=null;
            var text=SingleQuery(context,"ownerUserId");
            if (text is not null)
            {
                if (!Guid.TryParseExact(text,"D",out var value)||value==Guid.Empty) throw new ApiException(400,"invalid_contract","Invalid ownerUserId.");
                owner=value;
            }
            var page=new PageQuery(QueryInt(context,"limit",50,_options.Limits.PageSize),SingleQuery(context,"pageToken"),
                SingleQuery(context,"asOf"),SingleQuery(context,"view"),SingleQuery(context,"handlePrefix"),owner);
            var result=await _store.QueryAsync(resource,id,page,context.RequestAborted);
            if (resource=="subscriptions" && result is JsonObject obj && obj["items"] is JsonArray items)
                foreach (var item in items.OfType<JsonObject>()) WithConnections(item);
            await JsonAsync(context,result);
        }
        if (hasId) group.MapGet(route,async (HttpContext ctx,Guid id)=>await Run(ctx,id));
        else group.MapGet(route,async (HttpContext ctx)=>await Run(ctx,null));
    }

    private JsonObject WithConnections(JsonObject descriptor)
    {
        descriptor["activeConnectionCount"]=_streams.CountFor(Guid.Parse(descriptor["id"]!.GetValue<string>()));
        return descriptor;
    }

    private async Task StreamAsync(HttpContext context,Guid id)
    {
        CheckQuery(context,["after"]);
        var cursor=CursorFrom(context);
        var batch=await _store.ReadEventsAsync(id,cursor,Math.Min(100,_options.Limits.PageSize),context.RequestAborted);
        if (AfterInitialStreamReadForTest is { } afterRead) await afterRead(context.RequestAborted);
        using var lease=await _streams.RegisterAsync(id,async () =>
        {
            var descriptor=await _store.GetSubscriptionAsync(id,context.RequestAborted);
            if (descriptor["state"]!.GetValue<string>()!="active") throw new ApiException(410,"subscription_closed","This saved subscription is closed.");
        },context.RequestAborted);
        using var cancellation=CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted,lease.Token,App.Lifetime.ApplicationStopping);
        var ct=cancellation.Token;
        if (lease.Token.IsCancellationRequested) throw new ApiException(410,"subscription_closed","This subscription closed during attachment.");
        ct.ThrowIfCancellationRequested();
        context.Response.ContentType="text/event-stream";
        context.Response.Headers.CacheControl="no-cache, no-transform";
        context.Response.Headers["X-Accel-Buffering"]="no";
        await context.Response.StartAsync(ct);
        var heartbeat=Stopwatch.StartNew();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var wake=_streams.CaptureWake();
                var items=batch["items"]!.AsArray();
                foreach (var node in items)
                {
                    var evt=node!.AsObject();
                    var payload=evt.ToJsonString(Wire.ResponseOptions);
                    if (Encoding.UTF8.GetByteCount(payload)>_options.Limits.EventFrameBytes)
                        throw new ApiException(500,"internal_error","A durable event exceeds the configured frame budget.");
                    await FrameAsync(context,$"id: {evt["cursor"]!.GetValue<string>()}\nevent: {evt["eventType"]!.GetValue<string>()}\ndata: {payload}\n\n",ct);
                    heartbeat.Restart();
                }
                var next=batch["nextCursor"]!.GetValue<string>();
                if (next!=cursor)
                {
                    var control=new JsonObject { ["contractVersion"]=1,["cursor"]=next }.ToJsonString();
                    await FrameAsync(context,$"id: {next}\nevent: stream.checkpoint\ndata: {control}\n\n",ct);
                    cursor=next;
                    heartbeat.Restart();
                }
                if (!batch["hasMore"]!.GetValue<bool>())
                {
                    if (heartbeat.Elapsed>=TimeSpan.FromSeconds(_options.Limits.HeartbeatSeconds))
                    { await FrameAsync(context,": keepalive\n\n",ct); heartbeat.Restart(); }
                    await Task.WhenAny(wake,Task.Delay(TimeSpan.FromSeconds(2),ct));
                    ct.ThrowIfCancellationRequested();
                }
                batch=await _store.ReadEventsAsync(id,cursor,Math.Min(100,_options.Limits.PageSize),ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (ApiException e) when (e.Status is 409 or 410 or 503)
        {
            await TryControlAsync(context,e.Code);
        }
        catch (IOException) { context.Abort(); }
        finally
        {
            if (!context.RequestAborted.IsCancellationRequested)
                await TryControlAsync(context,_stopping?"server.stopping":"stream.closed");
        }
    }

    private async Task FrameAsync(HttpContext context,string frame,CancellationToken ct)
    {
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(_options.Limits.StreamWriteSeconds));
        try
        {
            await context.Response.WriteAsync(frame,Encoding.UTF8,deadline.Token);
            await context.Response.Body.FlushAsync(deadline.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { context.Abort(); throw new IOException("The bounded stream write expired."); }
    }

    private static async Task TryControlAsync(HttpContext context,string type)
    {
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        timeout.CancelAfter(TimeSpan.FromSeconds(1));
        try { await context.Response.WriteAsync($"event: {type}\ndata: {{\"contractVersion\":1}}\n\n",timeout.Token); await context.Response.Body.FlushAsync(timeout.Token); }
        catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException) { context.Abort(); }
    }

    private async Task WithCommandSlot(HttpContext context,Func<Task> action)
    {
        context.Items["commandTimer"]=Stopwatch.StartNew();
        if (!await _commands.WaitAsync(0,context.RequestAborted)) throw new ApiException(429,"resource_limit_reached","Command capacity is busy.");
        try { await action(); }
        finally { _commands.Release(); }
    }

    private static Task ReceiptAsync(HttpContext context,JsonObject receipt,string prefix)
    {
        var replayed=receipt["replayed"]?.GetValue<bool>()==true;
        context.Response.Headers.Location=prefix+"/requests/"+receipt["requestId"]!.GetValue<string>();
        return JsonAsync(context,receipt,replayed?200:201);
    }

    private async Task<JsonElement> ReadJsonAsync(HttpContext context)
    {
        if (!MediaTypeHeaderValue.TryParse(context.Request.ContentType,out var type)
            || !string.Equals(type.MediaType.Value,"application/json",StringComparison.OrdinalIgnoreCase)
            || (type.Charset.HasValue && !string.Equals(type.Charset.Value,"utf-8",StringComparison.OrdinalIgnoreCase)))
            throw new ApiException(415,"unsupported_content_type","Use application/json with UTF-8.");
        var bytes=await ReadBoundedAsync(context.Request.Body,_options.Limits.JsonBytes,"request_too_large",context.RequestAborted);
        return CaptureRequestId(context,ParseJson(bytes));
    }

    private async Task<(JsonElement Request,byte[] Image,string ContentType)> ReadMultipartAsync(HttpContext context)
    {
        if (!MediaTypeHeaderValue.TryParse(context.Request.ContentType,out var type)) throw new ApiException(400,"invalid_contract","Invalid multipart content type.");
        var boundary=HeaderUtilities.RemoveQuotes(type.Boundary).Value;
        if (string.IsNullOrWhiteSpace(boundary)||boundary.Length>128) throw new ApiException(400,"invalid_contract","A bounded multipart boundary is required.");
        var reader=new MultipartReader(boundary,context.Request.Body) { HeadersCountLimit=8,HeadersLengthLimit=4096,BodyLengthLimit=_options.Limits.MultipartBytes };
        byte[]? json=null,image=null;
        string? contentType=null;
        try
        {
            MultipartSection? part;
            var count=0;
            while ((part=await reader.ReadNextSectionAsync(context.RequestAborted)) is not null)
            {
                if (++count>2) throw new ApiException(400,"invalid_contract","Exactly the request and image parts are accepted.");
                if (!ContentDispositionHeaderValue.TryParse(part.ContentDisposition,out var disposition)
                    || !string.Equals(disposition.DispositionType.Value,"form-data",StringComparison.OrdinalIgnoreCase))
                    throw new ApiException(400,"invalid_contract","Invalid multipart disposition.");
                var name=HeaderUtilities.RemoveQuotes(disposition.Name).Value;
                if (name=="request" && json is null)
                {
                    if (!MediaTypeHeaderValue.TryParse(part.ContentType,out var jsonType)
                        || !string.Equals(jsonType.MediaType.Value,"application/json",StringComparison.OrdinalIgnoreCase))
                        throw new ApiException(415,"unsupported_content_type","The request part must be application/json.");
                    json=await ReadBoundedAsync(part.Body,_options.Limits.JsonBytes,"request_too_large",context.RequestAborted);
                }
                else if (name=="image" && image is null)
                {
                    if (!MediaTypeHeaderValue.TryParse(part.ContentType,out var imageType)
                        || imageType.MediaType.Value?.ToLowerInvariant() is not ("image/png" or "image/jpeg"))
                        throw new ApiException(415,"unsupported_image_type","The image part must declare image/png or image/jpeg.");
                    contentType=imageType.MediaType.Value!.ToLowerInvariant();
                    image=await ReadBoundedAsync(part.Body,_options.Limits.ImageBytes,"image_too_large",context.RequestAborted);
                }
                else throw new ApiException(400,"invalid_contract","Duplicate or unknown multipart part.");
            }
        }
        catch (InvalidDataException) { throw new ApiException(400,"invalid_contract","Malformed multipart input."); }
        if (json is null||image is null||contentType is null) throw new ApiException(400,"invalid_contract","Exactly one request part and one image part are required.");
        return (CaptureRequestId(context,ParseJson(json)),image,contentType);
    }

    private static JsonElement CaptureRequestId(HttpContext context,JsonElement request)
    {
        if (request.TryGetProperty("requestId",out var id) && id.ValueKind==JsonValueKind.String && id.TryGetGuid(out var value))
            context.Items["requestId"]=value.ToString("D");
        return request;
    }

    internal static async Task<byte[]> ReadBoundedAsync(Stream stream,int maximum,string code,CancellationToken ct)
    {
        using var output=new MemoryStream();
        var buffer=new byte[8192];
        while (true)
        {
            var read=await stream.ReadAsync(buffer,ct);
            if (read==0) break;
            if (output.Length+read>maximum) throw new ApiException(413,code,"The configured byte limit was exceeded.");
            output.Write(buffer,0,read);
        }
        return output.ToArray();
    }

    private static JsonElement ParseJson(byte[] bytes)
    {
        using var document=JsonDocument.Parse(bytes,new JsonDocumentOptions { MaxDepth=32 });
        if (document.RootElement.ValueKind!=JsonValueKind.Object) throw new ApiException(400,"invalid_contract","A JSON object is required.");
        void Check(JsonElement node)
        {
            if (node.ValueKind==JsonValueKind.Object)
            {
                var names=new HashSet<string>(StringComparer.Ordinal);
                foreach (var p in node.EnumerateObject()) { if (!names.Add(p.Name)) throw new ApiException(400,"invalid_contract","Duplicate JSON property."); Check(p.Value); }
            }
            else if (node.ValueKind==JsonValueKind.Array) foreach (var item in node.EnumerateArray()) Check(item);
        }
        Check(document.RootElement);
        return document.RootElement.Clone();
    }

    private static string? SingleQuery(HttpContext context,string name)
    {
        var values=context.Request.Query[name];
        if (values.Count>1) throw new ApiException(400,"invalid_contract",$"Query parameter {name} must occur once.");
        if (values.Count==0) return null;
        if (string.IsNullOrEmpty(values[0])) throw new ApiException(400,"invalid_contract",$"Query parameter {name} cannot be empty.");
        return values[0];
    }
    private static int QueryInt(HttpContext context,string name,int fallback,int maximum)
    {
        var text=SingleQuery(context,name);
        if (text is null) return Math.Min(fallback,maximum);
        if (!int.TryParse(text,NumberStyles.None,CultureInfo.InvariantCulture,out var result)||result<1||result>maximum)
            throw new ApiException(400,"invalid_contract",$"{name} must be between 1 and {maximum}.");
        return result;
    }
    private static void CheckQuery(HttpContext context,string[] allowed)
    {
        foreach (var name in context.Request.Query.Keys)
            if (!allowed.Contains(name,StringComparer.Ordinal)) throw new ApiException(400,"invalid_contract","Unknown query parameter: "+name);
    }
    private static string? CursorFrom(HttpContext context)
    {
        var after=SingleQuery(context,"after");
        var ids=context.Request.Headers["Last-Event-ID"];
        if (ids.Count>1) throw new ApiException(400,"conflicting_cursors","Only one Last-Event-ID is accepted.");
        var header=ids.Count==1?ids[0]:null;
        if (after is not null && header is not null && after!=header) throw new ApiException(400,"conflicting_cursors","after and Last-Event-ID disagree.");
        return after??header;
    }
    private static async Task JsonAsync(HttpContext context,JsonNode? data,int status=200)
    {
        context.Response.StatusCode=status;
        context.Response.ContentType="application/json; charset=utf-8";
        await context.Response.WriteAsync(data?.ToJsonString(Wire.ResponseOptions)??"null",context.RequestAborted);
    }
    private static async Task ProblemAsync(HttpContext context,ApiException error)
    {
        if (context.Response.HasStarted) { context.Abort(); return; }
        context.Response.Clear();
        context.Response.StatusCode=error.Status;
        context.Response.ContentType="application/problem+json";
        context.Response.Headers.XContentTypeOptions="nosniff";
        context.Response.Headers.CacheControl="no-store";
        if (error.Status is 429 or 503) context.Response.Headers.RetryAfter="1";
        var body=new JsonObject { ["type"]="urn:local-messaging:problem:"+error.Code,["title"]=error.Code.Replace('_',' '),
            ["status"]=error.Status,["detail"]=error.Message,["code"]=error.Code,["traceId"]=context.TraceIdentifier,
            ["errors"]=JsonSerializer.SerializeToNode(error.Errors,Wire.ResponseOptions) };
        if (context.Items["requestId"] is string requestId) body["requestId"]=requestId;
        await context.Response.WriteAsync(body.ToJsonString(Wire.ResponseOptions),context.RequestAborted);
    }
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed=true; _stopping=true; _ready=false; _streams.Stop();
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(_options.Limits.DrainSeconds));
        try { await App.StopAsync(timeout.Token); } catch (OperationCanceledException) { }
        await App.DisposeAsync();
        _store.Committed-=_streams.Pulse;
        _store.SubscriptionClosed-=_streams.Close;
        await _store.DisposeAsync();
        _streams.Dispose();
        _imageResponses.Dispose(); _imageCommands.Dispose(); _commands.Dispose();
        if (_requireHttp3 && File.Exists(_options.DescriptorPath))
        {
            try
            {
                var descriptor=JsonNode.Parse(await File.ReadAllTextAsync(_options.DescriptorPath));
                if (descriptor?["instanceId"]?.GetValue<string>()==InstanceId.ToString("D")) File.Delete(_options.DescriptorPath);
            }
            catch (Exception e) when (e is IOException or JsonException) { }
        }
    }
}
