using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bim.Evaluation;

// Wall geometry is the observed interior boundary segment [x1,y1,x2,y2]; openings use the host-face projection [x,y].
// Observation contracts are isolated from BIM-IR and native RPC contracts. All lengths are millimetres.
public sealed record ElementObservation(string LogicalId, string NativeId, string Category, string? HostId, string[] References, double[] Geometry);
public sealed record RoomObservation(double ClearWidthMm, double ClearDepthMm, double WallThicknessMm, double WallHeightMm,
    double DoorWidthMm, string Units, ElementObservation[] Elements, double DimensionWidthMm, double ScheduleAreaM2,
    bool Saved, bool Reopened, string NativeFile);
public sealed record RevisionObservation(RoomObservation Before, RoomObservation After, RoomObservation Retry, RoomObservation Rollback,
    string AnchorLogicalId, string[] AllowedChangedIds, string[] SemanticChangedIds, string[] UpdatedDocumentIds,
    string OperationId, string RetryOperationId, bool ForcedFailureObserved, bool ValidationPassed,
    string RevisionBefore, string RevisionAfter, string RetryRevision, string RollbackRevision);
public sealed record BackendObservations(string Backend, string Source, RoomObservation? TestA, RevisionObservation? TestB);
public sealed record Metric(double? Value, string Reason);
public sealed record Counts(int Passed, int Failed, int Blocked, int Unsupported);
public sealed record CriterionResult(string Id, string Status, string Reason, double? Expected = null, double? Actual = null, double? Tolerance = null);
public sealed record CaseResult(string Id, string Outcome, Counts Counts, CriterionResult[] Criteria);
public sealed record EvaluationReport(string VerificationState, string Outcome, string Scope, string Backend, string BackendVersion, string Source,
    string TimingScope, Counts Counts, Dictionary<string,Metric> Metrics, CaseResult[] Cases, string[] Gaps,
    [property: JsonIgnore] int ExitCode) { public bool ProductAcceptance => ExitCode==0 && VerificationState is "integration-tested (ifc)" or "verified-in-revit"; public string EvidenceOrigin => VerificationState=="unit-tested" ? "offline-observations" : "backend-prerequisite-inspection"; }

public static class EvaluationService
{
    public static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy=JsonNamingPolicy.SnakeCaseLower, UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow, WriteIndented=true };
    public static EvaluationReport ParseOffline(string json)
    {
        try { var value=JsonSerializer.Deserialize<BackendObservations>(json,JsonOptions); return value==null?Malformed("null observations"):EvaluateOffline(value); }
        catch(JsonException ex) { return Malformed("invalid_json: "+ex.Message); }
    }
    public static readonly string[] MetricNames = ["intent_parse_ms","semantic_query_ms","solver_ms","diff_ms","runtime_wait_ms","revit_execute_ms","revit_regeneration_ms","native_readback_ms","validation_ms","documentation_ms","total_ms","llm_calls","model_elements_read","model_elements_written","transaction_count","unintended_element_changes"];
    private static readonly string[] ACatalog = ["level","clear_width_mm","clear_depth_mm","wall_count","wall_thickness_mm","wall_height_mm","door_count","door_width_mm","window_count","room_boundary","dimensions","plan","view_3d","schedule","sheet","categories","hosting","units","references","geometry_consistency","identities","native_save","native_reopen"];
    private static readonly string[] BCatalog = ["requires_A","post_revision_acceptance","baseline_matches_A","width_delta_mm","exterior_anchor","unaffected_ids","dependent_hosts","documentation_updates","semantic_diff","idempotency_key","retry_no_duplicates","retry_revision","forced_failure","rollback_snapshot","rollback_revision","validation","unintended_changes"];
    private static readonly HashSet<string> NativeOnly = ["dimensions","plan","view_3d","schedule","sheet","native_save","native_reopen","documentation_updates"];

    // Caller-supplied observations cannot confer integration provenance, irrespective of Backend or Source strings.
    public static EvaluationReport EvaluateOffline(BackendObservations observations)
    {
        var timer = Stopwatch.StartNew();
        try { return Evaluate(observations, "unit-tested", timer); }
        catch (Exception ex) when (ex is ArgumentException or NullReferenceException or InvalidOperationException) { return Malformed("invalid_observations: " + ex.Message); }
    }

    // A real executor port returns observations only after isolated authoring/readback. No historical artifacts are loaded here.
    public static async Task<EvaluationReport> ExecuteAsync(string backend, CancellationToken cancellationToken = default)
    {
        var timer = Stopwatch.StartNew();
        IBackendExecutor executor = backend switch { "ifc" => new IfcExecutor(), "revit" => new RevitExecutor(), _ => new UnsupportedExecutor(backend) };
        var execution = await executor.ExecuteAsync(cancellationToken);
        var report = Evaluate(new(backend is "ifc" or "revit" ? backend : "revit", execution.Source, null, null), "blocked", timer);
        return report with { Backend=backend, BackendVersion = execution.Version, Gaps = execution.Gaps };
    }

    public static EvaluationReport Malformed(string reason)
    {
        var criterion = new CriterionResult("input", "failed", reason);
        return new("unit-tested","fail","full Test A/B", "unknown","unknown","invalid input","orchestration only; not architectural delivery",new(0,1,0,0),UnknownMetrics(),[new("input","fail",new(0,1,0,0),[criterion])],[reason],1);
    }

    private static EvaluationReport Evaluate(BackendObservations o, string state, Stopwatch timer)
    {
        if (o.Backend is not ("ifc" or "revit") || string.IsNullOrWhiteSpace(o.Source)) return Malformed("unsupported_backend_or_missing_source");
        var a = o.TestA == null ? Missing("A", ACatalog) : CheckA(o.TestA, o.Backend);
        if (o.Backend == "ifc") a = WithUnsupported(a);
        var b = o.TestB == null ? Missing("B", BCatalog) : CheckB(o.TestB, o.TestA, a.Outcome == "pass", o.Backend);
        if (o.Backend == "ifc") b = WithUnsupported(b);
        var counts = Count(a.Criteria.Concat(b.Criteria));
        var exit = counts.Failed > 0 ? 1 : counts.Blocked + counts.Unsupported > 0 ? 2 : 0;
        var metrics = UnknownMetrics(); metrics["total_ms"] = new(timer.Elapsed.TotalMilliseconds,"observed evaluator orchestration only; excludes full architectural delivery");
        return new(state,exit==0?"pass":"fail","full Test A/B",o.Backend,"not observed",o.Source,
            "orchestration only; not architectural delivery",counts,metrics,[a,b],a.Criteria.Concat(b.Criteria).Where(x=>x.Status!="passed").Select(x=>x.Id+": "+x.Reason).ToArray(),exit);
    }
    private static Dictionary<string,Metric> UnknownMetrics() => MetricNames.ToDictionary(x=>x,_=>new Metric(null,"not instrumented by a real backend execution"));
    private static CaseResult Missing(string id, string[] catalogue) => Result(id,catalogue.Select(x=>new CriterionResult(x,"blocked","mandatory backend observation missing")).ToList());
    private static CaseResult WithUnsupported(CaseResult c) => Result(c.Id,c.Criteria.Select(x=>NativeOnly.Contains(x.Id)?x with {Status="unsupported",Reason="IFC cannot prove native Revit annotation/view/sheet/RVT acceptance"}:x).ToList());
    private static Counts Count(IEnumerable<CriterionResult> c) { var a=c.ToArray(); return new(a.Count(x=>x.Status=="passed"),a.Count(x=>x.Status=="failed"),a.Count(x=>x.Status=="blocked"),a.Count(x=>x.Status=="unsupported")); }
    private static CaseResult Result(string id, List<CriterionResult> c) { var n=Count(c); return new(id,n.Failed+n.Blocked+n.Unsupported==0?"pass":"fail",n,c.ToArray()); }
    private static void Bool(List<CriterionResult> c,string id,bool value,string reason) => c.Add(new(id,value?"passed":"failed",value?"measured observation satisfies criterion":reason));
    private static void Number(List<CriterionResult> c,string id,double expected,double actual,double tolerance=0) => c.Add(new(id,double.IsFinite(actual)&&Math.Abs(expected-actual)<=tolerance?"passed":"failed","measured numerical comparison",expected,double.IsFinite(actual)?actual:null,tolerance));
    private static bool IdsValid(ElementObservation[] e) => e.All(x=>!string.IsNullOrWhiteSpace(x.LogicalId)&&!string.IsNullOrWhiteSpace(x.NativeId)) && e.Select(x=>x.LogicalId).Distinct().Count()==e.Length && e.Select(x=>x.NativeId).Distinct().Count()==e.Length;
    private static bool SameElement(ElementObservation x,ElementObservation y) => x.LogicalId==y.LogicalId&&x.NativeId==y.NativeId&&x.Category==y.Category&&x.HostId==y.HostId&&x.References.SequenceEqual(y.References)&&x.Geometry.SequenceEqual(y.Geometry);
    private static bool Same(RoomObservation x,RoomObservation y) => x.ClearWidthMm==y.ClearWidthMm&&x.ClearDepthMm==y.ClearDepthMm&&x.WallThicknessMm==y.WallThicknessMm&&x.WallHeightMm==y.WallHeightMm&&x.DoorWidthMm==y.DoorWidthMm&&x.Units==y.Units&&x.DimensionWidthMm==y.DimensionWidthMm&&x.ScheduleAreaM2==y.ScheduleAreaM2&&x.Saved==y.Saved&&x.Reopened==y.Reopened&&x.NativeFile==y.NativeFile&&IdsValid(x.Elements)&&IdsValid(y.Elements)&&x.Elements.Length==y.Elements.Length&&x.Elements.All(a=>y.Elements.Any(b=>SameElement(a,b)));
    private static bool SetEquals(string[] a,IEnumerable<string> b) => a.Length==a.Distinct().Count()&&a.Order().SequenceEqual(b.Order());
    private static bool GeometryValid(RoomObservation room)
    {
        var walls=room.Elements.Where(x=>x.Category=="Wall").ToArray();
        if(walls.Length!=4 || walls.Any(x=>x.Geometry.Length!=4||!x.Geometry.All(double.IsFinite))) return false;
        var minX=walls.Min(x=>Math.Min(x.Geometry[0],x.Geometry[2])); var maxX=walls.Max(x=>Math.Max(x.Geometry[0],x.Geometry[2]));
        var minY=walls.Min(x=>Math.Min(x.Geometry[1],x.Geometry[3])); var maxY=walls.Max(x=>Math.Max(x.Geometry[1],x.Geometry[3]));
        if(maxX-minX!=room.ClearWidthMm||maxY-minY!=room.ClearDepthMm||room.ClearWidthMm<=0||room.ClearDepthMm<=0) return false;
        double[][] edges=[[minX,minY,maxX,minY],[maxX,minY,maxX,maxY],[maxX,maxY,minX,maxY],[minX,maxY,minX,minY]];
        if(!edges.All(edge=>walls.Count(w=>w.Geometry.SequenceEqual(edge)||w.Geometry.SequenceEqual(new[]{edge[2],edge[3],edge[0],edge[1]}))==1)) return false;
        return room.Elements.Where(x=>x.Category is "Door" or "Window").All(x=> {
            var host=walls.SingleOrDefault(w=>w.LogicalId==x.HostId); if(host==null||x.Geometry.Length!=2||!x.Geometry.All(double.IsFinite))return false;
            var g=host.Geometry;var p=x.Geometry;
            return (g[0]==g[2]&&p[0]==g[0]&&p[1]>=Math.Min(g[1],g[3])&&p[1]<=Math.Max(g[1],g[3])) || (g[1]==g[3]&&p[1]==g[1]&&p[0]>=Math.Min(g[0],g[2])&&p[0]<=Math.Max(g[0],g[2]));
        });
    }
    private static CaseResult CheckA(RoomObservation r,string backend,double expectedWidth=5000)
    {
        var c=new List<CriterionResult>(); var e=r.Elements; var byId=e.GroupBy(x=>x.LogicalId).ToDictionary(x=>x.Key,x=>x.First());
        ElementObservation[] Cat(string s)=>e.Where(x=>x.Category==s).ToArray();
        Number(c,"level",1,Cat("Level").Length); Number(c,"clear_width_mm",expectedWidth,r.ClearWidthMm); Number(c,"clear_depth_mm",4000,r.ClearDepthMm);
        Number(c,"wall_count",4,Cat("Wall").Length); Number(c,"wall_thickness_mm",200,r.WallThicknessMm); Number(c,"wall_height_mm",3000,r.WallHeightMm);
        Number(c,"door_count",1,Cat("Door").Length); Number(c,"door_width_mm",900,r.DoorWidthMm); Number(c,"window_count",2,Cat("Window").Length);
        Bool(c,"room_boundary",Cat("Room").Length==1 && SetEquals(Cat("Room")[0].References,Cat("Wall").Select(x=>x.LogicalId)),"Room must reference all four boundary walls");
        Bool(c,"dimensions",Cat("Dimension").Length>0&&r.DimensionWidthMm==r.ClearWidthMm&&Cat("Dimension").All(x=>x.References.Length>=2&&x.References.All(z=>byId.TryGetValue(z,out var wall)&&wall.Category=="Wall")),"native dimension value and wall references required");
        Bool(c,"plan",Cat("Plan").Length>0&&Cat("Plan").All(x=>x.References.Any(z=>byId.TryGetValue(z,out var d)&&d.Category=="Dimension")),"plan and associated dimensions required");
        Bool(c,"view_3d",Cat("View3D").Length>0&&Cat("View3D").All(x=>x.References.Length>0),"3D view references required");
        Bool(c,"schedule",Cat("Schedule").Length>0&&Math.Abs(r.ScheduleAreaM2-r.ClearWidthMm*r.ClearDepthMm/1e6)<=1e-9&&Cat("Schedule").All(x=>x.References.Any(z=>byId.TryGetValue(z,out var room)&&room.Category=="Room")),"schedule area and Room reference required");
        Bool(c,"sheet",Cat("Sheet").Length>0&&Cat("Sheet").All(x=>new[]{"Plan","Schedule"}.All(cat=>x.References.Any(z=>byId.TryGetValue(z,out var doc)&&doc.Category==cat))),"sheet must place plan and schedule");
        Bool(c,"categories",e.All(x=>new[]{"Level","Wall","Door","Window","Room","Dimension","Plan","View3D","Schedule","Sheet"}.Contains(x.Category)),"unexpected/untyped category");
        Bool(c,"hosting",e.Where(x=>x.Category is "Door" or "Window").All(x=>x.HostId!=null&&byId.TryGetValue(x.HostId,out var h)&&h.Category=="Wall")&&e.Where(x=>x.Category is "Wall" or "Room" or "Plan").All(x=>x.HostId!=null&&byId.TryGetValue(x.HostId,out var h)&&h.Category=="Level"),"host categories must resolve");
        Bool(c,"units",r.Units=="mm","explicit millimetre observation contract required");
        Bool(c,"references",e.All(x=>x.References.All(byId.ContainsKey)&&x.Geometry.All(double.IsFinite)),"unresolved references or nonfinite geometry");
        Bool(c,"geometry_consistency",GeometryValid(r),"wall inner-face rectangle and hosted opening positions must match measured clear dimensions");
        Bool(c,"identities",IdsValid(e),"logical/native IDs missing or duplicate");
        Bool(c,"native_save",r.Saved&&Path.GetExtension(r.NativeFile).Equals(backend=="ifc"?".ifc":".rvt",StringComparison.OrdinalIgnoreCase),"native save evidence required");
        Bool(c,"native_reopen",r.Reopened,"saved model must be reopened and inspected");
        return Result("A",c);
    }
    private static CaseResult CheckB(RevisionObservation r,RoomObservation? a,bool aPass,string backend)
    {
        var c=new List<CriterionResult>();
        c.Add(new("requires_A",aPass?"passed":"blocked",aPass?"Test A passed":"Test B requires complete passing Test A"));
        Bool(c,"baseline_matches_A",a!=null&&Same(a,r.Before),"B must start from exact Test A observations");
        Number(c,"width_delta_mm",400,r.After.ClearWidthMm-r.Before.ClearWidthMm);
        var before=r.Before.Elements;var after=r.After.Elements;
        var valid=IdsValid(before)&&IdsValid(after);
        var actual=valid ? before.Where(x=>!after.Any(y=>SameElement(x,y))).Select(x=>x.LogicalId).Concat(after.Where(x=>!before.Any(y=>y.LogicalId==x.LogicalId)).Select(x=>x.LogicalId)).ToArray() : ["invalid-identities"];
        var anchor=before.SingleOrDefault(x=>x.LogicalId==r.AnchorLogicalId);
        Bool(c,"exterior_anchor",anchor!=null&&anchor.Category=="Wall"&&anchor.Geometry.Length==4&&after.Any(x=>SameElement(anchor,x))&&(anchor.Geometry[0]==0&&anchor.Geometry[2]==0),"chosen west exterior anchor must remain fixed");
        Bool(c,"unaffected_ids",valid&&before.Length==after.Length&&before.All(x=>after.Any(y=>x.LogicalId==y.LogicalId&&x.NativeId==y.NativeId)),"native/logical IDs must survive revision");
        var afterA=CheckA(r.After,backend,5400);
        Bool(c,"post_revision_acceptance",afterA.Outcome=="pass","full room acceptance must remain satisfied after width correction");
        Bool(c,"dependent_hosts",afterA.Criteria.Single(x=>x.Id=="hosting").Status=="passed"&&before.Where(x=>x.Category is "Door" or "Window").All(x=>after.Any(y=>y.LogicalId==x.LogicalId&&y.HostId==x.HostId))&&r.After.ClearDepthMm==r.Before.ClearDepthMm&&r.After.WallThicknessMm==r.Before.WallThicknessMm&&r.After.WallHeightMm==r.Before.WallHeightMm,"dependent hosting and protected parameters required");
        Bool(c,"documentation_updates",r.After.DimensionWidthMm==r.After.ClearWidthMm&&Math.Abs(r.After.ScheduleAreaM2-r.After.ClearWidthMm*r.After.ClearDepthMm/1e6)<=1e-9&&SetEquals(r.UpdatedDocumentIds,after.Where(x=>x.Category is "Dimension" or "Plan" or "Schedule" or "Sheet").Select(x=>x.LogicalId)),"all affected dimensions/plan/schedule/sheet must update");
        Bool(c,"semantic_diff",valid&&SetEquals(r.SemanticChangedIds,actual),"semantic diff must match measured changed elements exactly");
        Bool(c,"idempotency_key",!string.IsNullOrWhiteSpace(r.OperationId)&&r.OperationId==r.RetryOperationId,"retry requires same nonempty operation id");
        Bool(c,"retry_no_duplicates",Same(r.After,r.Retry),"retry must preserve complete snapshot and identities");
        Bool(c,"retry_revision",!string.IsNullOrWhiteSpace(r.RevisionBefore)&&r.RevisionBefore!=r.RevisionAfter&&r.RevisionAfter==r.RetryRevision,"revision advances once only");
        Bool(c,"forced_failure",r.ForcedFailureObserved,"forced failure must be observed");
        Bool(c,"rollback_snapshot",Same(r.Before,r.Rollback),"forced-failure rollback restores full original snapshot");
        Bool(c,"rollback_revision",r.RollbackRevision==r.RevisionBefore,"rollback restores revision");
        Bool(c,"validation",r.ValidationPassed,"post-revision validation required");
        Bool(c,"unintended_changes",valid&&SetEquals(r.AllowedChangedIds,actual)&&actual.All(id=>before.Any(x=>x.LogicalId==id&&x.Category is "Wall" or "Door" or "Window"))&&r.After.NativeFile==r.Before.NativeFile&&r.After.Units==r.Before.Units,"changed set must equal permitted set; protected file/units unchanged");
        return Result("B",c);
    }
}
internal sealed record BackendExecution(string Source,string Version,string[] Gaps);
internal interface IBackendExecutor { Task<BackendExecution> ExecuteAsync(CancellationToken token); }
internal sealed class IfcExecutor : IBackendExecutor { public Task<BackendExecution> ExecuteAsync(CancellationToken token) { token.ThrowIfCancellationRequested(); return Task.FromResult(new BackendExecution("prerequisite inspection; no execution","unavailable",["IfcOpenShell absent; dependency approval pending (ADR0006)","IFC cannot satisfy mandatory native Revit annotations/views/sheets/RVT save-reopen"])); } }
internal sealed class RevitExecutor : IBackendExecutor { public Task<BackendExecution> ExecuteAsync(CancellationToken token) { token.ThrowIfCancellationRequested(); return Task.FromResult(new BackendExecution("capability inspection; no model writes","not observed",["Existing native executor supports level/wall primitives only","Missing door, windows, Room, dimensions, views, schedule and sheet authoring/readback","Full Test B dependent/document updates and rollback fixture unavailable; historical primitive evidence excluded"])); } }
internal sealed class UnsupportedExecutor(string backend) : IBackendExecutor { public Task<BackendExecution> ExecuteAsync(CancellationToken token)=>Task.FromResult(new BackendExecution("unsupported backend: "+backend,"unavailable",["unsupported backend"])); }



