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
        case "export-gltf": GltfExportCommand.Run(args); break;
        case "cad-draft": case "cad-brief": CadCommand.Run(args); break;
        case "cad-plan-brief": CadPlanBriefCommand.Run(args); break;
        case "mirror-drift": MirrorDriftCommand.Run(args); break;
        case "mirror-drift-select": MirrorDriftSelectCommand.Run(args); break;
        case "skills-lock": SkillLockCommand.Run(args); break;
        case "diff-edits": DiffEditsCommand.Run(args); break;
        case "apply-edits": ApplyEditsCommand.Run(args); break;
        case "doc-update": DocUpdateCommand.Run(args); break;
        case "catalogue-report": CatalogueReportCommand.Run(args); break;
        case "generate-prototypes": GenerateCommand.Run(args); break;
        case "plan-layouts": PlanLayoutsCommand.Run(args); break;
        case "audit-verify": AuditVerifyCommand.Run(args); break;
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
        case "revit-command":
            // ADR0009 catalogue v1: same explicit-pipe rule as native, checked before any file or pipe I/O. Enabled write
            // roots come only from the repository (.bim/native-runs and the owner file .bim/revit-enabled-roots.txt), read
            // fresh on every run through the same RevitCommandPolicySource the MCP server keeps current.
            // --all (P1-15): a query.elements envelope is paged with RevitElementQueryPaging.All, each page through the
            // same RevitCommandService path; every --all outcome, early refusals included, is ElementQueryAllResult JSON.
            // Any other 4th (or further) argument keeps the explicit_process_pipe_required refusal (the call is not the
            // explicit pipe form; tests/revit-commands pins that code) but names the argument and the usage.
            if(args.Length>=4 && (args[3]!="--all" || args.Length>4)) {
                var revitExtra=args[3]!="--all" ? 3 : 4;
                Console.WriteLine(RevitCommandResponse.Failure(RevitCommandCodes.ExplicitProcessPipeRequired,"args["+revitExtra+"]",(revitExtra==3 ? "unknown option " : "unexpected argument ")+args[revitExtra]+"; usage: revit-command envelope.json process-pipe-name [--all]").ToJson()); Environment.ExitCode=1; break;
            }
            var revitAll=args.Length==4;
            string RevitEarly(string code,string? field,string? detail) => revitAll ? ElementQueryAllResult.Refusal(code,field,detail).ToJson() : RevitCommandResponse.Failure(code,field,detail).ToJson();
            var revitPipe=args.Length>=3 ? args[2] : null;
            if(RevitCommandService.RejectPipe(revitPipe) is {} revitRejected) { var revitRefusal=RevitCommandResponse.Parse(revitRejected.Line); Console.WriteLine(RevitEarly(revitRefusal.Code,revitRefusal.Field,revitRefusal.Detail)); Environment.ExitCode=1; break; }
            RevitCommandPolicy revitPolicy;
            try { revitPolicy=new RevitCommandPolicySource(AppContext.BaseDirectory).Current(); }
            catch(RevitCommandException e) { Console.WriteLine(RevitEarly(e.Code,e.Field,e.Detail)); Environment.ExitCode=1; break; }
            string revitEnvelope;
            try { revitEnvelope=BoundedInput.ReadText(args[1]); }
            catch(IOException e) { Console.WriteLine(RevitEarly(RevitCommandCodes.EnvelopeInvalid,"envelope",e.Message)); Environment.ExitCode=1; break; }
            if(revitAll) {
                var revitListing=RevitElementQueryPaging.All(revitEnvelope,new RevitServiceTransport(revitPipe!,revitPolicy,RevitCommandService.PipeTransport));
                Console.WriteLine(revitListing.ToJson()); if(!revitListing.IsSuccess)Environment.ExitCode=1; break;
            }
            var revitResult=RevitCommandService.Run(revitPipe!,revitEnvelope,revitPolicy,RevitCommandService.PipeTransport);
            Console.WriteLine(revitResult.Line); if(revitResult.IsError)Environment.ExitCode=1; break;
        case "open-project-check":
            // Owner project mode preflight (scripts/owner-open-project.ps1): may this .rvt be opened in a new child Revit
            // with the command pipe? Same enabled roots as revit-command writes, read from this binary's repository; no pipe.
            if(args.Length!=2) throw new ArgumentException("usage: open-project-check absolute-path.rvt");
            RevitCommandPolicy projectPolicy;
            try { projectPolicy=new RevitCommandPolicySource(AppContext.BaseDirectory).Current(); }
            catch(RevitCommandException e) { Console.WriteLine(JsonSerializer.Serialize(new {status="failed",code=e.Code,detail=e.Field+": "+e.Detail,document_path=args[1]})); Environment.ExitCode=1; break; }
            if(OwnerProjectGuard.Check(args[1],projectPolicy,RevitFileSystemProbe.Real) is {} projectRefusal) { Console.WriteLine(JsonSerializer.Serialize(new {status="failed",code=projectRefusal.Code,detail=projectRefusal.Detail,document_path=args[1]})); Environment.ExitCode=1; break; }
            Console.WriteLine(JsonSerializer.Serialize(new {status="succeeded",document_path=args[1],enabled_root=RevitCopiesGuard.MatchingRoot(args[1],projectPolicy)})); break;
        default:throw new ArgumentException("usage: room project-guid revision intent.json | diff before.json after.json | validate project.json | export-gltf project.json output.glb|output.gltf | cad-draft input.dxf|input.pdf output.json [options] | cad-brief input.dxf|input.pdf output.json [options] | cad-plan-brief draft.json output.json [--min-confidence x] [--confirm id]... [--confirm-reason code]... | mirror-drift store-directory snapshot.json mirror.json output.json | mirror-drift-select store-directory snapshot.json mirror.json choices.json output.json | skills-lock pin|verify|update|unpin skills-directory lock.json [pack-id...] | diff-edits --project project.json --edits edits.json | apply-edits --store store-directory --edits edits.json [--expected-revision n] | doc-update --before before.json --after after.json --realized realized.json --bindings bindings.json --output plan.json [--dependents dependents.json | --probe-dependents process-pipe-name [--accept uid]...] | catalogue-report --run .bim/native-runs/<run> | generate-prototypes request.json output.json | audit-verify log.jsonl [--anchor head.json] [--actors a,b] [--summary] [--write-anchor head.json] | native request.json process-pipe-name | revit-command envelope.json process-pipe-name [--all] | open-project-check absolute-path.rvt");
    }
} catch(Exception e) {Console.WriteLine(JsonSerializer.Serialize(new {status="failed",code=e.Message}));Environment.ExitCode=1;}
