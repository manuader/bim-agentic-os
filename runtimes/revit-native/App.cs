using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text.Json;
namespace Bim.Revit;

public record Request(string DocumentPath,string Operation,string? UniqueId,double[] Metres);
public sealed class App : IExternalApplication, IExternalEventHandler
{
    readonly ConcurrentQueue<(Request,TaskCompletionSource<string>)> queue=new();
    CancellationTokenSource stop=new(); ExternalEvent? signal; Task? server;
    public Result OnStartup(UIControlledApplication app) { signal=ExternalEvent.Create(this); server=Task.Run(Listen); return Result.Succeeded; }
    public Result OnShutdown(UIControlledApplication app) { stop.Cancel(); signal?.Dispose(); return Result.Succeeded; }
    public string GetName()=>"BIM Agentic OS serialized native dispatcher";
    async Task Listen()
    {
        while(!stop.IsCancellationRequested) {
            try {
                // ACL enforced by Windows: only same user. No network listener or arbitrary scripts.
                using var pipe=new NamedPipeServerStream("bim-agentic-os-2025",PipeDirection.InOut,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(stop.Token);
                using var reader=new StreamReader(pipe); using var writer=new StreamWriter(pipe){AutoFlush=true};
                while(pipe.IsConnected && !stop.IsCancellationRequested) {
                    var line=await reader.ReadLineAsync(stop.Token); if(line==null) break;
                    if(line.Length>65536) {await writer.WriteLineAsync("{\"status\":\"failed\",\"code\":\"request_too_large\"}");break;}
                    try {
                        var request=JsonSerializer.Deserialize<Request>(line) ?? throw new ArgumentException("invalid_request");
                        var completion=new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                        queue.Enqueue((request,completion)); signal!.Raise();
                        await writer.WriteLineAsync(await completion.Task.WaitAsync(stop.Token));
                    } catch(Exception e) {await writer.WriteLineAsync(JsonSerializer.Serialize(new {status="failed",code=e.Message}));}
                }
            } catch(OperationCanceledException) {break;}
        }
    }
    public void Execute(UIApplication app)
    {
        while(queue.TryDequeue(out var job)) {
            try { job.Item2.SetResult(Run(app,job.Item1)); }
            catch(Exception e) {job.Item2.SetResult(JsonSerializer.Serialize(new {status="failed",code=e.Message,verification="unverified"}));}
        }
    }
    static string Run(UIApplication app,Request r)
    {
        var d=app.ActiveUIDocument?.Document ?? throw new InvalidOperationException("no_document");
        if(string.IsNullOrWhiteSpace(r.DocumentPath)||!String.Equals(d.PathName,r.DocumentPath,StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("document_conflict");
        if(r.Metres.Any(x=>!double.IsFinite(x))) throw new ArgumentException("invalid_units");
        Element? e=r.UniqueId==null?null:d.GetElement(r.UniqueId);
        if(r.Operation=="inspect") {
            if(e==null) throw new ArgumentException("element_not_found");
            return JsonSerializer.Serialize(new {status="succeeded",uniqueId=e.UniqueId,category=e.Category?.Name,kind=e.GetType().Name,verification="readback"});
        }
        if(d.IsReadOnly || d.IsModifiable) throw new InvalidOperationException("document_not_writable");
        using var tx=new Transaction(d,"BIM Agentic OS "+r.Operation); tx.Start();
        try {
            double U(double x)=>UnitUtils.ConvertToInternalUnits(x,UnitTypeId.Meters);
            switch(r.Operation) {
                case "level.create": if(r.Metres.Length!=1) throw new ArgumentException("invalid_geometry"); e=Level.Create(d,U(r.Metres[0])); break;
                case "wall.create":
                    if(r.Metres.Length!=5 || r.Metres[4]<=0 || e is not Level l) throw new ArgumentException("wall_requires_level_and_axis_height");
                    e=Wall.Create(d,Line.CreateBound(new XYZ(U(r.Metres[0]),U(r.Metres[1]),l.Elevation),new XYZ(U(r.Metres[2]),U(r.Metres[3]),l.Elevation)),l.Id,false);
                    e.get_Parameter(BuiltInParameter.WALL_USER_HEIGHT_PARAM).Set(U(r.Metres[4])); break;
                case "wall.axis.update":
                    if(e is not Wall w || r.Metres.Length!=4) throw new ArgumentException("invalid_wall");
                    var location=(LocationCurve)w.Location; double z=location.Curve.GetEndPoint(0).Z;
                    location.Curve=Line.CreateBound(new XYZ(U(r.Metres[0]),U(r.Metres[1]),z),new XYZ(U(r.Metres[2]),U(r.Metres[3]),z)); break;
                case "element.delete": if(e==null) throw new ArgumentException("element_not_found"); d.Delete(e.Id); e=null; break;
                default: throw new NotSupportedException("unsupported_capability");
            }
            d.Regenerate(); var id=e?.UniqueId;
            if(tx.Commit()!=TransactionStatus.Committed) throw new InvalidOperationException("transaction_not_committed");
            return JsonSerializer.Serialize(new {status="succeeded",uniqueId=id,verification="unverified",warning="Primitive executed; independent BIM validation required"});
        } catch {if(tx.GetStatus()==TransactionStatus.Started)tx.RollBack();throw;}
    }
}
