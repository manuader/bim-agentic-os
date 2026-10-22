using Bim.Core;
using System.Text.Json;
using System.IO.Pipes;
try {
    switch(args[0]) {
        case "room": var roomIntent=JsonSerializer.Deserialize<RoomIntent>(File.ReadAllText(args[3]))!; Console.WriteLine(JsonSerializer.Serialize(RoomCompiler.Compile(Guid.Parse(args[1]),int.Parse(args[2]),roomIntent))); break;
        case "init": new ProjectStore(args[1]).Initialize(JsonSerializer.Deserialize<Project>(File.ReadAllText(args[2]))!); Console.WriteLine("{\"status\":\"succeeded\",\"scope\":\"intended_state_only\"}"); break;
        case "plan": var current=new ProjectStore(args[1]).Read(); var desired=JsonSerializer.Deserialize<Project>(File.ReadAllText(args[2]))!; Console.WriteLine(JsonSerializer.Serialize(Compiler.Diff(current,desired))); break;
        case "commit-intent":
            var store=new ProjectStore(args[1]); var target=JsonSerializer.Deserialize<Project>(File.ReadAllText(args[2]))!; var patch=JsonSerializer.Deserialize<ExecutionPlan>(File.ReadAllText(args[3]))!;
            Console.WriteLine(JsonSerializer.Serialize(new {status="succeeded",scope="intended_state_only",native_verification="unverified",project=store.Commit(target,patch,args[4])})); break;
        case "diff": var a=JsonSerializer.Deserialize<Project>(File.ReadAllText(args[1]))!; var b=JsonSerializer.Deserialize<Project>(File.ReadAllText(args[2]))!; Console.WriteLine(JsonSerializer.Serialize(Compiler.Diff(a,b)));break;
        case "validate": var issues=Compiler.Validate(JsonSerializer.Deserialize<Project>(File.ReadAllText(args[1]))!); Console.WriteLine(JsonSerializer.Serialize(issues)); if(issues.Length>0)Environment.ExitCode=1;break;
        case "native":
            using(var pipe=new NamedPipeClientStream(".","bim-agentic-os-2025",PipeDirection.InOut,PipeOptions.Asynchronous)) {
                await pipe.ConnectAsync(5000); using var writer=new StreamWriter(pipe){AutoFlush=true};using var reader=new StreamReader(pipe);
                await writer.WriteLineAsync(File.ReadAllText(args[1])); Console.WriteLine(await reader.ReadLineAsync());
            } break;
        default:throw new ArgumentException("usage: room project-guid revision intent.json | diff before.json after.json | validate project.json | native request.json");
    }
} catch(Exception e) {Console.WriteLine(JsonSerializer.Serialize(new {status="failed",code=e.Message}));Environment.ExitCode=1;}
