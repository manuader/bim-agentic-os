using System.Diagnostics;
using Bim.Ifc;
using System.Text.Json;

namespace Bim.Evaluation;

public sealed record IfcExecutionOptions(string PythonExecutable, string WorkerScript, string OutputDirectory)
{
    public IfcCompiledRequest? Request { get; init; }
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(2);
}
internal sealed record IfcCompiledBinding(string Role,string GlobalId,int StepId);
internal sealed record IfcEnvelope(int ProtocolVersion,string Backend,string BackendVersion,string Source,BackendObservations Observations,Dictionary<string,Metric> Metrics,CriterionResult[] Checks,string[] Artifacts,string[] Errors,Dictionary<Guid,IfcCompiledBinding>? CompiledBindings=null,Guid[]? UnsupportedCoreOutputs=null,string[]? FixtureOnlyRoles=null);

public static partial class EvaluationService
{
    private static bool ProcessResultConsistent(int exitCode,bool failed) => (exitCode==0)!=failed;
    private static async Task WaitForOwnedProcessAsync(Process process,Task stdout,Task stderr,CancellationToken token)
    {
        try {await process.WaitForExitAsync(token);await Task.WhenAll(stdout,stderr);}
        catch {if(!process.HasExited)process.Kill(entireProcessTree:true);throw;}
    }
    private static async Task<string> ReadBoundedAsync(StreamReader reader,CancellationToken token)
    {
        var output=new System.Text.StringBuilder();var buffer=new char[4096];int count;
        while((count=await reader.ReadAsync(buffer.AsMemory(),token))>0) {
            if(output.Length+count>4*1024*1024)throw new IOException("IFC worker output exceeds 4 MiB");
            output.Append(buffer,0,count);
        }
        return output.ToString();
    }
    private static readonly string[] RequiredIfcChecks=["ifc_schema","ifc_geometry","ifc_hosting","ifc_save_reopen","ifc_idempotency","ifc_rollback","ifc_incremental","ifc_receipts"];
    private static IfcEnvelope? ParseIfcEnvelope(string json)
    {
            IfcEnvelope? envelope;
            try { envelope=JsonSerializer.Deserialize<IfcEnvelope>(json,JsonOptions); }
            catch(JsonException) {return null;}
            if(envelope==null||envelope.ProtocolVersion!=1||envelope.Backend!="ifc"||string.IsNullOrWhiteSpace(envelope.BackendVersion)||string.IsNullOrWhiteSpace(envelope.Source)||envelope.Observations==null||envelope.Observations.Backend!="ifc"||envelope.Observations.Source!=envelope.Source||envelope.Observations.TestA==null||envelope.Observations.TestB==null||envelope.Checks==null||envelope.Errors==null||envelope.Metrics==null||envelope.Artifacts==null)return null;
            if(envelope.Checks.Any(x=>x==null||string.IsNullOrWhiteSpace(x.Id)||x.Outcome is not ("pass" or "fail" or "blocked" or "unsupported")||string.IsNullOrWhiteSpace(x.Reason))||envelope.Checks.Select(x=>x.Id).Distinct().Count()!=envelope.Checks.Length||RequiredIfcChecks.Any(id=>!envelope.Checks.Any(x=>x.Id==id)))return null;
            if(envelope.Metrics.Any(x=>!MetricNames.Concat(new[]{"create_ms","update_ms","retry_ms","rollback_ms"}).Contains(x.Key)||x.Value==null||string.IsNullOrWhiteSpace(x.Value.Reason)||x.Value.Value is double value&&(!double.IsFinite(value)||value<0)))return null;
            return envelope;
    }
    private static bool CompiledBindingsMatch(IfcEnvelope envelope,IfcCompiledRequest request)
    {
        if(envelope.Observations.TestB?.After?.Elements==null)return false;
        if(envelope.CompiledBindings==null||envelope.CompiledBindings.Count!=request.LogicalRoles.Count||request.LogicalRoles.Any(pair=>!envelope.CompiledBindings.TryGetValue(pair.Value,out var binding)||binding==null||binding.Role!=pair.Key||string.IsNullOrWhiteSpace(binding.GlobalId)||binding.StepId<=0||!envelope.Observations.TestB!.After.Elements.Any(element=>element!=null&&element.LogicalId==pair.Key&&element.NativeId==binding.GlobalId))||envelope.CompiledBindings.Values.Select(binding=>binding.GlobalId).Distinct().Count()!=request.LogicalRoles.Count||envelope.UnsupportedCoreOutputs==null||!envelope.UnsupportedCoreOutputs.SequenceEqual(request.UnsupportedDocumentationIds)||envelope.FixtureOnlyRoles==null||!envelope.FixtureOnlyRoles.SequenceEqual(new[]{"door","window-north","window-east"}))return false;
        return true;
    }
    public static async Task<EvaluationReport> ExecuteAsync(string backend,IfcExecutionOptions options,CancellationToken cancellationToken=default)
    {
        if(backend!="ifc")return await ExecuteAsync(backend,cancellationToken);
        var timer=Stopwatch.StartNew();
        EvaluationReport Block(string reason) => Evaluate(new("ifc","IFC execution prerequisite inspection",null,null),"blocked",timer) with {Gaps=[reason]};
        try {
            if(!Path.IsPathFullyQualified(options.PythonExecutable)||!File.Exists(options.PythonExecutable)||!File.Exists(options.WorkerScript))return Block("explicit Python executable and repository IFC worker required");
            var root=new DirectoryInfo(AppContext.BaseDirectory);
            while(root!=null && !(File.Exists(Path.Combine(root.FullName,"global.json"))&&Directory.Exists(Path.Combine(root.FullName,"core","Bim.Core"))))root=root.Parent;
            var expected=root==null?null:Path.Combine(root.FullName,"runtimes","ifc","worker.py");
            var comparison=OperatingSystem.IsWindows()?StringComparison.OrdinalIgnoreCase:StringComparison.Ordinal;
            if(expected==null||!Path.GetFullPath(options.WorkerScript).Equals(expected,comparison))return Block("foreign IFC worker path rejected");
            for(var entry=new FileInfo(expected) as FileSystemInfo;entry!=null;entry=entry is FileInfo f?f.Directory:(entry as DirectoryInfo)?.Parent)
                if(entry.LinkTarget!=null)return Block("IFC worker symlink or reparse path rejected");
            if(!Path.IsPathFullyQualified(options.OutputDirectory)||options.Timeout<=TimeSpan.Zero||options.Timeout>TimeSpan.FromMinutes(10))return Block("absolute output directory and bounded timeout required");
            var request=options.Request??IfcCompiler.EvaluationRequest();
            IfcCompiler.Validate(request);
            Directory.CreateDirectory(options.OutputDirectory);
            var requestPath=Path.Combine(options.OutputDirectory,"compiler-input.json");
            using(var requestFile=new FileStream(requestPath,FileMode.CreateNew,FileAccess.Write,FileShare.None))JsonSerializer.Serialize(requestFile,request);
            var start=new ProcessStartInfo(options.PythonExecutable){UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true,CreateNoWindow=true,WorkingDirectory=root!.FullName};
            start.ArgumentList.Add(expected);start.ArgumentList.Add("--output-dir");start.ArgumentList.Add(Path.GetFullPath(options.OutputDirectory));start.ArgumentList.Add("--request");start.ArgumentList.Add(requestPath);
            using var process=new Process{StartInfo=start};
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);timeout.CancelAfter(options.Timeout);
            process.Start();
            var stdout=ReadBoundedAsync(process.StandardOutput,timeout.Token);var stderr=ReadBoundedAsync(process.StandardError,timeout.Token);
            string raw; string diagnostic;
            try { await WaitForOwnedProcessAsync(process,stdout,stderr,timeout.Token); raw=await stdout;diagnostic=await stderr; }
            catch(IOException ex) { if(!process.HasExited)process.Kill(entireProcessTree:true);return Block(ex.Message); }
            catch(OperationCanceledException) { if(!process.HasExited)process.Kill(entireProcessTree:true);return Block(cancellationToken.IsCancellationRequested?"IFC execution cancelled":"IFC execution timed out"); }

            await File.WriteAllTextAsync(Path.Combine(options.OutputDirectory,"worker.stderr.log"),diagnostic,CancellationToken.None);
            var envelope=ParseIfcEnvelope(raw);
            if(envelope==null)return Block("invalid IFC worker protocol, checks or metrics");
            if(!CompiledBindingsMatch(envelope,request))return Block("IFC compiled identity or unsupported-output contract mismatch");
            if(envelope.Artifacts.Length==0||envelope.Artifacts.Any(name=>string.IsNullOrWhiteSpace(name)||name!=Path.GetFileName(name)||name.Contains('/')||name.Contains('\\')||!File.Exists(Path.Combine(options.OutputDirectory,name))))return Block("invalid or missing IFC artifacts");
            foreach(var name in envelope.Artifacts)if(new FileInfo(Path.Combine(options.OutputDirectory,name)).LinkTarget!=null)return Block("IFC artifact reparse path rejected");
            if(!envelope.Artifacts.Any(x=>Path.GetExtension(x)==".ifc"))return Block("saved IFC artifact missing");
            var report=EvaluateOffline(envelope.Observations);
            if(report.Backend!="ifc")return Block("malformed IFC observations");
            var checkFailure=envelope.Checks.Any(x=>x.Outcome=="fail")||envelope.Errors.Length>0;
            if(!ProcessResultConsistent(process.ExitCode,checkFailure))return Block("IFC process/check result disagreement");
            var allChecks=envelope.Checks.All(x=>x.Outcome=="pass");
            var metrics=report.Metrics;foreach(var metric in envelope.Metrics)metrics[metric.Key]=metric.Value;
            var accepted=process.ExitCode==0&&allChecks&&report.Counts.Failed==0&&report.Counts.Blocked==0;
            return report with {VerificationState="integration-tested (ifc)",BackendVersion=envelope.BackendVersion,BackendChecks=envelope.Checks,BackendAcceptance=accepted,Metrics=metrics,TimingScope="IFC runtime execution and independent C# oracle; native Revit documentation unsupported",Gaps=[..report.Gaps,..envelope.Errors],ExitCode=checkFailure||report.Counts.Failed>0?1:2};
        } catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) {return Block("IFC execution unavailable: "+ex.Message);}
    }
}
