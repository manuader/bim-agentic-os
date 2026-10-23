using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
namespace Bim.Core;

// Width/depth describe clear interior dimensions. The west exterior face at AnchorXMetres
// and south exterior face at AnchorYMetres are invariant during width/depth revisions.
public record RoomIntent(Guid RoomId, double WidthMetres, double DepthMetres,
    double HeightMetres, double ThicknessMetres, double AnchorXMetres, double AnchorYMetres,
    string Units, Provenance Provenance);

public static class RoomCompiler
{
    public static Guid Id(Guid roomId, string role) => new(SHA256.HashData(Encoding.UTF8.GetBytes(roomId.ToString("D")+":"+role)).Take(16).ToArray());
    public static Project Compile(Guid projectId, int revision, RoomIntent intent)
    {
        if(projectId==Guid.Empty || revision<0 || intent.RoomId==Guid.Empty || intent.Units!="m" || intent.Provenance is null || string.IsNullOrWhiteSpace(intent.Provenance.Source) || string.IsNullOrWhiteSpace(intent.Provenance.Approval)) throw new ArgumentException("invalid_room_identity_units_provenance");
        var values=new[]{intent.WidthMetres,intent.DepthMetres,intent.HeightMetres,intent.ThicknessMetres,intent.AnchorXMetres,intent.AnchorYMetres};
        if(values.Any(v=>!double.IsFinite(v)) || values.Take(4).Any(v=>v<=0) || intent.ThicknessMetres>=Math.Min(intent.WidthMetres,intent.DepthMetres)/2) throw new ArgumentException("invalid_room_constraints");
        var level=Id(intent.RoomId,"level"); var walls=new[]{"south","east","north","west"}.Select(r=>Id(intent.RoomId,r)).ToArray();
        double t=intent.ThicknessMetres, x=intent.AnchorXMetres+t/2, y=intent.AnchorYMetres+t/2, right=x+intent.WidthMetres+t, top=y+intent.DepthMetres+t;
        if(new[]{x,y,right,top,intent.AnchorXMetres+t,intent.AnchorYMetres+t}.Any(v=>!double.IsFinite(v)) || right<=x || top<=y) throw new ArgumentException("invalid_room_coordinate_range");
        Element E(string role,string kind,double[] geometry,Guid[] dependencies)=>new(Id(intent.RoomId,role),kind,geometry,dependencies,intent.Provenance);
        Element W(string role,double ax,double ay,double bx,double by)=>E(role,"wall",[ax,ay,bx,by,intent.HeightMetres,t],[level]);
        return new(projectId,revision,[E("level","level",[0],[]),W("south",x,y,right,y),W("east",right,y,right,top),W("north",right,top,x,top),W("west",x,top,x,y),
            E("space","space",[intent.AnchorXMetres+t,intent.AnchorYMetres+t,intent.WidthMetres,intent.DepthMetres],walls),
            E("plan","plan",[],[level,..walls,Id(intent.RoomId,"space")]),
            E("schedule","schedule",[],[Id(intent.RoomId,"space")]),
            E("sheet","sheet",[],[Id(intent.RoomId,"plan"),Id(intent.RoomId,"schedule")])]);
    }
}

public static class NativeBatchContract
{
    public static ValidationIssue[] Preflight(Project before, Project after, ExecutionPlan plan) => Preflight(before, after, plan, CapabilityRegistry.NativeDefault);
    // Native kind support comes from the capability registry (capabilities.json or its built-in mirror).
    public static ValidationIssue[] Preflight(Project before, Project after, ExecutionPlan plan, CapabilityRegistry capabilities)
    {
        var issues=new List<ValidationIssue>();
        if(plan.Operations.Any(o=>o.Action=="delete")) issues.Add(new("unsupported_native_delete",null,"Deletion may cascade to untracked native dependents"));
        try {
            var expected=Compiler.Diff(before,after);
            if(JsonSerializer.Serialize(expected)!=JsonSerializer.Serialize(plan)) issues.Add(new("plan_mismatch",null,"Plan must equal deterministic diff including hashes, operations and dirty closure"));
        } catch(Exception e) when(e is ArgumentException or InvalidOperationException) { issues.Add(new("invalid_plan",null,e.Message)); }
        foreach(var project in new[]{before,after}) foreach(var e in project.Elements) {
            if(!capabilities.SupportsNativeKind(e.Kind)) issues.Add(new("unsupported_native_kind",e.Id,"Native authoring/readback unavailable for "+e.Kind));
            if(e.Kind=="level" && (!GeometryCodec.HasExpectedArity(e) || e.Dependencies.Length!=0)) issues.Add(new("invalid_level",e.Id,"Level requires elevation in metres and no dependencies"));
            if(e.Kind=="wall" && (e.Dependencies.Length!=1 || !project.Elements.Any(l=>l.Id==e.Dependencies[0] && l.Kind=="level"))) issues.Add(new("invalid_wall_level",e.Id,"Wall requires exactly one level"));
        }
        return issues.ToArray();
    }
}
public record NativeBatchRequest(string DocumentIdentity, string IdempotencyKey, Project Before, Project After, ExecutionPlan Plan);
