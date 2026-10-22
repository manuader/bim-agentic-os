using Bim.Core;
using System.Text.Json;
using System.IO.Pipes;
try {
    switch(args[0]) {
        case "diff": var a=JsonSerializer.Deserialize<Project>(File.ReadAllText(args[1]))!; var b=JsonSerializer.Deserialize<Project>(File.ReadAllText(args[2]))!; Console.WriteLine(JsonSerializer.Serialize(Compiler.Diff(a,b)));break;
        case "validate": var issues=Compiler.Validate(JsonSerializer.Deserialize<Project>(File.ReadAllText(args[1]))!); Console.WriteLine(JsonSerializer.Serialize(issues)); if(issues.Length>0)Environment.ExitCode=1;break;
        case "native":
            using(var pipe=new NamedPipeClientStream(".","bim-agentic-os-2025",PipeDirection.InOut,PipeOptions.Asynchronous)) {
                await pipe.ConnectAsync(5000); using var writer=new StreamWriter(pipe){AutoFlush=true};using var reader=new StreamReader(pipe);
                await writer.WriteLineAsync(File.ReadAllText(args[1])); Console.WriteLine(await reader.ReadLineAsync());
            } break;
        default:throw new ArgumentException("usage: diff before.json after.json | validate project.json | native request.json");
    }
} catch(Exception e) {Console.WriteLine(JsonSerializer.Serialize(new {status="failed",code=e.Message}));Environment.ExitCode=1;}
