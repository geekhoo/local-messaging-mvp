using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization.Metadata;
using LocalMessaging.Contracts;

namespace LocalMessaging.Host;

internal static class OpenApiDocument
{
    public static JsonObject Create(HostOptions options)
    {
        var serializer=new JsonSerializerOptions(Wire.RequestOptions) { TypeInfoResolver=new DefaultJsonTypeInfoResolver() };
        serializer.MakeReadOnly();
        JsonNode Schema(Type type)=>serializer.GetJsonSchemaAsNode(type);
        static JsonObject Ref(string name)=>new() { ["$ref"]="#/components/schemas/"+name };
        static JsonObject String(string? format=null)=>format is null?new() {["type"]="string"}:new() {["type"]="string",["format"]=format};
        var createRequest=Schema(typeof(CreateRequest));
        createRequest["if"]=new JsonObject{["properties"]=new JsonObject{["dataObject"]=new JsonObject{
            ["properties"]=new JsonObject{["type"]=new JsonObject{["enum"]=new JsonArray("acknowledgement","participation")}},
            ["required"]=new JsonArray("type")}}};
        createRequest["then"]=new JsonObject{["required"]=new JsonArray("callerUserId"),["properties"]=new JsonObject{
            ["callerUserId"]=String("uuid"),["subscribe"]=new JsonObject{["type"]="null"},
            ["dataObject"]=new JsonObject{
                ["if"]=new JsonObject{["properties"]=new JsonObject{["type"]=new JsonObject{["const"]="participation"}}},
                ["then"]=new JsonObject{["oneOf"]=new JsonArray(ParticipationScope("channelId","sessionId"),ParticipationScope("sessionId","channelId"))}}}};
        static JsonObject ParticipationScope(string selected,string other)=>new(){["required"]=new JsonArray(selected),
            ["properties"]=new JsonObject{[selected]=String("uuid"),[other]=new JsonObject{["type"]="null"}}};
        var acknowledgementPage=Schema(typeof(ObjectPage<ProfileAcknowledgement>));
        acknowledgementPage["properties"]!["items"]=new JsonObject{["type"]="array",["items"]=Ref("AcknowledgementItem")};
        var participation=Schema(typeof(ParticipationChange));
        participation["properties"]!["id"]=new JsonObject{["type"]=new JsonArray("string","null"),["format"]="uuid"};
        participation["properties"]!["changedUtcMs"]=new JsonObject{["type"]=new JsonArray("integer","null"),["format"]="int64"};
        var schemas=new JsonObject
        {
            ["CreateRequest"]=createRequest,
            ["CreateSubscriptionRequest"]=Schema(typeof(CreateSubscriptionRequest)),
            ["CloseSubscriptionRequest"]=Schema(typeof(CloseSubscriptionRequest)),
            ["User"]=Schema(typeof(UserDto)),["Channel"]=Schema(typeof(ChannelDto)),
            ["Session"]=Schema(typeof(SessionDto)),["Message"]=Schema(typeof(MessageDto)),
            ["ImageDescriptor"]=Schema(typeof(ImageDescriptor)),["Subscription"]=Schema(typeof(SubscriptionDto)),
            ["UserPage"]=Schema(typeof(ObjectPage<UserDto>)),["ChannelPage"]=Schema(typeof(ObjectPage<ChannelDto>)),
            ["SessionPage"]=Schema(typeof(ObjectPage<SessionDto>)),["MessagePage"]=Schema(typeof(ObjectPage<MessageDto>)),
            ["ThreadPage"]=Schema(typeof(ThreadPage)),
            ["PublicProfile"]=Schema(typeof(PublicProfile)),
            ["ProfileAcknowledgement"]=Schema(typeof(ProfileAcknowledgement)),
            ["ParticipationChange"]=participation,
            ["AcknowledgementItem"]=new JsonObject{["type"]="object",["required"]=new JsonArray("profile","acknowledgedUtcMs"),
                ["properties"]=new JsonObject{["profile"]=Ref("PublicProfile"),["acknowledgedUtcMs"]=new JsonObject{["type"]="integer",["format"]="int64"}}},
            ["AcknowledgementPage"]=acknowledgementPage,
            ["ParticipantPage"]=Schema(typeof(ObjectPage<Participant>)),
            ["Problem"]=new JsonObject { ["type"]="object",["required"]=new JsonArray("type","title","status","detail","code","traceId"),
                ["properties"]=new JsonObject { ["type"]=String(),["title"]=String(),["status"]=new JsonObject{["type"]="integer"},
                    ["detail"]=String(),["code"]=String(),["requestId"]=String("uuid"),["traceId"]=String(),
                    ["errors"]=new JsonObject{["type"]=new JsonArray("object","null"),["additionalProperties"]=new JsonObject{["type"]="array",["items"]=String()}} } },
            ["Event"]=new JsonObject { ["type"]="object",["required"]=new JsonArray("contractVersion","eventType","sequence","cursor","requestId","occurredAtUtc","actorUserId","dataObject"),
                ["properties"]=new JsonObject {
                    ["contractVersion"]=new JsonObject{["const"]=1},["eventType"]=new JsonObject{
                        ["enum"]=new JsonArray("user.created","channel.created","session.created","message.created","acknowledgement.created","participation.changed"),
                        ["description"]="Acknowledgement and participation events are delivered to matching channel or session subscribers, not to catalog, all-messages or target-only subscribers."},
                    ["sequence"]=new JsonObject{["type"]="string",["pattern"]="^[0-9]+$"},["cursor"]=String(),
                    ["requestId"]=String("uuid"),["occurredAtUtc"]=String("date-time"),["actorUserId"]=String("uuid"),
                    ["channelId"]=new JsonObject{["type"]=new JsonArray("string","null"),["format"]="uuid"},
                    ["sessionId"]=new JsonObject{["type"]=new JsonArray("string","null"),["format"]="uuid"},
                    ["dataObject"]=new JsonObject{["oneOf"]=new JsonArray(Ref("User"),Ref("Channel"),Ref("Session"),Ref("Message"),Ref("ProfileAcknowledgement"),Ref("ParticipationChange"))} } },
            ["EventPage"]=new JsonObject { ["type"]="object",["required"]=new JsonArray("contractVersion","subscriptionId","items","nextCursor","headCursor","hasMore"),
                ["properties"]=new JsonObject{["contractVersion"]=new JsonObject{["const"]=1},["subscriptionId"]=String("uuid"),
                    ["items"]=new JsonObject{["type"]="array",["items"]=Ref("Event")},["nextCursor"]=String(),["headCursor"]=String(),["hasMore"]=new JsonObject{["type"]="boolean"}} },
            ["SubscriptionPage"]=new JsonObject{["type"]="object",["properties"]=new JsonObject{
                ["items"]=new JsonObject{["type"]="array",["items"]=Ref("Subscription")},["nextPageToken"]=new JsonObject{["type"]=new JsonArray("string","null")}}},
            ["Status"]=new JsonObject{["type"]="object",["required"]=new JsonArray("contractVersion","instanceId","ready","storeEpoch","headCursor","limits","startupProbe"),
                ["properties"]=new JsonObject{["contractVersion"]=new JsonObject{["const"]=1},["instanceId"]=String("uuid"),
                    ["storeEpoch"]=String("uuid"),["headCursor"]=String(),["ready"]=new JsonObject{["type"]="boolean"},
                    ["version"]=String(),["activeStreamCount"]=new JsonObject{["type"]="integer"},["limits"]=Schema(typeof(ProductLimits)),
                    ["startupProbe"]=new JsonObject{["type"]="object"},["capabilities"]=new JsonObject{["type"]="object",["properties"]=new JsonObject{
                        ["profileAcknowledgements"]=new JsonObject{["type"]="boolean",["description"]="Supports profile acknowledgements; missing means unsupported."},["liveAcknowledgementSummaries"]=new JsonObject{["type"]="boolean",["description"]="Supports the allAcknowledgements filter branch replaying acknowledgement.created events and message acknowledgement summaries."},
                        ["participantRosters"]=new JsonObject{["type"]="boolean",["description"]="Supports public participant rosters; missing means unsupported."}}},
                    ["endpoints"]=new JsonObject{["type"]="object"}}}
        };
        JsonObject Receipt(string operation,JsonObject fields)
        {
            var properties=new JsonObject{["contractVersion"]=new JsonObject{["const"]=1},["requestId"]=String("uuid"),["operation"]=new JsonObject{["const"]=operation},
                ["state"]=new JsonObject{["const"]="committed"},["replayed"]=new JsonObject{["type"]="boolean"},
                ["storeEpoch"]=String("uuid"),["links"]=new JsonObject{["type"]="object",["properties"]=new JsonObject{["receipt"]=String()}}};
            foreach(var field in fields) properties[field.Key]=field.Value?.DeepClone();
            return new JsonObject{["type"]="object",["required"]=new JsonArray("contractVersion","requestId","operation","state","storeEpoch","links"),["properties"]=properties};
        }
        schemas["CreateReceipt"]=Receipt("createObject",new JsonObject{
            ["dataObjects"]=new JsonObject{["type"]="object",["properties"]=new JsonObject{["user"]=Ref("User"),["channel"]=Ref("Channel"),["session"]=Ref("Session"),["message"]=Ref("Message"),
                ["acknowledgement"]=Ref("ProfileAcknowledgement"),["participation"]=Ref("ParticipationChange")}},
            ["created"]=new JsonObject{["type"]="array",["description"]="Empty for committed no-op profile actions.",
                ["items"]=new JsonObject{["type"]="object",["properties"]=new JsonObject{["type"]=String(),["id"]=String("uuid")}}},
            ["eventRange"]=new JsonObject{["type"]=new JsonArray("object","null"),["properties"]=new JsonObject{["firstSequence"]=String(),["lastSequence"]=String()}},
            ["afterRequestCursor"]=String(),["subscription"]=new JsonObject{["anyOf"]=new JsonArray(Ref("Subscription"),new JsonObject{["type"]="null"})}});
        schemas["SubscribeReceipt"]=Receipt("createSubscription",new JsonObject{["subscription"]=Ref("Subscription")});
        schemas["CloseReceipt"]=Receipt("closeSubscription",new JsonObject{["subscriptionId"]=String("uuid"),["subscriptionState"]=new JsonObject{["const"]="closed"},["closedAtUtc"]=String("date-time")});
        schemas["Receipt"]=new JsonObject{["oneOf"]=new JsonArray(Ref("CreateReceipt"),Ref("SubscribeReceipt"),Ref("CloseReceipt"))};
        var paths=new JsonObject();
        void Add(string path,string method,string title,string response,string? request=null,string[]? query=null,bool created=false)
        {
            var parameters=new JsonArray(new JsonObject{["name"]=Wire.ClientHeader,["in"]="header",["required"]=true,
                ["description"]="Required local-client marker; this is not authentication.",["schema"]=new JsonObject{["type"]="string",["const"]="1"}});
            if(path.Contains("{id}",StringComparison.Ordinal)) parameters.Add(new JsonObject{["name"]="id",["in"]="path",["required"]=true,["schema"]=String("uuid")});
            if(query is not null) foreach(var q in query) parameters.Add(new JsonObject{["name"]=q,["in"]="query",["schema"]=q=="limit"?new JsonObject{["type"]="integer",["minimum"]=1,["maximum"]=options.Limits.PageSize}:String()});
            if(path.EndsWith("/stream",StringComparison.Ordinal)||path.EndsWith("/events",StringComparison.Ordinal))
                parameters.Add(new JsonObject{["name"]="Last-Event-ID",["in"]="header",["schema"]=String(),["description"]="Must equal after when both are supplied."});
            var responses=new JsonObject();
            JsonObject ContentResponse(string description)=>new(){["description"]=description,["content"]=new JsonObject{["application/json"]=new JsonObject{["schema"]=Ref(response)}}};
            responses["200"]=ContentResponse("Successful query or idempotent committed retry.");
            if(created) responses["201"]=ContentResponse("Committed creation.");
            foreach(var status in new[]{"400","403","404","409","410","413","415","422","429","503","500"})
                responses[status]=new JsonObject{["description"]="Problem Details. See code for recovery; 429/503 may include Retry-After.",["content"]=new JsonObject{["application/problem+json"]=new JsonObject{["schema"]=Ref("Problem")}}};
            var operation=new JsonObject{["summary"]=title,["parameters"]=parameters,["responses"]=responses};
            if(request is not null)
            {
                var content=new JsonObject{["application/json"]=new JsonObject{["schema"]=Ref(request)}};
                if(path=="/requests")
                    content["multipart/form-data"]=new JsonObject{["schema"]=new JsonObject{["type"]="object",["additionalProperties"]=false,["required"]=new JsonArray("request","image"),
                        ["properties"]=new JsonObject{["request"]=Ref("CreateRequest"),["image"]=new JsonObject{["type"]="string",["format"]="binary"}}},
                        ["encoding"]=new JsonObject{["request"]=new JsonObject{["contentType"]="application/json"},["image"]=new JsonObject{["contentType"]="image/png, image/jpeg"}}};
                operation["requestBody"]=new JsonObject{["required"]=true,["content"]=content};
            }
            if(!paths.ContainsKey(path)) paths[path]=new JsonObject();
            paths[path]![method]=operation;
        }
        string[] page=["limit","pageToken","asOf"];
        Add("/status","get","Read readiness, effective limits and event head.","Status");
        Add("/requests","post","Submit an immutable create command or public profile action.","CreateReceipt","CreateRequest",created:true);
        paths["/requests"]!["post"]!["description"]="Profile actions require callerUserId and use JSON only, without a coupled subscription. Participation selects exactly one of channelId or sessionId. The participation result reports direct scope membership in joined and effective parent channel membership in channelJoined. Multipart is for image messages only.";
        Add("/requests/{id}","get","Read the historical committed receipt.","Receipt");
        Add("/users","get","List profiles in a stable snapshot.","UserPage",query:[..page,"handlePrefix"]);
        Add("/users/{id}","get","Read a profile.","User");
        Add("/users/{id}/mentions","get","Read public messages targeting a profile.","MessagePage",query:page);
        Add("/channels","get","Discover channels.","ChannelPage",query:page);
        Add("/channels/{id}","get","Read a channel.","Channel");
        Add("/channels/{id}/sessions","get","Discover a channel's sessions.","SessionPage",query:page);
        Add("/sessions/{id}","get","Read a session.","Session");
        Add("/sessions/{id}/messages","get","Read roots or flat session history.","MessagePage",query:[..page,"view"]);
        Add("/messages/{id}","get","Read a message.","Message");
        Add("/messages/{id}/thread","get","Read root, paged replies and required ancestors.","ThreadPage",query:page);
        Add("/messages/{id}/acknowledgements","get","List public profile acknowledgements for a message.","AcknowledgementPage",query:page);
        paths["/messages/{id}/acknowledgements"]!["get"]!["description"]="Visible to anyone who can view the message. An acknowledgement is a self-asserted profile action, not proof that a verified person read the message.";
        Add("/channels/{id}/participants","get","Read a snapshot of the public channel roster.","ParticipantPage",query:page);
        paths["/channels/{id}/participants"]!["get"]!["description"]="Includes profiles joined directly or through any session in the channel. Leaving the channel removes only direct membership; leaving a session does not remove a direct channel join. This is not live presence or access control.";
        Add("/sessions/{id}/participants","get","Read a snapshot of the public session roster.","ParticipantPage",query:page);
        paths["/sessions/{id}/participants"]!["get"]!["description"]="Includes profiles joined to this session. Session participation also counts toward the parent channel roster. This is not live presence or access control.";
        Add("/subscriptions","get","List saved subscriptions with live lifecycle state.","SubscriptionPage",query:["limit","pageToken","ownerUserId"]);
        Add("/subscriptions","post","Create a saved query with an independent consumer cursor.","SubscribeReceipt","CreateSubscriptionRequest",created:true);
        Add("/subscriptions/{id}","get","Read saved definition and runtime connection count.","Subscription");
        Add("/subscriptions/{id}/close","post","Close saved subscription and cancel streams.","CloseReceipt","CloseSubscriptionRequest");
        Add("/subscriptions/{id}/events","get","Read bounded retained events and a safe examined cursor.","EventPage",query:["after","limit"]);
        Add("/subscriptions/{id}/stream","get","Replay and stream durable events as SSE.","EventPage",query:["after"]);
        paths["/subscriptions/{id}/stream"]!["get"]!["responses"]!["200"]=new JsonObject{["description"]="UTF-8 SSE; ids advance only after applied durable events/checkpoints. Keepalives are comments.",
            ["content"]=new JsonObject{["text/event-stream"]=new JsonObject{["schema"]=String()}}};
        Add("/images/{id}/content","get","Retrieve validated original image bytes.","ImageDescriptor");
        paths["/images/{id}/content"]!["get"]!["responses"]!["200"]=new JsonObject{["description"]="Validated bytes, digest ETag, private immutable cache and nosniff.",
            ["content"]=new JsonObject{["image/png"]=new JsonObject{["schema"]=new JsonObject{["type"]="string",["format"]="binary"}},["image/jpeg"]=new JsonObject{["schema"]=new JsonObject{["type"]="string",["format"]="binary"}}}};
        paths["/images/{id}/content"]!["get"]!["responses"]!["304"]=new JsonObject{["description"]="Digest matches If-None-Match."};
        Add("/host/stop","post","Operator stop; frontend listener only.","Status");
        paths["/host/stop"]!["post"]!["servers"]=new JsonArray(new JsonObject{["url"]=options.FrontendBaseUrl.TrimEnd('/')});
        paths["/host/stop"]!["post"]!["responses"]!["200"]=new JsonObject{["description"]="Response completes before cancellation is signalled.",["content"]=new JsonObject{["application/json"]=new JsonObject{["schema"]=new JsonObject{["type"]="object",["properties"]=new JsonObject{["state"]=new JsonObject{["const"]="stopping"}}}}}};
        Add("/openapi.json","get","Read this local OpenAPI contract.","Status");
        paths["/openapi.json"]!["get"]!["responses"]!["200"]=new JsonObject{["description"]="OpenAPI 3.1 document.",["content"]=new JsonObject{["application/json"]=new JsonObject{["schema"]=new JsonObject{["type"]="object"}}}};
        return new JsonObject{["openapi"]="3.1.0",["info"]=new JsonObject{["title"]="Local Messaging",["version"]="1.0.0",
            ["description"]="Local create-only messaging. Logical users are self-asserted, history/targets are public, and ownership adds no privilege."},
            ["servers"]=new JsonArray(new JsonObject{["url"]=options.FrontendBaseUrl.TrimEnd('/')},new JsonObject{["url"]=options.MessagingBaseUrl.TrimEnd('/')}),
            ["paths"]=paths,["components"]=new JsonObject{["schemas"]=schemas}};
    }
}
