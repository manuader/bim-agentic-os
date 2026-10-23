using System.Text.Json;
namespace Bim.Revit;
internal sealed record NativeRoomElement(string LogicalId,string NativeId,string Category,string? HostId,string[] References,double[] Geometry,double? ThicknessMm=null,double? HeightMm=null,double? WidthMm=null,double? AreaM2=null);
internal sealed record NativeRoomObservation(double ClearWidthMm,double ClearDepthMm,double WallThicknessMm,double WallHeightMm,double DoorWidthMm,string Units,NativeRoomElement[] Elements,double? DimensionWidthMm,double? ScheduleAreaM2,bool Saved,bool Reopened,string NativeFile);
internal sealed record NativeRoomRevision(NativeRoomObservation Before,NativeRoomObservation After,NativeRoomObservation Retry,NativeRoomObservation Rollback,string AnchorLogicalId,string[] AllowedChangedIds,string[] SemanticChangedIds,string[] UpdatedDocumentIds,string OperationId,string RetryOperationId,bool ForcedFailureObserved,bool ValidationPassed,string RevisionBefore,string RevisionAfter,string RetryRevision,string RollbackRevision);
internal sealed record NativeRoomObservations(string Backend,string Source,NativeRoomObservation? TestA,NativeRoomRevision? TestB);
internal sealed record NativeRoomCheck(string Id,string Outcome,string Reason,double? Expected=null,double? Actual=null,double? Tolerance=null);
internal sealed record NativeRoomMetric(double? Value,string Reason);
internal sealed record NativeRoomEnvelope(int ProtocolVersion,string Backend,string BackendVersion,string Source,NativeRoomObservations Observations,Dictionary<string,NativeRoomMetric> Metrics,NativeRoomCheck[] Checks,string[] Artifacts,string[] Errors);
internal static class NativeRoomJson
{
    internal static readonly JsonSerializerOptions Options=new(){PropertyNamingPolicy=JsonNamingPolicy.SnakeCaseLower};
    internal const string Source="actual isolated Revit authoring and reopened readback";
    internal static readonly string[] CheckIds=["native_hosting","native_geometry","native_documentation","native_save_reopen","native_idempotency","native_rollback","native_incremental","native_receipts"];
}
