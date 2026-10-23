using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
namespace Bim.Evaluation;
public sealed record RevitExecutionOptions(string RunDirectory,string PipeName,string DocumentPath,string DocumentIdentity,int ProcessId,int TimeoutSeconds=180);
internal sealed record RevitEnvelope(int ProtocolVersion,string Backend,string BackendVersion,string Source,BackendObservations Observations,Dictionary<string,Metric> Metrics,CriterionResult[] Checks,string[] Artifacts,string[] Errors);
public static partial class EvaluationService
{
    private static readonly string[] RequiredRevitChecks=["native_hosting","native_geometry","native_documentation","native_save_reopen","native_idempotency","native_rollback","native_incremental","native_receipts"];
    [DllImport("kernel32.dll",SetLastError=true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe,out uint serverProcessId);
    private static void NoLinks(string path)
    {
        for(FileSystemInfo? entry=Directory.Exists(path)?new DirectoryInfo(path):new FileInfo(path);entry!=null;entry=entry is FileInfo f?f.Directory:(entry as DirectoryInfo)?.Parent)
            if(entry.LinkTarget!=null)throw new ArgumentException("reparse fixture path rejected");
    }
    internal static RevitEnvelope? ParseNativeEnvelope(string raw,string build) => ParseNativeEnvelopeCore(raw,build,true);
    internal static string? ParseNativeFailure(string raw,string build)
    {
        var envelope=ParseNativeEnvelopeCore(raw,build,false);
        return envelope!=null&&envelope.Errors.Length>0&&envelope.Errors.All(x=>!string.IsNullOrWhiteSpace(x))?string.Join("; ",envelope.Errors):null;
    }
    private static RevitEnvelope? ParseNativeEnvelopeCore(string raw,string build,bool requireReadback)
    {
            RevitEnvelope? envelope;try {envelope=JsonSerializer.Deserialize<RevitEnvelope>(raw,JsonOptions);}catch(JsonException){return null;}
            if(envelope==null||envelope.ProtocolVersion!=1||envelope.Backend!="revit"||envelope.BackendVersion!=build||envelope.Source!="actual isolated Revit authoring and reopened readback"||envelope.Observations==null||envelope.Observations.Backend!="revit"||envelope.Observations.Source!=envelope.Source||requireReadback&&(envelope.Observations.TestA==null||envelope.Observations.TestB==null)||envelope.Checks==null||envelope.Errors==null||envelope.Artifacts==null||envelope.Metrics==null)return null;
            if(envelope.Checks.Length!=RequiredRevitChecks.Length||envelope.Checks.Any(x=>x==null||!RequiredRevitChecks.Contains(x.Id)||x.Outcome is not ("pass" or "fail" or "blocked" or "unsupported")||string.IsNullOrWhiteSpace(x.Reason))||envelope.Checks.Select(x=>x.Id).Distinct().Count()!=RequiredRevitChecks.Length)return null;
            if(envelope.Metrics.Any(x=>!MetricNames.Concat(new[]{"create_ms","update_ms","retry_ms","rollback_ms"}).Contains(x.Key)||x.Value==null||string.IsNullOrWhiteSpace(x.Value.Reason)||x.Value.Value is double v&&(!double.IsFinite(v)||v<0)))return null;
            return envelope;
    }
    private static bool SamePath(string a,string b)=>Path.IsPathFullyQualified(a)&&Path.IsPathFullyQualified(b)&&Path.GetFullPath(a).Equals(Path.GetFullPath(b),OperatingSystem.IsWindows()?StringComparison.OrdinalIgnoreCase:StringComparison.Ordinal);
    public static async Task<EvaluationReport> ExecuteAsync(string backend,RevitExecutionOptions options,CancellationToken cancellationToken=default)
    {
        if(backend!="revit")return await ExecuteAsync(backend,cancellationToken);
        var timer=Stopwatch.StartNew();bool dispatched=false;
        EvaluationReport Block(string reason)=>Evaluate(new("revit","native fixture prerequisite/execution inspection",null,null),"blocked",timer) with {Gaps=[reason],ExitCode=2};
        try {
            if(options.TimeoutSeconds<1||options.TimeoutSeconds>900)return Block("bounded timeout 1..900 seconds required");
            if(!Path.IsPathFullyQualified(options.RunDirectory)||!Directory.Exists(options.RunDirectory)||!Path.IsPathFullyQualified(options.DocumentPath)||!SamePath(Path.GetDirectoryName(options.DocumentPath)!,options.RunDirectory)||!Path.GetFileName(options.DocumentPath).StartsWith("BIM-Agentic-Test",StringComparison.Ordinal)||!Path.GetExtension(options.DocumentPath).Equals(".rvt",StringComparison.OrdinalIgnoreCase)||string.IsNullOrWhiteSpace(options.DocumentIdentity)||options.ProcessId<=0||options.PipeName!="bim-agentic-os-2025-"+options.ProcessId)return Block("explicit isolated fixture path, identity and PID pipe required");
            var configPath=Path.Combine(options.RunDirectory,"fixture-config.json");var resultPath=Path.Combine(options.RunDirectory,"fixture-result.json");
            if(!File.Exists(configPath)||!File.Exists(resultPath)||!File.Exists(options.DocumentPath))return Block("fixture config/result/model missing");
            foreach(var path in new[]{options.RunDirectory,configPath,resultPath,options.DocumentPath})NoLinks(path);
            using var config=JsonDocument.Parse(File.ReadAllText(configPath));using var result=JsonDocument.Parse(File.ReadAllText(resultPath));
            var c=config.RootElement;var r=result.RootElement;
            if(!c.GetProperty("ConfirmIsolatedTestProcess").GetBoolean()||!SamePath(c.GetProperty("RunDirectory").GetString()!,options.RunDirectory)||!SamePath(c.GetProperty("FixturePath").GetString()!,options.DocumentPath)||!SamePath(c.GetProperty("ResultPath").GetString()!,resultPath)||r.GetProperty("status").GetString()!="succeeded"||r.GetProperty("processId").GetInt32()!=options.ProcessId||r.GetProperty("pipeName").GetString()!=options.PipeName||!SamePath(r.GetProperty("documentPath").GetString()!,options.DocumentPath)||r.GetProperty("documentUniqueId").GetString()!=options.DocumentIdentity||r.GetProperty("revitVersion").GetString()!="2025")return Block("fixture config/result provenance mismatch");
            var build=r.GetProperty("revitBuild").GetString();if(string.IsNullOrWhiteSpace(build))return Block("fixture actual Revit build missing");
            if(!OperatingSystem.IsWindows())return Block("Windows same-user native fixture required");
            using var process=Process.GetProcessById(options.ProcessId);if(process.HasExited||!process.ProcessName.Equals("Revit",StringComparison.OrdinalIgnoreCase))return Block("fixture live Revit PID required");
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);timeout.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));
            using var pipe=new NamedPipeClientStream(".",options.PipeName,PipeDirection.InOut,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(timeout.Token);
            if(!GetNamedPipeServerProcessId(pipe.SafePipeHandle,out var pid)||pid!=options.ProcessId)return Block("native pipe server PID mismatch");
            using var writer=new StreamWriter(pipe,new UTF8Encoding(false),1024,true){AutoFlush=true};using var reader=new StreamReader(pipe,Encoding.UTF8,false,4096,true);
            var request=JsonSerializer.Serialize(new {options.DocumentPath,Operation="fixture.room.ab",UniqueId=options.DocumentIdentity,Metres=Array.Empty<double>(),Batch=(object?)null,Checkpoint=(object?)null});
            dispatched=true;await writer.WriteLineAsync(request.AsMemory(),timeout.Token);
            var raw=new StringBuilder();var buffer=new char[1];
            while(await reader.ReadAsync(buffer.AsMemory(),timeout.Token)>0) {if(buffer[0]=='\n')break;if(raw.Length>=4*1024*1024)throw new IOException("native response exceeds 4 MiB; completion UNKNOWN");raw.Append(buffer[0]);}
            try {
                using var response=JsonDocument.Parse(raw.ToString());
                if(response.RootElement.TryGetProperty("status",out var status)&&status.GetString() is "failed" or "blocked") {
                    var reason=response.RootElement.TryGetProperty("code",out var code)?code.ToString():"native operation rejected";
                    return Block("native execution "+status.GetString()+": "+reason);
                }
            }catch(JsonException) {return Block("invalid native response JSON; completion UNKNOWN; do not automatically retry");}
            var nativeFailure=ParseNativeFailure(raw.ToString(),build);if(nativeFailure!=null)return Block("native execution errors: "+nativeFailure);
            var envelope=ParseNativeEnvelope(raw.ToString(),build);if(envelope==null)return Block("invalid native envelope protocol/metadata/checks/metrics");
            if(envelope.Errors.Length>0)return Block("native execution errors: "+string.Join("; ",envelope.Errors));
            if(envelope.Artifacts.Length==0||envelope.Artifacts.Distinct().Count()!=envelope.Artifacts.Length||!envelope.Artifacts.Contains(Path.GetFileName(options.DocumentPath)))return Block("native fixture artifact missing");
            foreach(var name in envelope.Artifacts) {if(string.IsNullOrWhiteSpace(name)||name is "." or ".."||name!=Path.GetFileName(name)||name.Contains('/')||name.Contains('\\')||!File.Exists(Path.Combine(options.RunDirectory,name)))return Block("invalid/missing native artifact");NoLinks(Path.Combine(options.RunDirectory,name));}
            var b=envelope.Observations.TestB!;foreach(var snapshot in new[]{envelope.Observations.TestA,b.Before,b.After,b.Retry,b.Rollback})if(snapshot==null||!SamePath(snapshot.NativeFile,options.DocumentPath))return Block("native readback document path mismatch");
            var report=EvaluateOffline(envelope.Observations);if(report.Backend!="revit")return Block("malformed native observations");
            var accepted=envelope.Checks.All(x=>x.Outcome=="pass")&&report.ExitCode==0;
            foreach(var pair in envelope.Metrics)report.Metrics[pair.Key]=pair.Value;
            return report with {VerificationState="verified-in-revit",BackendVersion=build,BackendChecks=envelope.Checks,BackendAcceptance=accepted,ExitCode=accepted?0:envelope.Checks.Any(x=>x.Outcome=="fail")||report.Counts.Failed>0?1:2,Outcome=accepted?"pass":"fail",TimingScope="actual isolated native execution and reopened readback; independent C# oracle",Gaps=[..report.Gaps,..envelope.Checks.Where(x=>x.Outcome!="pass").Select(x=>x.Id+": "+x.Reason)]};
        }catch(OperationCanceledException) {return Block(dispatched?"native completion UNKNOWN after dispatch; do not automatically retry":"native connection cancelled/timed out before dispatch");}
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or JsonException or KeyNotFoundException or NullReferenceException or System.ComponentModel.Win32Exception) {return Block((dispatched?"native completion UNKNOWN; do not automatically retry: ":"native fixture prerequisite rejected: ")+ex.Message);}
    }
}
