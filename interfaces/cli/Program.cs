using Bim.Core;
using Bim.Ifc;
using Bim.Evaluation;
using System.Text.Json;
using System.IO.Pipes;
using Bim.Cli;
try {
    switch(args[0]) {
        case "ifc-export":
            if(args.Length!=6)throw new ArgumentException("usage: ifc-export before.json after.json plan.json room-guid operation-id");
            var ifcBefore=JsonSerializer.Deserialize<Project>(BoundedInput.ReadText(args[1]))!;
            var ifcAfter=JsonSerializer.Deserialize<Project>(BoundedInput.ReadText(args[2]))!;
            var ifcPlan=JsonSerializer.Deserialize<ExecutionPlan>(BoundedInput.ReadText(args[3]))!;
            Console.WriteLine(JsonSerializer.Serialize(IfcCompiler.Export(ifcBefore,ifcAfter,ifcPlan,Guid.Parse(args[4]),args[5])));break;
        case "evaluate":
            EvaluationReport evaluation;
            try {
                evaluation = args.Length switch {
                    2 => await EvaluationService.ExecuteAsync(args[1]),
                    5 when args[1] == "ifc" => await EvaluationService.ExecuteAsync("ifc",new IfcExecutionOptions(args[2],args[3],args[4])),
                    6 when args[1] == "ifc" => await EvaluationService.ExecuteAsync("ifc",new IfcExecutionOptions(args[2],args[3],args[4]) {Request=JsonSerializer.Deserialize<IfcCompiledRequest>(BoundedInput.ReadText(args[5]))}),
                    3 when args[1] == "revit" => await EvaluationService.ExecuteAsync("revit",JsonSerializer.Deserialize<RevitExecutionOptions>(BoundedInput.ReadText(args[2]),EvaluationService.JsonOptions) ?? throw new ArgumentException("native options required")),
                    3 when args[1] == "offline" => EvaluationService.ParseOffline(BoundedInput.ReadText(args[2])),
                    _ => EvaluationService.Malformed("usage: evaluate ifc|revit | evaluate revit options.json | evaluate offline observations.json")
                };
            } catch (Exception e) { evaluation=EvaluationService.Malformed("evaluation_input_error: "+e.Message); }
            Console.WriteLine(JsonSerializer.Serialize(evaluation,EvaluationService.JsonOptions)); Environment.ExitCode=evaluation.ExitCode; break;
        case "room": var roomIntent=JsonSerializer.Deserialize<RoomIntent>(BoundedInput.ReadText(args[3]))!; Console.WriteLine(JsonSerializer.Serialize(RoomCompiler.Compile(Guid.Parse(args[1]),int.Parse(args[2]),roomIntent))); break;
        case "init": new ProjectStore(args[1]).Initialize(JsonSerializer.Deserialize<Project>(BoundedInput.ReadText(args[2]))!); Console.WriteLine("{\"status\":\"succeeded\",\"scope\":\"intended_state_only\"}"); break;
        case "plan": var current=new ProjectStore(args[1]).Read(); var desired=JsonSerializer.Deserialize<Project>(BoundedInput.ReadText(args[2]))!; Console.WriteLine(JsonSerializer.Serialize(Compiler.Diff(current,desired))); break;
        case "commit-intent":
            var store=new ProjectStore(args[1]); var target=JsonSerializer.Deserialize<Project>(BoundedInput.ReadText(args[2]))!; var patch=JsonSerializer.Deserialize<ExecutionPlan>(BoundedInput.ReadText(args[3]))!;
            Console.WriteLine(JsonSerializer.Serialize(new {status="succeeded",scope="intended_state_only",native_verification="unverified",project=store.Commit(target,patch,args[4])})); break;
        case "reconcile":
            if(args.Length!=4)throw new ArgumentException("usage: reconcile core-directory ifc-receipt.json compiled-request.json");
            var reconciled=IfcReconcile.Reconcile(args[1],IfcReconcile.ParseReceipt(BoundedInput.ReadText(args[2])),IfcReconcile.ParsePacket(BoundedInput.ReadText(args[3])));
            Console.WriteLine(JsonSerializer.Serialize(new {status=reconciled.Succeeded?"succeeded":"failed",code=reconciled.Code,scope="intended_state_reconcile",core_revision=reconciled.CoreRevision,backend_revision=reconciled.BackendRevision,core_hash=reconciled.CoreHash,backend_hash=reconciled.BackendHash}));
            if(!reconciled.Succeeded)Environment.ExitCode=1;
            break;
        case "diff": var a=JsonSerializer.Deserialize<Project>(BoundedInput.ReadText(args[1]))!; var b=JsonSerializer.Deserialize<Project>(BoundedInput.ReadText(args[2]))!; Console.WriteLine(JsonSerializer.Serialize(Compiler.Diff(a,b)));break;
        case "validate": var issues=Compiler.Validate(JsonSerializer.Deserialize<Project>(BoundedInput.ReadText(args[1]))!); Console.WriteLine(JsonSerializer.Serialize(issues)); if(issues.Length>0)Environment.ExitCode=1;break;
        case "native":
            if(args.Length != 3 || !System.Text.RegularExpressions.Regex.IsMatch(args[2], @"\Abim-agentic-os-2025-[1-9][0-9]*\z")) throw new ArgumentException("explicit_process_pipe_required");
            var nativeRequest=BoundedInput.ReadText(args[1]);
            using(var pipe=new NamedPipeClientStream(".",args[2],PipeDirection.InOut,PipeOptions.Asynchronous)) {
                await pipe.ConnectAsync(5000); using var writer=new StreamWriter(pipe){AutoFlush=true};using var reader=new StreamReader(pipe);
                await writer.WriteLineAsync(nativeRequest); Console.WriteLine(await reader.ReadLineAsync());
            } break;
        default:throw new ArgumentException("usage: room project-guid revision intent.json | diff before.json after.json | validate project.json | native request.json process-pipe-name");
    }
} catch(Exception e) {Console.WriteLine(JsonSerializer.Serialize(new {status="failed",code=e.Message}));Environment.ExitCode=1;}
