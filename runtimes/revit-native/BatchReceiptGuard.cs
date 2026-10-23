using Bim.Core;
namespace Bim.Revit;

// A receipt check cannot execute a missing batch or persist anything. This is deliberately
// separate from Run's retry-or-apply branch and is used on both sides of disk reopening.
public static class BatchReceiptGuard
{
    public static void Verify(NativeBatchRequest request, Project project, Dictionary<Guid,string> map, Dictionary<string,string> receipts, int expectedRevision, Dictionary<Guid,string> expectedMap)
    {
        if(string.IsNullOrWhiteSpace(request.IdempotencyKey) || NativeBatchContract.Preflight(request.Before,request.After,request.Plan).Length!=0) throw new InvalidOperationException("checkpoint_invalid_batch");
        if(!receipts.TryGetValue(request.IdempotencyKey,out var receipt)) throw new InvalidOperationException("checkpoint_receipt_missing");
        if(receipt!=request.Plan.AfterHash) throw new InvalidOperationException("checkpoint_receipt_conflict");
        if(expectedRevision!=request.After.Revision+1 || project.Revision!=expectedRevision) throw new InvalidOperationException("checkpoint_revision_conflict");
        if(project.Id!=request.After.Id || Compiler.Hash(project with {Revision=request.After.Revision})!=request.Plan.AfterHash) throw new InvalidOperationException("checkpoint_snapshot_conflict");
        if(map.Count!=project.Elements.Length || expectedMap.Count!=map.Count || map.Values.Any(string.IsNullOrWhiteSpace) || map.Values.Distinct(StringComparer.Ordinal).Count()!=map.Count || project.Elements.Any(e=>!map.ContainsKey(e.Id)) || map.Any(pair=>!expectedMap.TryGetValue(pair.Key,out var id) || id!=pair.Value)) throw new InvalidOperationException("checkpoint_mapping_conflict");
    }
}
