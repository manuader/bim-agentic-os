using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.DB.ExtensibleStorage;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using Bim.Core;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using CoreElement=Bim.Core.Element;
namespace Bim.Revit;

// All entry points run only on App.Execute's serialized ExternalEvent thread.
internal static class NativeRoomFixture
{
    static readonly Guid SchemaId=new("be8622df-08dc-4842-95d5-787ff2a80a84");
    static readonly Guid RoomId=new("cd178069-b563-4de9-84f4-3bdc08d7a2a1");
    static readonly Guid ProjectId=new("a8b37b05-20e7-41c2-b337-c41fad54eff4");
    static int mappedReads,nativeWrites,transactions;
    const string Operation="native-room-width-400-v1";
    sealed record State(Project Project,Dictionary<string,string> Map,Dictionary<string,string> Receipts);
    static string Id(string role)=> role is "door" or "window-east" or "window-north" or "dimension" or "3d" ? "fixture:"+role : RoomCompiler.Id(RoomId,role).ToString("D");
    static double U(double m)=>UnitUtils.ConvertToInternalUnits(m,UnitTypeId.Meters);
    static double Mm(double feet)=>NativeRoomGuard.NormalizeMillimetres(UnitUtils.ConvertFromInternalUnits(feet,UnitTypeId.Millimeters));
    static Project Compile(int revision,double width)=>RoomCompiler.Compile(ProjectId,revision,new(RoomId,width,4,3,.2,0,0,"m",new("isolated native Room fixture","user-authorized native fixture")));
    static Schema Schema()
    {
        var existing=Autodesk.Revit.DB.ExtensibleStorage.Schema.Lookup(SchemaId); if(existing!=null)return existing;
        var b=new SchemaBuilder(SchemaId);b.SetSchemaName("BimNativeRoomABStateV1");b.SetReadAccessLevel(AccessLevel.Public);b.SetWriteAccessLevel(AccessLevel.Public);b.AddSimpleField("Payload",typeof(string));return b.Finish();
    }
    static State? ReadState(Document d)
    {
        var schema=Autodesk.Revit.DB.ExtensibleStorage.Schema.Lookup(SchemaId);if(schema==null)return null;
        var stores=new FilteredElementCollector(d).OfClass(typeof(DataStorage)).Cast<DataStorage>().Where(x=>x.GetEntity(schema).IsValid()).ToArray();
        if(stores.Length>1)throw new InvalidOperationException("room_state_ambiguous");
        return stores.Length==0?null:JsonSerializer.Deserialize<State>(stores[0].GetEntity(schema).Get<string>("Payload")) ?? throw new InvalidOperationException("room_state_invalid");
    }
    static void Persist(Document d,State state)
    {
        var schema=Schema();var store=new FilteredElementCollector(d).OfClass(typeof(DataStorage)).Cast<DataStorage>().SingleOrDefault(x=>x.GetEntity(schema).IsValid())??DataStorage.Create(d);
        var entity=new Entity(schema);entity.Set("Payload",JsonSerializer.Serialize(state));store.SetEntity(entity);nativeWrites++;
    }
    static T Get<T>(Document d,State s,string role) where T:Autodesk.Revit.DB.Element => d.GetElement(s.Map[Id(role)]) as T ?? throw new InvalidOperationException("room_missing_native_"+role);
    static void Transaction(Document d,string name,Action action)
    {
        using var tx=new Transaction(d,name); if(tx.Start()!=TransactionStatus.Started)throw new InvalidOperationException("room_transaction_start");
        transactions++;try {action();d.Regenerate();if(tx.Commit()!=TransactionStatus.Committed)throw new InvalidOperationException("room_transaction_commit");}
        catch {if(tx.GetStatus()==TransactionStatus.Started)tx.RollBack();throw;}
    }
    static void Authorize(UIApplication app,Request r,FixtureConfig config,string configPath,string parking)
    {
        NativeRoomGuard.ValidateRequest(r.UniqueId,r.Metres,r.Batch!=null||r.Checkpoint!=null);
        FixtureCheckpointGuard.Validate(config,configPath,Environment.GetEnvironmentVariable("BIM_AGENTIC_TEST_CONFIG"),parking,Environment.ProcessId,r.DocumentPath,r.UniqueId!);
        var d=app.ActiveUIDocument?.Document??throw new InvalidOperationException("no_document");
        if(d.PathName!=config.FixturePath || d.ProjectInformation.UniqueId!=r.UniqueId)throw new InvalidOperationException("room_document_identity_conflict");
        if(app.Application.Documents.Size!=1 || d.IsReadOnly||d.IsModifiable||d.IsFamilyDocument||d.IsWorkshared||!d.Title.StartsWith("BIM-Agentic-Test",StringComparison.Ordinal))throw new InvalidOperationException("room_dedicated_writable_fixture_required");
    }
    static Document Checkpoint(UIApplication app,Request r,FixtureConfig config,string configPath)
    {
        var parkingPath=Path.Combine(config.RunDirectory,"BIM-Agentic-Test-room-parking-"+Guid.NewGuid().ToString("N")+".rvt");
        Authorize(app,r,config,configPath,parkingPath);
        var fixture=app.ActiveUIDocument.Document;fixture.Save();
        var created=app.Application.NewProjectDocument(config.TemplatePath);
        using(var save=new SaveAsOptions{OverwriteExistingFile=false})created.SaveAs(parkingPath,save);
        if(!created.Close(false))throw new InvalidOperationException("room_created_parking_close_failed");
        var parking=app.OpenAndActivateDocument(parkingPath).Document;
        if(app.ActiveUIDocument.Document.Equals(fixture)||fixture.IsModifiable||!fixture.Close(false))throw new InvalidOperationException("room_fixture_close_failed");
        var reopened=app.OpenAndActivateDocument(config.FixturePath).Document;
        if(reopened.ProjectInformation.UniqueId!=r.UniqueId||reopened.IsReadOnly||reopened.IsModifiable||reopened.IsWorkshared)throw new InvalidOperationException("room_reopen_identity_failed");
        if(app.ActiveUIDocument.Document.Equals(parking)||parking.IsModifiable||!parking.Close(false))throw new InvalidOperationException("room_parking_close_failed");
        return reopened;
    }
    static FamilySymbol Family(Document d,BuiltInCategory category,string libraryPath)
    {
        FamilySymbol? Find()=>new FilteredElementCollector(d).OfClass(typeof(FamilySymbol)).OfCategory(category).Cast<FamilySymbol>().FirstOrDefault(x=>x.Family.FamilyPlacementType==FamilyPlacementType.OneLevelBasedHosted);
        var symbol=Find();
        if(symbol==null) {
            if(!File.Exists(libraryPath))throw new NotSupportedException("room_wall_hosted_family_prerequisite_missing_"+category);
            if(!d.LoadFamily(libraryPath,out var family)||family==null)throw new NotSupportedException("room_family_load_failed_"+category);
            symbol=Find();
        }
        return symbol??throw new NotSupportedException("room_loaded_family_not_wall_hosted_"+category);
    }
    static State Create(Document d)
    {
        // Preflight all resources before creating physical fixture elements. Group rollback also
        // removes any loaded symbols/types when a prerequisite cannot satisfy its native contract.
        var wallType=new FilteredElementCollector(d).OfClass(typeof(WallType)).Cast<WallType>().FirstOrDefault(x=>x.Kind==WallKind.Basic&&Math.Abs(Mm(x.Width)-200)<1e-6)??throw new NotSupportedException("room_200mm_basic_wall_type_missing");
        var planType=new FilteredElementCollector(d).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>().FirstOrDefault(x=>x.ViewFamily==ViewFamily.FloorPlan)??throw new NotSupportedException("room_floor_plan_type_missing");
        var threeType=new FilteredElementCollector(d).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>().FirstOrDefault(x=>x.ViewFamily==ViewFamily.ThreeDimensional)??throw new NotSupportedException("room_3d_type_missing");
        var state=new State(Compile(1,5),new(),new());
        using var group=new TransactionGroup(d,"Isolated native Room A");group.Start();
        try {
            Transaction(d,"Preflight wall-hosted family resources",()=> {
                var door=Family(d,BuiltInCategory.OST_Doors,@"C:\ProgramData\Autodesk\RVT 2025\Libraries\Spanish\Puertas\Puertas internas\Puerta abatible 1.rfa");
                var window=Family(d,BuiltInCategory.OST_Windows,@"C:\ProgramData\Autodesk\RVT 2025\Libraries\Spanish\Ventanas\Ventana batiente 1.rfa");
                var duplicate=(FamilySymbol)door.Duplicate("BIM-Agentic-Test-900");
                var width=duplicate.get_Parameter(BuiltInParameter.DOOR_WIDTH)??throw new NotSupportedException("room_door_width_parameter_missing");
                if(width.IsReadOnly||!width.Set(U(.9)))throw new NotSupportedException("room_door_900_width_unavailable");
                duplicate.Activate();window.Activate();state.Map["door-symbol"]=duplicate.UniqueId;state.Map["window-symbol"]=window.UniqueId;
            });
            Transaction(d,"Compile native Room A",()=> {
                void Put(string role,Autodesk.Revit.DB.Element e) {state.Map.Add(Id(role),e.UniqueId);nativeWrites++;}
                var level=Level.Create(d,0);level.Name="BIM-Agentic-Test-Room-Level";Put("level",level);
                foreach(var role in new[]{"south","east","north","west"}) {
                    var g=state.Project.Elements.Single(x=>x.Id==RoomCompiler.Id(RoomId,role)).GeometryMetres;
                    var w=Wall.Create(d,Line.CreateBound(new(U(g[0]),U(g[1]),0),new(U(g[2]),U(g[3]),0)),wallType.Id,level.Id,U(g[4]),0,false,false);Put(role,w);
                }
                d.Regenerate();
                var door=d.Create.NewFamilyInstance(new XYZ(U(1.5),U(.1),0),(FamilySymbol)d.GetElement(state.Map["door-symbol"]),Get<Wall>(d,state,"south"),level,StructuralType.NonStructural);Put("door",door);
                var east=d.Create.NewFamilyInstance(new XYZ(U(5.3),U(2),U(1)),(FamilySymbol)d.GetElement(state.Map["window-symbol"]),Get<Wall>(d,state,"east"),level,StructuralType.NonStructural);Put("window-east",east);
                var north=d.Create.NewFamilyInstance(new XYZ(U(2),U(4.3),U(1)),(FamilySymbol)d.GetElement(state.Map["window-symbol"]),Get<Wall>(d,state,"north"),level,StructuralType.NonStructural);Put("window-north",north);
                var room=d.Create.NewRoom(level,new UV(U(2),U(2)));room.Number="BIM-ROOM-AB";room.Name="Isolated Room";Put("space",room);
                var plan=ViewPlan.Create(d,planType.Id,level.Id);plan.Name="BIM-Agentic-Test-Room-Plan";Put("plan",plan);
                var three=View3D.CreateIsometric(d,threeType.Id);three.Name="BIM-Agentic-Test-Room-3D";Put("3d",three);
                d.Regenerate();
                var refs=new ReferenceArray();refs.Append(InnerFace(d,Get<Wall>(d,state,"west"),new XYZ(U(2),U(2),0)));refs.Append(InnerFace(d,Get<Wall>(d,state,"east"),new XYZ(U(2),U(2),0)));
                Put("dimension",d.Create.NewDimension(plan,Line.CreateBound(new XYZ(U(.2),U(2),0),new XYZ(U(5.2),U(2),0)),refs));
                var schedule=ViewSchedule.CreateSchedule(d,new ElementId(BuiltInCategory.OST_Rooms));schedule.Name="BIM-Agentic-Test-Room-Schedule";
                var number=schedule.Definition.AddField(ScheduleFieldType.Instance,new ElementId(BuiltInParameter.ROOM_NUMBER));number.IsHidden=true;
                schedule.Definition.AddFilter(new ScheduleFilter(number.FieldId,ScheduleFilterType.Equal,"BIM-ROOM-AB"));
                var area=schedule.Definition.AddField(ScheduleFieldType.Instance,new ElementId(BuiltInParameter.ROOM_AREA));
                var format=new FormatOptions(UnitTypeId.SquareMeters){UseDefault=false,Accuracy=.000000001};format.SetSymbolTypeId(new ForgeTypeId());area.SetFormatOptions(format);Put("schedule",schedule);
                var sheet=ViewSheet.Create(d,ElementId.InvalidElementId);sheet.SheetNumber="BIM-AB-01";sheet.Name="BIM-Agentic-Test-Room-Sheet";Put("sheet",sheet);
                Viewport.Create(d,sheet.Id,plan.Id,new XYZ(.5,.5,0));ScheduleSheetInstance.Create(d,sheet.Id,schedule.Id,new XYZ(1.5,.8,0));
                state.Map.Remove("door-symbol");state.Map.Remove("window-symbol");state.Receipts.Add("create",Compiler.Hash(state.Project));Persist(d,state);
            });
            if(group.Assimilate()!=TransactionStatus.Committed)throw new InvalidOperationException("room_create_group_failed");return state;
        }catch {if(group.GetStatus()==TransactionStatus.Started)group.RollBack();throw;}
    }
    static Reference InnerFace(Document d,Wall wall,XYZ center)
    {
        return new[]{ShellLayerType.Interior,ShellLayerType.Exterior}.SelectMany(side=>HostObjectUtils.GetSideFaces(wall,side)).Select(r=>(Reference:r,Face:wall.GetGeometryObjectFromReference(r) as PlanarFace)).Where(x=>x.Face!=null).OrderBy(x=>Math.Abs((center-x.Face!.Origin).DotProduct(x.Face.FaceNormal))).First().Reference;
    }
    static State Resize(Document d,State state,bool fail)
    {
        var desired=Compile(state.Project.Revision,5.4);var plan=Compiler.Diff(state.Project,desired);
        if(state.Receipts.TryGetValue(Operation,out var receipt)) {NativeRoomGuard.ValidateReceipt(Compiler.Hash(Compile(1,5.4)),receipt,2,state.Project.Revision);NativeRoomGuard.ValidateCommittedSnapshot(Compiler.Hash(Compile(2,5.4)),Compiler.Hash(state.Project));return state;}
        using var group=new TransactionGroup(d,"Isolated native Room width revision");group.Start();
        try {
            var updated=state with{Project=desired with{Revision=state.Project.Revision+1},Receipts=new(state.Receipts)};
            Transaction(d,"Apply native width diff",()=> {
                foreach(var op in plan.Operations.Where(x=>x.Element.Kind=="wall")) {
                    var wall=(Wall)d.GetElement(state.Map[op.Element.Id.ToString("D")]);var g=op.Element.GeometryMetres;
                    ((LocationCurve)wall.Location).Curve=Line.CreateBound(new(U(g[0]),U(g[1]),0),new(U(g[2]),U(g[3]),0));nativeWrites++;
                }
                d.Regenerate();updated.Receipts.Add(Operation,plan.AfterHash);Persist(d,updated);
            });
            if(fail)throw new InvalidOperationException("room_injected_failure_after_real_mutation");
            if(group.Assimilate()!=TransactionStatus.Committed)throw new InvalidOperationException("room_resize_group_failed");return updated;
        } catch {if(group.GetStatus()==TransactionStatus.Started)group.RollBack();throw;}
    }
    static NativeRoomObservation Observe(Document d,State state,List<object> raw)
    {
        var room=Get<Room>(d,state,"space");
        using var options=new SpatialElementBoundaryOptions{SpatialElementBoundaryLocation=SpatialElementBoundaryLocation.Finish};
        var loops=room.GetBoundarySegments(options);if(loops.Count!=1||loops[0].Count!=4)throw new InvalidOperationException("room_boundary_not_four_segments");
        var segments=loops[0];var points=segments.SelectMany(s=>new[]{s.GetCurve().GetEndPoint(0),s.GetCurve().GetEndPoint(1)}).ToArray();
        var minX=points.Min(p=>p.X);var minY=points.Min(p=>p.Y);var width=Mm(points.Max(p=>p.X)-minX);var depth=Mm(points.Max(p=>p.Y)-minY);
        var reverse=state.Map.ToDictionary(x=>x.Value,x=>x.Key);
        string Logical(ElementId id)=>reverse.TryGetValue(d.GetElement(id).UniqueId,out var logical)?logical:throw new InvalidOperationException("room_unmapped_native_reference");
        var result=new List<NativeRoomElement>();
        void Add(string role,string category,string? host,string[] references,double[] geometry,double? thickness=null,double? height=null,double? span=null,double? area=null) {
            var e=d.GetElement(state.Map[Id(role)])??throw new InvalidOperationException("room_native_missing");
            mappedReads++;result.Add(new(Id(role),e.UniqueId,category,host,references.Order(StringComparer.Ordinal).ToArray(),geometry,thickness,height,span,area));
        }
        var level=Get<Level>(d,state,"level");Add("level","Level",null,[],[Mm(level.Elevation)]);
        foreach(var role in new[]{"south","east","north","west"}) {
            var wall=Get<Wall>(d,state,role);var boundary=segments.SingleOrDefault(s=>s.ElementId==wall.Id)??throw new InvalidOperationException("room_boundary_host_missing");
            var c=boundary.GetCurve();var a=c.GetEndPoint(0);var b=c.GetEndPoint(1);
            // Canonical direction depends only on actual segment orientation, not intended width.
            if((Math.Abs(a.Y-b.Y)<1e-8&&a.X>b.X)||(Math.Abs(a.X-b.X)<1e-8&&a.Y>b.Y))(a,b)=(b,a);
            Add(role,"Wall",Logical(wall.LevelId),[],[Mm(a.X-minX),Mm(a.Y-minY),Mm(b.X-minX),Mm(b.Y-minY)],Mm(wall.Width),Mm(wall.get_Parameter(BuiltInParameter.WALL_USER_HEIGHT_PARAM).AsDouble()));
            raw.Add(new{kind="wall",wall.UniqueId,axis_start=((LocationCurve)wall.Location).Curve.GetEndPoint(0),axis_end=((LocationCurve)wall.Location).Curve.GetEndPoint(1),width_feet=wall.Width});
        }
        foreach(var role in new[]{"door","window-east","window-north"}) {
            var instance=Get<FamilyInstance>(d,state,role);var host=instance.Host as Wall??throw new InvalidOperationException("room_opening_not_wall_hosted");
            var p=((LocationPoint)instance.Location).Point;
            var edge=result.Single(x=>x.NativeId==host.UniqueId).Geometry;
            var x=edge[0]==edge[2]?edge[0]:Mm(p.X-minX);var y=edge[1]==edge[3]?edge[1]:Mm(p.Y-minY);
            var doorWidth=role=="door"?Mm((instance.get_Parameter(BuiltInParameter.DOOR_WIDTH)??instance.Symbol.get_Parameter(BuiltInParameter.DOOR_WIDTH)??throw new InvalidOperationException("room_door_width_readback_missing")).AsDouble()):(double?)null;
            Add(role,role=="door"?"Door":"Window",Logical(host.Id),[],[x,y],span:doorWidth);
        }
        var boundaryIds=segments.Select(x=>Logical(x.ElementId)).Distinct().ToArray();
        var areaM2=UnitUtils.ConvertFromInternalUnits(room.Area,UnitTypeId.SquareMeters);if(Math.Abs(areaM2-Math.Round(areaM2,9))<1e-12)areaM2=Math.Round(areaM2,9);
        Add("space","Room",Logical(room.LevelId),boundaryIds,[],area:areaM2);
        var dimension=Get<Dimension>(d,state,"dimension");if(dimension.Value==null)throw new InvalidOperationException("room_dimension_value_missing");
        var dimensionWidth=Mm(dimension.Value.Value);var dimRefs=dimension.References.Cast<Reference>().Select(r=>Logical(r.ElementId)).ToArray();
        Add("dimension","Dimension",null,dimRefs,[],span:dimensionWidth);
        var plan=Get<ViewPlan>(d,state,"plan");
        var planDimensions=new FilteredElementCollector(d,plan.Id).OfClass(typeof(Dimension)).Cast<Dimension>().Where(x=>x.OwnerViewId==plan.Id).Select(x=>Logical(x.Id)).ToArray();
        Add("plan","Plan",Logical(plan.GenLevel.Id),planDimensions,[]);
        var three=Get<View3D>(d,state,"3d");
        var visible=new FilteredElementCollector(d,three.Id).WhereElementIsNotElementType().ToElements().Where(x=>reverse.ContainsKey(x.UniqueId)&&x is Wall or FamilyInstance or Room).Select(x=>reverse[x.UniqueId]).ToArray();
        Add("3d","View3D",null,visible,[]);
        var schedule=Get<ViewSchedule>(d,state,"schedule");var scheduleRooms=new FilteredElementCollector(d,schedule.Id).OfCategory(BuiltInCategory.OST_Rooms).WhereElementIsNotElementType().ToElements().Select(x=>Logical(x.Id)).ToArray();
        var table=schedule.GetTableData().GetSectionData(SectionType.Body);var areaColumn=Enumerable.Range(0,schedule.Definition.GetFieldCount()).Where(i=>!schedule.Definition.GetField(i).IsHidden).ToArray();
        if(areaColumn.Length!=1)throw new InvalidOperationException("room_schedule_visible_field_ambiguous");
        var cells=new List<string>();var areas=new List<double>();
        for(var row=table.FirstRowNumber;row<=table.LastRowNumber;row++) {
            var text=schedule.GetCellText(SectionType.Body,row,table.FirstColumnNumber);cells.Add(text);
            if(double.TryParse(text,NumberStyles.Float|NumberStyles.AllowThousands,CultureInfo.CurrentCulture,out var numeric))areas.Add(numeric);
        }
        if(areas.Count!=1)throw new InvalidOperationException("room_schedule_area_cell_missing_or_ambiguous");
        var scheduledArea=areas[0];Add("schedule","Schedule",null,scheduleRooms,[],area:scheduledArea);
        var sheet=Get<ViewSheet>(d,state,"sheet");var placed=sheet.GetAllPlacedViews().Select(Logical).Concat(new FilteredElementCollector(d,sheet.Id).OfClass(typeof(ScheduleSheetInstance)).Cast<ScheduleSheetInstance>().Where(x=>x.OwnerViewId==sheet.Id).Select(x=>Logical(x.ScheduleId))).ToArray();
        Add("sheet","Sheet",null,placed,[]);
        raw.Add(new{kind="readback",room_area_internal=room.Area,dimension_internal=dimension.Value,clear_width_internal=points.Max(p=>p.X)-minX,clear_depth_internal=points.Max(p=>p.Y)-minY,schedule_cells=cells,normalization_mm=1e-6});
        var walls=result.Where(x=>x.Category=="Wall").ToArray();if(walls.Select(x=>x.ThicknessMm).Distinct().Count()!=1||walls.Select(x=>x.HeightMm).Distinct().Count()!=1)throw new InvalidOperationException("room_wall_parameters_disagree");
        return new(width,depth,walls[0].ThicknessMm!.Value,walls[0].HeightMm!.Value,result.Single(x=>x.Category=="Door").WidthMm!.Value,"mm",result.ToArray(),dimensionWidth,scheduledArea,true,true,d.PathName);
    }
    static bool Same(NativeRoomObservation a,NativeRoomObservation b)=>JsonSerializer.Serialize(a)==JsonSerializer.Serialize(b);
    static string[] Changed(NativeRoomObservation a,NativeRoomObservation b)=>a.Elements.Where(x=>!b.Elements.Any(y=>JsonSerializer.Serialize(x)==JsonSerializer.Serialize(y))).Select(x=>x.LogicalId).Order(StringComparer.Ordinal).ToArray();
    static bool Valid(NativeRoomObservation o,double width)=>o.ClearWidthMm==width&&o.ClearDepthMm==4000&&o.WallThicknessMm==200&&o.WallHeightMm==3000&&o.DoorWidthMm==900&&o.DimensionWidthMm==width&&Math.Abs(o.ScheduleAreaM2!.Value-width*.004)<=1e-9&&o.Elements.Select(x=>x.LogicalId).Order(StringComparer.Ordinal).SequenceEqual(new[]{"level","south","east","north","west","door","window-east","window-north","space","dimension","plan","3d","schedule","sheet"}.Select(Id).Order(StringComparer.Ordinal));
    public static string Run(UIApplication app,Request request)
    {
        mappedReads=0;nativeWrites=0;transactions=0;bool authorized=false;var timer=Stopwatch.StartNew();Stopwatch? correctionTimer=null;double? correctionMs=null;var raw=new List<object>();NativeRoomObservation? a=null;NativeRoomRevision? b=null;var errors=new List<string>();var artifacts=new List<string>();var checks=NativeRoomJson.CheckIds.Select(id=>new NativeRoomCheck(id,"blocked","native fixture has not completed")).ToArray();
        string stage="authorization";FixtureConfig? config=null;
        try {
            var configPath=Environment.GetEnvironmentVariable("BIM_AGENTIC_TEST_CONFIG")??throw new InvalidOperationException("room_fixture_opt_in_required");
            config=JsonSerializer.Deserialize<FixtureConfig>(File.ReadAllText(configPath))??throw new InvalidOperationException("room_invalid_fixture_config");
            Authorize(app,request,config,configPath,Path.Combine(config.RunDirectory,"BIM-Agentic-Test-room-authorization-"+Guid.NewGuid().ToString("N")+".rvt"));
            authorized=true;artifacts.Add(Path.GetFileName(config.FixturePath));
            var d=app.ActiveUIDocument.Document;
            if(ReadState(d)!=null)throw new InvalidOperationException("room_ab_already_executed_inspect_existing_receipts_no_blind_replay");
            stage="author_A";var state=Create(d);
            stage="reopen_A";d=Checkpoint(app,request,config,configPath);state=ReadState(d)??throw new InvalidOperationException("room_reopened_state_missing");
            if(state.Project.Revision!=1||state.Receipts["create"]!=Compiler.Hash(state.Project))throw new InvalidOperationException("room_create_receipt_invalid");
            stage="read_A";a=Observe(d,state,raw);
            stage="forced_native_rollback";bool forced=false;
            try {Resize(d,state,true);}catch(InvalidOperationException e) when(e.Message=="room_injected_failure_after_real_mutation"){forced=true;}
            var rolledState=ReadState(d)??throw new InvalidOperationException("room_rollback_state_missing");var rollback=Observe(d,rolledState,raw);
            if(!forced||!Same(a,rollback)||rolledState.Project.Revision!=1||rolledState.Receipts.ContainsKey(Operation))throw new InvalidOperationException("room_rollback_readback_mismatch");
            correctionTimer=Stopwatch.StartNew();stage="resize_B";state=Resize(d,rolledState,false);
            stage="reopen_B";d=Checkpoint(app,request,config,configPath);state=ReadState(d)??throw new InvalidOperationException("room_reopened_B_state_missing");
            stage="read_B";var after=Observe(d,state,raw);
            stage="persisted_retry";var persisted=Resize(d,state,false);var retry=Observe(d,persisted,raw);
            var actual=Changed(a,after);
            // Independent permission set derived from this bounded width correction's protected hosts.
            var allowed=new[]{"south","east","north","window-east","space","dimension","schedule"}.Select(Id).Order(StringComparer.Ordinal).ToArray();
            var docs=new[]{"dimension","plan","schedule","sheet"}.Select(Id).Order(StringComparer.Ordinal).ToArray();
            var valid=Valid(a,5000)&&Valid(after,5400)&&Same(after,retry)&&actual.SequenceEqual(allowed);
            b=new(a,after,retry,rollback,Id("west"),allowed,actual,docs,Operation,Operation,forced,valid,"1",state.Project.Revision.ToString(CultureInfo.InvariantCulture),persisted.Project.Revision.ToString(CultureInfo.InvariantCulture),rolledState.Project.Revision.ToString(CultureInfo.InvariantCulture));
            bool Hosting(NativeRoomObservation o)=>o.Elements.Where(x=>x.Category is "Door" or "Window").All(x=>o.Elements.Any(h=>h.LogicalId==x.HostId&&h.Category=="Wall"))&&o.Elements.Where(x=>x.Category is "Wall" or "Room" or "Plan").All(x=>o.Elements.Any(h=>h.LogicalId==x.HostId&&h.Category=="Level"));
            bool Docs(NativeRoomObservation o)=>o.Elements.Single(x=>x.Category=="Dimension").References.Order().SequenceEqual(new[]{Id("west"),Id("east")}.Order())&&o.Elements.Single(x=>x.Category=="Plan").References.Contains(Id("dimension"))&&o.Elements.Single(x=>x.Category=="Schedule").References.SequenceEqual(new[]{Id("space")})&&o.Elements.Single(x=>x.Category=="Sheet").References.Order().SequenceEqual(new[]{Id("plan"),Id("schedule")}.Order())&&o.Elements.Single(x=>x.Category=="View3D").References.Length>0;
            var outcomes=new Dictionary<string,bool>{
                ["native_hosting"]=Hosting(a)&&Hosting(after),
                ["native_geometry"]=Valid(a,5000)&&Valid(after,5400),
                ["native_documentation"]=Docs(a)&&Docs(after),
                ["native_save_reopen"]=a.Saved&&a.Reopened&&after.Saved&&after.Reopened,
                ["native_idempotency"]=Same(after,retry)&&state.Project.Revision==persisted.Project.Revision,
                ["native_rollback"]=forced&&Same(a,rollback)&&rolledState.Project.Revision==1,
                ["native_incremental"]=actual.SequenceEqual(allowed)&&a.Elements.All(x=>after.Elements.Any(y=>x.LogicalId==y.LogicalId&&x.NativeId==y.NativeId)),
                ["native_receipts"]=Compiler.Hash(state.Project)==Compiler.Hash(Compile(2,5.4))&&state.Receipts[Operation]==Compiler.Hash(Compile(1,5.4))};
            checks=NativeRoomJson.CheckIds.Select(id=>new NativeRoomCheck(id,outcomes[id]?"pass":"fail",outcomes[id]?"actual reopened native readback and persisted state satisfy "+id:"native readback failed "+id+"; inspect raw diagnostics")).ToArray();
            b=b with{ValidationPassed=valid&&outcomes.Values.All(x=>x)};correctionMs=correctionTimer.Elapsed.TotalMilliseconds;
        }catch(Exception e) {errors.Add(stage+": "+e.GetType().Name+": "+e.Message);}
        if(config!=null&&authorized) {
            var name="native-room-raw-"+Guid.NewGuid().ToString("N")+".json";
            try {File.WriteAllText(Path.Combine(config.RunDirectory,name),JsonSerializer.Serialize(new{stage,raw,errors},NativeRoomJson.Options));artifacts.Add(name);}catch(Exception e){errors.Add("raw_diagnostics: "+e.Message);}
        }
        var metrics=new Dictionary<string,NativeRoomMetric>{{"total_ms",new(correctionMs,"actual width correction through documentation regeneration, save-close-reopen, QA and persisted retry; excludes A creation and forced-failure probe")},{"revit_execute_ms",new(timer.Elapsed.TotalMilliseconds,"whole serialized fixture lifecycle including creation A, forced rollback, correction B, two save-close-reopen cycles and QA")}};
        metrics["model_elements_read"]=new(mappedReads,"count of mapped physical/documentation element observation records actually read; excludes prerequisite collector enumeration");
        metrics["model_elements_written"]=new(nativeWrites,"actual mapped output creates, wall curve updates and ExtensibleStorage writes, including rolled-back writes; excludes family resource preparation and sheet placement helpers");
        metrics["transaction_count"]=new(transactions,"actual started Revit Transactions, including transaction later reverted by outer group; excludes TransactionGroups");
        var envelope=new NativeRoomEnvelope(1,"revit",app.Application.VersionBuild,NativeRoomJson.Source,new("revit",NativeRoomJson.Source,a,b),metrics,checks,artifacts.ToArray(),errors.ToArray());
        return JsonSerializer.Serialize(envelope,NativeRoomJson.Options);
    }
}

