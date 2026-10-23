using Bim.Core;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
namespace Bim.Ifc;

// Adapter protocol only. Existing Core serialized records remain unchanged.
public sealed record IfcCompiledRequest(int ProtocolVersion,string Units,Guid RoomId,string OperationId,
    string BeforeJson,string AfterJson,string CommittedAfterJson,string PlanJson,string PlanSha256,
    Dictionary<string,Guid> LogicalRoles,Guid[] UnsupportedDocumentationIds,string JoinPolicy);

public static class IfcCompiler
{
    public const string JoinPolicy="horizontal-over-corners_vertical-between-inner-faces";
    static readonly string[] Roles=["level","south","east","north","west","space","plan","schedule","sheet"];
    public static string Sha256(string value)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    static string Json<T>(T value)=>JsonSerializer.Serialize(value);
    public static IfcCompiledRequest EvaluationRequest()=>Compile(
        Guid.Parse("66ef39da-f724-4328-b055-c33e78dbb2ed"),0,
        new(Guid.Parse("a71c442d-5181-4b11-9663-a4257ac1261f"),5,4,3,.2,-.2,-.2,"m",new("IFC evaluation","fixed evaluation fixture")),5.4,"resize-400");
    public static IfcCompiledRequest Compile(Guid projectId,int revision,RoomIntent before,double afterWidth,string operationId)
    {
        var a=RoomCompiler.Compile(projectId,revision,before);
        var b=RoomCompiler.Compile(projectId,revision,before with {WidthMetres=afterWidth});
        return Export(a,b,Compiler.Diff(a,b),before.RoomId,operationId);
    }
    public static IfcCompiledRequest Export(Project before,Project after,ExecutionPlan plan,Guid roomId,string operationId)
    {
        var logical=Roles.Take(6).ToDictionary(role=>role=="space"?"room":role,role=>RoomCompiler.Id(roomId,role));
        var planJson=Json(plan);
        var result=new IfcCompiledRequest(1,"m",roomId,operationId,Json(before),Json(after),Json(after with {Revision=checked(after.Revision+1)}),planJson,Sha256(planJson),logical,
            Roles.Skip(6).Select(role=>RoomCompiler.Id(roomId,role)).ToArray(),JoinPolicy);
        Validate(result);
        return result;
    }
    public static void Validate(IfcCompiledRequest request)
    {
        try {
            if(request.ProtocolVersion!=1||request.Units!="m"||request.RoomId==Guid.Empty||string.IsNullOrWhiteSpace(request.OperationId)||request.JoinPolicy!=JoinPolicy)throw new ArgumentException("unsupported_ifc_request");
            var before=JsonSerializer.Deserialize<Project>(request.BeforeJson)??throw new ArgumentException("missing_before");
            var after=JsonSerializer.Deserialize<Project>(request.AfterJson)??throw new ArgumentException("missing_after");
            var plan=JsonSerializer.Deserialize<ExecutionPlan>(request.PlanJson)??throw new ArgumentException("missing_plan");
            if(Sha256(request.PlanJson)!=request.PlanSha256||Sha256(request.BeforeJson)!=plan.BeforeHash||Sha256(request.AfterJson)!=plan.AfterHash||Json(Compiler.Diff(before,after))!=request.PlanJson)throw new InvalidOperationException("plan_mismatch");
            if(Json(after with {Revision=checked(after.Revision+1)})!=request.CommittedAfterJson)throw new ArgumentException("invalid_committed_snapshot");
            var a=Rectangle(before,request.RoomId);var b=Rectangle(after,request.RoomId);
            if(a != (b with {WidthMetres=a.WidthMetres}))throw new ArgumentException("unsupported_ifc_change_only_width");
            var roles=Roles.Take(6).ToDictionary(role=>role=="space"?"room":role,role=>RoomCompiler.Id(request.RoomId,role));
            if(request.LogicalRoles.Count!=roles.Count||roles.Any(pair=>!request.LogicalRoles.TryGetValue(pair.Key,out var id)||id!=pair.Value)||!request.UnsupportedDocumentationIds.SequenceEqual(Roles.Skip(6).Select(role=>RoomCompiler.Id(request.RoomId,role))))throw new ArgumentException("invalid_logical_roles");
        } catch(JsonException error) {throw new ArgumentException("malformed_ifc_request",error);}
          catch(NullReferenceException error) {throw new ArgumentException("malformed_ifc_request",error);}
          catch(IndexOutOfRangeException error) {throw new ArgumentException("malformed_ifc_geometry",error);}
    }
    static RoomIntent Rectangle(Project project,Guid roomId)
    {
        if(project.Elements.Length!=9||Compiler.Validate(project).Length!=0)throw new ArgumentException("unsupported_ifc_ir");
        var space=project.Elements.Single(e=>e.Id==RoomCompiler.Id(roomId,"space"));
        var west=project.Elements.Single(e=>e.Id==RoomCompiler.Id(roomId,"west"));
        // Arity and layout come from the core geometry codec; the wire packet stays raw metres.
        if(space.Kind!=GeometryCodec.Space||west.Kind!=GeometryCodec.Wall||GeometryCodec.Problem(space) is not null||GeometryCodec.Problem(west) is not null)throw new ArgumentException("unsupported_ifc_rectangle");
        var s=(SpaceGeometry)GeometryCodec.Decode(space);var w=(WallGeometry)GeometryCodec.Decode(west);
        var intent=new RoomIntent(roomId,s.Width.Metres,s.Depth.Metres,w.Height.Metres,w.Thickness.Metres,s.InteriorCorner.X.Metres-w.Thickness.Metres,s.InteriorCorner.Y.Metres-w.Thickness.Metres,LengthUnits.Metres,space.Provenance);
        if(intent.WidthMetres<3||intent.DepthMetres<3||intent.HeightMetres<2.1||intent.ThicknessMetres!=.2)throw new ArgumentException("fixture_extras_require_minimum_room_and_200mm_walls");
        if(Json(RoomCompiler.Compile(project.Id,project.Revision,intent))!=Json(project))throw new ArgumentException("unsupported_ifc_noncanonical_room");
        return intent;
    }
}
