using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

// Separately published, self-contained qualification tool. This is never shipped in the product host.
internal static class Program
{
    private static readonly HashSet<string> ObservedCases=["A02","A07","A33","A37","A39","A40","A41","A42","A43","A47","A48","A50"];
    public static async Task<int> Main(string[] args)
    {
        if(args.Length==1&&args[0]=="--help")
        {
            Console.WriteLine("LocalMessaging.Verify --scope acceptance|task|demo|checkpoint|milestone --id A03|All|T007|CP03|M02 --product <absolute product folder> --evidence <absolute folder> [--observations <JSON>]");
            Console.WriteLine("Exit: 0 executable evidence complete, 1 failed, 2 invalid invocation, 3 blocked/incomplete. Milestone review remains a separate recorded decision.");
            return 0;
        }
        var options=new Dictionary<string,string>(StringComparer.Ordinal);
        for(var i=0;i<args.Length;i++)
        {
            if(i+1>=args.Length||!new[]{"--scope","--id","--product","--evidence","--observations"}.Contains(args[i])||!options.TryAdd(args[i],args[++i]))
            { Console.Error.WriteLine("Unknown, duplicate or incomplete option. Use --help.");return 2; }
        }
        if(!options.TryGetValue("--scope",out var scope)||!options.TryGetValue("--id",out var id)
            ||!options.TryGetValue("--product",out var product)||!options.TryGetValue("--evidence",out var evidence)
            ||!Path.IsPathFullyQualified(product)||!Path.IsPathFullyQualified(evidence))
        { Console.Error.WriteLine("scope, id and absolute product/evidence paths are required.");return 2; }
        string[] cases; (string Id,string Text)[] criteria;
        try { cases=SelectCases(scope,id);criteria=SelectTaskCriteria(scope,id); }
        catch(ArgumentException e) { Console.Error.WriteLine(e.Message);return 2; }
        var verifyRoot=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,".."));
        if(IsWithin(product,evidence)||IsWithin(verifyRoot,evidence))
        { Console.Error.WriteLine("Evidence must be outside both product and verification-tool inventories.");return 2; }
        Directory.CreateDirectory(evidence);
        var steps=new List<object>();var result="blocked";string? candidate=null;string? manifestHash=null;
        var caseResults=new List<object>();var criterionResults=new List<object>();var blockers=new List<string>();
        async Task<int> Finish(int code)
        {
            await File.WriteAllTextAsync(Path.Combine(evidence,"qualification.json"),JsonSerializer.Serialize(new {
                schemaVersion=1,scope,id,result,atUtc=DateTime.UtcNow,product=Path.GetFullPath(product),candidateCommit=candidate,
                manifestSha256=manifestHash,os=System.Runtime.InteropServices.RuntimeInformation.OSDescription,
                architecture=System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
                runtime=Environment.Version.ToString(),command=Environment.GetCommandLineArgs(),steps,acceptanceCases=caseResults,taskCriteria=criterionResults,blockers,
                gateDecision="Independent task/checkpoint/milestone review is separate; executable evidence never auto-approves it."
            },new JsonSerializerOptions{WriteIndented=true}));
            Console.WriteLine(JsonSerializer.Serialize(new{scope,id,result,exitCode=code,evidence=Path.Combine(evidence,"qualification.json")}));return code;
        }
        if(!OperatingSystem.IsWindowsVersionAtLeast(10,0,22000)||System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture!=System.Runtime.InteropServices.Architecture.X64)
        { blockers.Add("Actual supported Windows 11 x64 execution is required. No platform substitution qualifies these scopes.");return await Finish(3); }
        try
        {
            var manifestFile=Path.Combine(product,"manifest.json");
            var manifestBytes=await File.ReadAllBytesAsync(manifestFile);
            manifestHash=Convert.ToHexStringLower(SHA256.HashData(manifestBytes));
            candidate=JsonNode.Parse(manifestBytes)!["candidateCommit"]!.GetValue<string>();
            await ValidateVerifierManifestAsync(verifyRoot,candidate);
            // This performs strict inventory/hash verification, launches the packaged Host, and tests actual HTTP/3/WIC/storage/lifecycle.
            var packageResult=await ScenarioProgram.Main(["--suite","windows","--product",product,"--evidence",Path.Combine(evidence,"published-host.json")]);
            steps.Add(new{name="published-host",exitCode=packageResult,result=packageResult==0?"passed":packageResult==3?"blocked":"failed"});
            if(packageResult!=0){result=packageResult==3?"blocked":"failed";return await Finish(packageResult==3?3:1);}
            var checks=new (string Name,string Folder,string Exe,string[] Args)[]
            {
                ("store","store","LocalMessaging.StoreTests.exe",["--output",Path.Combine(evidence,"store.json")]),
                ("client","client","LocalMessaging.ClientTests.exe",[]),
                ("native-model","native-model","LocalMessaging.NativeModelTests.exe",[]),
                ("wic","wic","LocalMessaging.Platform.WindowsTests.exe",[]),
                ("windows-races","scenarios","LocalMessaging.Scenarios.exe",["--suite","windows","--evidence",Path.Combine(evidence,"windows-races.json")]),
                ("workload","store","LocalMessaging.StoreTests.exe",["--workload","--output",Path.Combine(evidence,"workload.json")])
            };
            foreach(var check in checks)
            {
                var exe=Path.Combine(verifyRoot,check.Folder,check.Exe);
                if(!File.Exists(exe)){blockers.Add("Missing separately published verifier: "+exe);return await Finish(3);}
                var exit=await RunAsync(exe,check.Args,Path.Combine(evidence,check.Name+".log"),TimeSpan.FromMinutes(10));
                steps.Add(new{name=check.Name,exitCode=exit,result=exit==0?"passed":exit is 2 or 3?"blocked":"failed"});
                if(exit!=0){result=exit is 2 or 3?"blocked":"failed";return await Finish(result=="blocked"?3:1);}
            }
            JsonObject? observations=null;
            if(options.TryGetValue("--observations",out var observedPath))
            {
                observations=JsonNode.Parse(await File.ReadAllTextAsync(observedPath))!.AsObject();
                if(observations["schemaVersion"]?.GetValue<int>()!=1||observations["candidateCommit"]?.GetValue<string>()!=candidate
                    ||observations["manifestSha256"]?.GetValue<string>()!=manifestHash||string.IsNullOrWhiteSpace(observations["reviewer"]?.GetValue<string>())
                    ||!DateTimeOffset.TryParseExact(observations["observedAtUtc"]?.GetValue<string>(),
                        ["yyyy-MM-dd'T'HH:mm:ss'Z'","yyyy-MM-dd'T'HH:mm:ss.f'Z'","yyyy-MM-dd'T'HH:mm:ss.ff'Z'",
                         "yyyy-MM-dd'T'HH:mm:ss.fff'Z'","yyyy-MM-dd'T'HH:mm:ss.ffff'Z'","yyyy-MM-dd'T'HH:mm:ss.fffff'Z'",
                         "yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'","yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'"],CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal|DateTimeStyles.AdjustToUniversal,out _))
                    throw new InvalidDataException("Observation record must identify this exact candidate/package and an actual reviewer.");
            }
            foreach(var acceptance in cases)
            {
                if(!ObservedCases.Contains(acceptance)) { caseResults.Add(new{id=acceptance,result="passed",method="automated published-host and focused qualification suites"});continue; }
                var record=observations?["checks"]?.AsArray().OfType<JsonObject>().SingleOrDefault(c=>c["id"]?.GetValue<string>()==acceptance);
                var complete=record is not null&&record["result"]?.GetValue<string>()=="passed"
                    &&record["unexpectedWarnings"]?.GetValue<int>()==0
                    &&record["steps"] is JsonArray {Count:>0} stepsArray&&stepsArray.All(s=>(s?.GetValue<string>()?.Length??0)>=8)
                    &&(record["observed"]?.GetValue<string>()?.Length??0)>=20
                    &&record["evidence"] is JsonArray {Count:>0} files&&files.All(f=>ObservationFileExists(options["--observations"],f?.GetValue<string>()));
                if(acceptance=="A42") complete &= observations?["environment"]?["cleanMachine"]?.GetValue<bool>()==true
                    &&observations?["environment"]?["noDevelopmentSdk"]?.GetValue<bool>()==true&&observations?["environment"]?["noNode"]?.GetValue<bool>()==true;
                if(!complete)blockers.Add(acceptance+" needs actual observed frontend/platform evidence; see docs/windows-qualification.md.");
                caseResults.Add(new{id=acceptance,result=complete?"passed":"blocked",method="recorded actual frontend/platform observation"});
            }
            foreach(var criterion in criteria)
            {
                var record=observations?["criteria"]?.AsArray().OfType<JsonObject>().SingleOrDefault(c=>c["id"]?.GetValue<string>()==criterion.Id);
                var complete=record is not null&&record["result"]?.GetValue<string>()=="passed"
                    &&record["unexpectedWarnings"]?.GetValue<int>()==0
                    &&(record["observed"]?.GetValue<string>()?.Length??0)>=20
                    &&record["evidence"] is JsonArray {Count:>0} files&&files.All(f=>ObservationFileExists(options["--observations"],f?.GetValue<string>()));
                if(!complete)blockers.Add(criterion.Id+" needs criterion-specific evidence linked by the exact-candidate observation/review record.");
                criterionResults.Add(new{id=criterion.Id,required=criterion.Text,result=complete?"passed":"blocked",method="criterion-specific recorded evidence, additional to source acceptance cases"});
            }
            result=blockers.Count==0?"passed":"blocked";
            return await Finish(blockers.Count==0?0:3);
        }
        catch(Exception e)
        {
            result="failed";blockers.Add(e.GetType().Name+": "+e.Message);Console.Error.WriteLine(e);return await Finish(1);
        }
    }

    private static bool ObservationFileExists(string record,string? relative)
    {
        if(string.IsNullOrWhiteSpace(relative)||Path.IsPathFullyQualified(relative))return false;
        var root=Path.GetDirectoryName(Path.GetFullPath(record))!+Path.DirectorySeparatorChar;
        var file=Path.GetFullPath(Path.Combine(root,relative));
        return file.StartsWith(root,StringComparison.OrdinalIgnoreCase)&&File.Exists(file)&&new FileInfo(file).Length>0;
    }
    private static bool IsWithin(string root,string candidate)
    {
        var normalized=Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar);
        var path=Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar);
        return string.Equals(path,normalized,StringComparison.OrdinalIgnoreCase)||path.StartsWith(normalized+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase);
    }
    private static async Task ValidateVerifierManifestAsync(string root,string candidate)
    {
        var manifest=JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(root,"manifest.json")))!.AsObject();
        if(manifest["schemaVersion"]?.GetValue<int>()!=1||manifest["candidateCommit"]?.GetValue<string>()!=candidate
            ||manifest["configuration"]?.GetValue<string>()!="Release"||manifest["runtimeIdentifier"]?.GetValue<string>()!="win-x64"||manifest["selfContained"]?.GetValue<bool>()!=true)
            throw new InvalidDataException("Verification tools do not identify the same self-contained Windows candidate as the product.");
        if((File.GetAttributes(root)&FileAttributes.ReparsePoint)!=0
            ||Directory.EnumerateFileSystemEntries(root,"*",SearchOption.AllDirectories).Any(p=>(File.GetAttributes(p)&FileAttributes.ReparsePoint)!=0))
            throw new InvalidDataException("Verifier root and every inventory entry must be regular directories/files, without reparse points.");
        var prefix=Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
        var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(var item in manifest["files"]!.AsArray())
        {
            var relative=item!["path"]!.GetValue<string>();
            if(Path.IsPathFullyQualified(relative))throw new InvalidDataException("Absolute verifier manifest path.");
            var file=Path.GetFullPath(Path.Combine(prefix,relative.Replace('/',Path.DirectorySeparatorChar)));
            if(!file.StartsWith(prefix,StringComparison.OrdinalIgnoreCase)||!seen.Add(file))throw new InvalidDataException("Unsafe or repeated verifier manifest path.");
            if((File.GetAttributes(file)&FileAttributes.ReparsePoint)!=0)throw new InvalidDataException("Verifier artifacts must be regular files.");
            if(new FileInfo(file).Length!=item["bytes"]!.GetValue<long>())throw new InvalidDataException("Verifier artifact size mismatch: "+relative);
            await using var input=File.OpenRead(file);
            var hash=Convert.ToHexStringLower(await SHA256.HashDataAsync(input));
            if(hash!=item["sha256"]!.GetValue<string>())throw new InvalidDataException("Verifier artifact hash mismatch: "+relative);
        }
        var actual=Directory.EnumerateFiles(root,"*",SearchOption.AllDirectories).Where(f=>!string.Equals(Path.GetFullPath(f),Path.Combine(prefix,"manifest.json"),StringComparison.OrdinalIgnoreCase));
        if(!seen.SetEquals(actual.Select(Path.GetFullPath)))throw new InvalidDataException("Verifier inventory differs from its manifest.");
    }
    private static string[] SelectCases(string scope,string id)
    {
        var plan=JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"plan","implementation-plan.json")))!.AsObject();
        var all=plan["acceptance_coverage"]!.AsArray().OfType<JsonObject>().ToArray();
        if(scope=="acceptance")
        {
            if(id=="All")return all.Select(a=>a["id"]!.GetValue<string>()).ToArray();
            if(all.Any(a=>a["id"]!.GetValue<string>()==id))return [id];
            throw new ArgumentException("Unknown acceptance ID.");
        }
        var tasks=plan["tasks"]!.AsArray().OfType<JsonObject>().ToArray();
        IEnumerable<JsonObject> selected;
        if(scope is "task" or "demo")selected=tasks.Where(t=>t["id"]!.GetValue<string>()==id);
        else if(scope=="checkpoint")selected=tasks.Where(t=>t["checkpoint"]!.GetValue<string>()==id);
        else if(scope=="milestone"&&plan["milestones"]!.AsArray().Any(m=>m!["id"]!.GetValue<string>()==id))
            selected=tasks.Where(t=>string.CompareOrdinal(t["milestone"]!.GetValue<string>(),id)<=0);
        else throw new ArgumentException("Unknown scope or milestone ID.");
        var materialized=selected.ToArray();if(materialized.Length==0)throw new ArgumentException("Unknown task/checkpoint ID.");
        return materialized.SelectMany(t=>t["source_acceptance_refs"]!.AsArray().Select(a=>a!.GetValue<string>())).Distinct().Order().ToArray();
    }
    private static (string Id,string Text)[] SelectTaskCriteria(string scope,string id)
    {
        if(scope=="acceptance")return [];
        var plan=JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"plan","implementation-plan.json")))!.AsObject();
        var tasks=plan["tasks"]!.AsArray().OfType<JsonObject>();
        var selected=tasks.Where(t=>scope switch {
            "task" or "demo"=>t["id"]!.GetValue<string>()==id,
            "checkpoint"=>t["checkpoint"]!.GetValue<string>()==id,
            "milestone"=>string.CompareOrdinal(t["milestone"]!.GetValue<string>(),id)<=0,
            _=>false
        }).SelectMany(t=>t["acceptance_criteria"]!.AsArray().OfType<JsonObject>())
          .Select(c=>(c["id"]!.GetValue<string>(),c["text"]!.GetValue<string>())).ToArray();
        if(selected.Length==0)throw new ArgumentException("No mapped task criteria; an empty selection cannot pass.");
        return selected;
    }
    private static async Task<int> RunAsync(string exe,string[] arguments,string log,TimeSpan timeout)
    {
        using var process=new Process { StartInfo=new ProcessStartInfo(exe) { UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true,CreateNoWindow=true } };
        foreach(var argument in arguments)process.StartInfo.ArgumentList.Add(argument);
        if(!process.Start())throw new InvalidOperationException("Could not start verifier "+exe);
        using var writer=new StreamWriter(log);using var gate=new SemaphoreSlim(1);long characters=0;
        async Task Drain(StreamReader reader,string prefix)
        {
            var buffer=new char[4096];int count;
            while((count=await reader.ReadAsync(buffer))>0)
            {
                await gate.WaitAsync();try
                {
                    if(characters<8*1024*1024){var kept=(int)Math.Min(count,8*1024*1024-characters);await writer.WriteAsync(prefix);await writer.WriteAsync(buffer.AsMemory(0,kept));characters+=kept;}
                }
                finally{gate.Release();}
            }
        }
        var stdout=Drain(process.StandardOutput,"");var stderr=Drain(process.StandardError,"stderr: ");
        using var deadline=new CancellationTokenSource(timeout);
        try { await process.WaitForExitAsync(deadline.Token);await Task.WhenAll(stdout,stderr);return process.ExitCode; }
        catch(OperationCanceledException){process.Kill(entireProcessTree:true);await process.WaitForExitAsync();await Task.WhenAll(stdout,stderr);throw new TimeoutException("Verifier exceeded its bounded runtime: "+exe);}
    }
}
