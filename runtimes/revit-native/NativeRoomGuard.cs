namespace Bim.Revit;
public static class NativeRoomGuard
{
    public static void ValidateRequest(string? identity,double[] metres,bool nested)
    {
        if(string.IsNullOrWhiteSpace(identity))throw new InvalidOperationException("room_document_identity_required");
        if(metres==null || metres.Length!=0)throw new InvalidOperationException("room_empty_metres_required");
        if(nested)throw new InvalidOperationException("room_nested_request_rejected");
    }
    // Only eliminate less than one millionth millimetre of binary conversion noise.
    public static double NormalizeMillimetres(double value)
    {
        if(!double.IsFinite(value))throw new InvalidOperationException("room_nonfinite_measurement");
        var integer=Math.Round(value); return Math.Abs(value-integer)<=1e-6?integer:value;
    }
    public static double SelectMeasuredWidth(double? instance,double? symbol)
    {
        if(instance is double value&&double.IsFinite(value)&&value>0)return value;
        if(symbol is double typeValue&&double.IsFinite(typeValue)&&typeValue>0)return typeValue;
        throw new InvalidOperationException("room_door_width_readback_missing");
    }
    public static int SelectParameterField(long[] actualParameterIds,long requiredParameterId)
    {
        var indices=Enumerable.Range(0,actualParameterIds.Length).Where(i=>actualParameterIds[i]==requiredParameterId).ToArray();
        if(indices.Length!=1)throw new InvalidOperationException("room_schedulable_parameter_missing_or_ambiguous");
        return indices[0];
    }
    public static void ValidateCommittedSnapshot(string expected,string actual)
    {
        if(expected!=actual)throw new InvalidOperationException("room_committed_snapshot_conflict");
    }
    public static void ValidateReceipt(string expected,string actual,int expectedRevision,int actualRevision)
    {
        if(expected!=actual || expectedRevision!=actualRevision)throw new InvalidOperationException("room_receipt_conflict");
    }
}
