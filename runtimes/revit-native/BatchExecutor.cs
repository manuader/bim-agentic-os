using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using Bim.Core;
using System.Text.Json;
using CoreElement = Bim.Core.Element;
namespace Bim.Revit;

// Only invoked by App.Execute on Revit's ExternalEvent thread. The persistent receipt,
// intended snapshot and logical/native map are committed in the same transaction group.
public static class BatchExecutor
{
    static readonly Guid SchemaId=new("9f5a8262-a6b7-4817-8482-2fdb0359da87");
    public record State(Project Project, Dictionary<Guid,string> Map, Dictionary<string,string> Receipts);
    static Schema StorageSchema()
    {
        var existing=Schema.Lookup(SchemaId); if(existing!=null)return existing;
        var builder=new SchemaBuilder(SchemaId); builder.SetSchemaName("BimAgenticRoomSliceState"); builder.SetReadAccessLevel(AccessLevel.Public); builder.SetWriteAccessLevel(AccessLevel.Public); builder.AddSimpleField("Payload",typeof(string)); return builder.Finish();
    }
    static double U(double m)=>UnitUtils.ConvertToInternalUnits(m,UnitTypeId.Meters);
    static bool Near(double actual,double metres)=>Math.Abs(actual-U(metres))<1e-6;
    static void Readback(Document d,Project project,Dictionary<Guid,string> map)
    {
        foreach(var logical in project.Elements) {
            if(!map.TryGetValue(logical.Id,out var id))throw new InvalidOperationException("native_mapping_missing");
            var native=d.GetElement(id) ?? throw new InvalidOperationException("native_element_missing"); var g=logical.GeometryMetres;
            if(logical.Kind=="level") {if(native is not Level l || !Near(l.Elevation,g[0]))throw new InvalidOperationException("level_readback_mismatch");}
            else if(logical.Kind=="wall") {
                if(native is not Wall w || w.Location is not LocationCurve location || location.Curve is not Line)throw new InvalidOperationException("wall_readback_kind");
                var a=location.Curve.GetEndPoint(0); var b=location.Curve.GetEndPoint(1);
                if(!Near(a.X,g[0]) || !Near(a.Y,g[1]) || !Near(b.X,g[2]) || !Near(b.Y,g[3]) || !Near(w.get_Parameter(BuiltInParameter.WALL_USER_HEIGHT_PARAM).AsDouble(),g[4]) || !Near(w.Width,g[5]) || w.LevelId!=d.GetElement(map[logical.Dependencies[0]]).Id)throw new InvalidOperationException("wall_readback_mismatch");
                var level=(Level)d.GetElement(w.LevelId);
                if(Math.Abs(a.Z-level.Elevation)>1e-6 || Math.Abs(b.Z-level.Elevation)>1e-6)throw new InvalidOperationException("wall_base_readback_mismatch");
            } else throw new NotSupportedException("unsupported_native_kind");
        }
    }
    public static State VerifyReceipt(Document d,NativeBatchRequest request,FixtureCheckpointRequest checkpoint)
    {
        if(request.DocumentIdentity!=d.ProjectInformation.UniqueId) throw new InvalidOperationException("document_identity_conflict");
        var schema=Schema.Lookup(SchemaId) ?? throw new InvalidOperationException("checkpoint_native_state_missing");
        var stores=new FilteredElementCollector(d).OfClass(typeof(DataStorage)).Cast<DataStorage>().Where(s=>s.GetEntity(schema).IsValid()).ToArray();
        if(stores.Length!=1) throw new InvalidOperationException("checkpoint_native_state_missing_or_ambiguous");
        var state=JsonSerializer.Deserialize<State>(stores[0].GetEntity(schema).Get<string>("Payload")) ?? throw new InvalidOperationException("invalid_native_state");
        BatchReceiptGuard.Verify(request,state.Project,state.Map,state.Receipts,checkpoint.ExpectedRevision,checkpoint.ExpectedMapping);
        Readback(d,state.Project,state.Map);
        return state;
    }
    public static string Run(Document d,NativeBatchRequest request)
    {
        if(d.IsFamilyDocument || d.IsReadOnly || d.IsModifiable || d.IsWorkshared || !d.Title.StartsWith("BIM-Agentic-Test",StringComparison.Ordinal))throw new InvalidOperationException("isolated_test_document_required");
        if(request.DocumentIdentity!=d.ProjectInformation.UniqueId)throw new InvalidOperationException("document_identity_conflict");
        if(string.IsNullOrWhiteSpace(request.IdempotencyKey))throw new ArgumentException("idempotency_key_required");
        var issues=NativeBatchContract.Preflight(request.Before,request.After,request.Plan);
        if(issues.Length>0)return JsonSerializer.Serialize(new {status="blocked",verification="unverified",issues});
        var schema=StorageSchema(); var stores=new FilteredElementCollector(d).OfClass(typeof(DataStorage)).Cast<DataStorage>().Where(s=>s.GetEntity(schema).IsValid()).ToArray();
        if(stores.Length>1)throw new InvalidOperationException("ambiguous_native_state");
        var storage=stores.SingleOrDefault();
        var state=storage==null ? new State(request.Before,new(),new()) : JsonSerializer.Deserialize<State>(storage.GetEntity(schema).Get<string>("Payload")) ?? throw new InvalidOperationException("invalid_native_state");
        if(state.Receipts.TryGetValue(request.IdempotencyKey,out var receipt)) {
            if(receipt!=request.Plan.AfterHash)throw new InvalidOperationException("idempotency_conflict");
            if(Compiler.Hash(state.Project with {Revision=request.After.Revision})!=request.Plan.AfterHash)throw new InvalidOperationException("retry_superseded");
            Readback(d,state.Project,state.Map);
            return JsonSerializer.Serialize(new {status="succeeded",verification="native_geometry_readback",retry=true,project=state.Project,mapping=state.Map,documentation="unsupported"});
        }
        if(storage==null && request.Before.Elements.Length!=0)throw new InvalidOperationException("native_baseline_not_initialized");
        if(state.Project.Id!=request.Before.Id || state.Project.Revision!=request.Before.Revision || Compiler.Hash(state.Project)!=request.Plan.BeforeHash)throw new InvalidOperationException("native_revision_conflict");
        Readback(d,state.Project,state.Map);
        using var group=new TransactionGroup(d,"BIM Agentic typed batch");
        if(group.Start()!=TransactionStatus.Started)throw new InvalidOperationException("group_not_started");
        try {
            using(var tx=new Transaction(d,"Apply typed operations")) {
                if(tx.Start()!=TransactionStatus.Started)throw new InvalidOperationException("transaction_not_started");
                foreach(var op in request.Plan.Operations.OrderBy(o=>o.Element.Kind=="level"?0:1)) {
                    var e=op.Element; var g=e.GeometryMetres;
                    if(op.Action=="delete") { d.Delete(d.GetElement(state.Map[e.Id]).Id); state.Map.Remove(e.Id); continue; }
                    Autodesk.Revit.DB.Element native;
                    if(e.Kind=="level") {
                        if(op.Action=="create") native=Level.Create(d,U(g[0]));
                        else {var level=(Level)d.GetElement(state.Map[e.Id]); level.Elevation=U(g[0]); native=level;}
                    } else if(e.Kind=="wall") {
                        var level=(Level)d.GetElement(state.Map[e.Dependencies[0]]);
                        var axis=Line.CreateBound(new XYZ(U(g[0]),U(g[1]),level.Elevation),new XYZ(U(g[2]),U(g[3]),level.Elevation));
                        // Do not edit or duplicate a shared wall type to simulate requested thickness.
                        var type=new FilteredElementCollector(d).OfClass(typeof(WallType)).Cast<WallType>().FirstOrDefault(t=>t.Kind==WallKind.Basic && Near(t.Width,g[5])) ?? throw new NotSupportedException("matching_wall_type_required");
                        Wall wall;
                        if(op.Action=="create")wall=Wall.Create(d,axis,type.Id,level.Id,U(g[4]),0,false,false);
                        else {wall=(Wall)d.GetElement(state.Map[e.Id]); wall.WallType=type; wall.get_Parameter(BuiltInParameter.WALL_BASE_CONSTRAINT).Set(level.Id); wall.get_Parameter(BuiltInParameter.WALL_BASE_OFFSET).Set(0.0); ((LocationCurve)wall.Location).Curve=axis; wall.get_Parameter(BuiltInParameter.WALL_USER_HEIGHT_PARAM).Set(U(g[4]));}
                        native=wall;
                    } else throw new NotSupportedException("unsupported_native_kind");
                    if(op.Action=="update" && state.Map[e.Id]!=native.UniqueId)throw new InvalidOperationException("native_identity_changed");
                    state.Map[e.Id]=native.UniqueId;
                }
                d.Regenerate(); Readback(d,request.After,state.Map);
                if(tx.Commit()!=TransactionStatus.Committed)throw new InvalidOperationException("transaction_not_committed");
            }
            // Verify again after Revit failure processing, before publishing the durable receipt.
            Readback(d,request.After,state.Map);
            var result=request.After with {Revision=request.After.Revision+1};
            state.Receipts.Add(request.IdempotencyKey,request.Plan.AfterHash);
            using(var receiptTx=new Transaction(d,"Persist logical map and receipt")) {
                if(receiptTx.Start()!=TransactionStatus.Started)throw new InvalidOperationException("receipt_transaction_not_started");
                storage ??= DataStorage.Create(d); var entity=new Entity(schema); entity.Set("Payload",JsonSerializer.Serialize(new State(result,state.Map,state.Receipts))); storage.SetEntity(entity);
                if(receiptTx.Commit()!=TransactionStatus.Committed)throw new InvalidOperationException("receipt_not_committed");
            }
            if(group.Assimilate()!=TransactionStatus.Committed)throw new InvalidOperationException("group_not_committed");
            return JsonSerializer.Serialize(new {status="succeeded",verification="native_geometry_readback",project=result,mapping=state.Map,documentation="unsupported"});
        } catch {if(group.GetStatus()==TransactionStatus.Started)group.RollBack();throw;}
    }
}

