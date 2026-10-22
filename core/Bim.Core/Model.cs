using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
namespace Bim.Core;

public readonly record struct Length
{
    public double Metres { get; }
    public Length(double metres) { if (!double.IsFinite(metres)) throw new ArgumentException("Non-finite length"); Metres = metres; }
    public double Feet => Metres / 0.3048;
}
public record Provenance(string Source, string Approval);
public record Element(Guid Id, string Kind, double[] GeometryMetres, Guid[] Dependencies, Provenance Provenance);
public record Project(Guid Id, int Revision, Element[] Elements);
public record Operation(string Action, Element Element);
public record ExecutionPlan(Guid ProjectId, int ExpectedRevision, string BeforeHash, string AfterHash, Operation[] Operations, Guid[] DirtyIds);
public record ValidationIssue(string Code, Guid? Id, string Message);
public record CommandResult(string Status, string Verification, ExecutionPlan Plan, double TotalMs);

public static class Compiler
{
    public static string Hash(Project p) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(p))));
    public static ValidationIssue[] Validate(Project p)
    {
        var issues = new List<ValidationIssue>();
        var ids = p.Elements.Select(e => e.Id).ToHashSet();
        if(ids.Count != p.Elements.Length) issues.Add(new("duplicate_id", null, "Duplicate logical identity"));
        foreach(var e in p.Elements) {
            if(e.GeometryMetres.Any(x => !double.IsFinite(x))) issues.Add(new("units",e.Id,"Non-finite coordinate"));
            if(e.Dependencies.Any(d => !ids.Contains(d))) issues.Add(new("missing_dependency",e.Id,"Dangling dependency"));
            if(e.Kind == "wall" && (e.GeometryMetres.Length != 6 || e.GeometryMetres[4] <= 0 || e.GeometryMetres[5] <= 0 || (e.GeometryMetres[0] == e.GeometryMetres[2] && e.GeometryMetres[1] == e.GeometryMetres[3]))) issues.Add(new("wall_geometry",e.Id,"Invalid wall axis/height/thickness"));
        }
        return issues.ToArray();
    }
    public static ExecutionPlan Diff(Project before, Project after)
    {
        if(before.Id != after.Id || before.Revision != after.Revision) throw new InvalidOperationException("revision_conflict");
        if(Validate(after).Length > 0) throw new InvalidOperationException("invalid_ir");
        var old = before.Elements.ToDictionary(e=>e.Id); var next = after.Elements.ToDictionary(e=>e.Id);
        var ops = new List<Operation>();
        foreach(var e in after.Elements) if(!old.TryGetValue(e.Id,out var prior)) ops.Add(new("create",e)); else if(JsonSerializer.Serialize(e)!=JsonSerializer.Serialize(prior)) ops.Add(new("update",e));
        foreach(var e in before.Elements) if(!next.ContainsKey(e.Id)) ops.Add(new("delete",e));
        var dirty = ops.Select(o=>o.Element.Id).ToHashSet();
        bool changed;
        do { changed=false; foreach(var e in before.Elements.Concat(after.Elements)) if(e.Dependencies.Any(dirty.Contains)) changed |= dirty.Add(e.Id); } while(changed);
        return new(before.Id,before.Revision,Hash(before),Hash(after),ops.ToArray(),dirty.Order().ToArray());
    }
}

// Durable intended-state store. Native realized state is deliberately separate.
// One OS file lock protects compare-and-swap across processes; atomic rename publishes snapshots.
public sealed class ProjectStore(string directory)
{
    public Project Read() => JsonSerializer.Deserialize<Project>(File.ReadAllText(Path.Combine(directory,"project.json")))!;
    public void Initialize(Project p) { Directory.CreateDirectory(directory); using var guard = Lock(); if(File.Exists(Path.Combine(directory,"project.json"))) throw new IOException("already_initialized"); Write(p); }
    FileStream Lock() => new(Path.Combine(directory,"lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
    void Write(Project p) { var tmp=Path.Combine(directory,"project.tmp"); File.WriteAllText(tmp,JsonSerializer.Serialize(p)); File.Move(tmp,Path.Combine(directory,"project.json"),true); }
    public Project Commit(Project desired, ExecutionPlan plan, string key)
    {
        using var guard = Lock();
        var receipt=Path.Combine(directory,Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))+".json");
        if(File.Exists(receipt)) { var saved=JsonSerializer.Deserialize<ExecutionPlan>(File.ReadAllText(receipt))!; if(saved.AfterHash!=plan.AfterHash) throw new InvalidOperationException("idempotency_conflict"); return Read(); }
        var current=Read(); if(Compiler.Hash(current)!=plan.BeforeHash || Compiler.Hash(desired)!=plan.AfterHash) throw new InvalidOperationException("revision_conflict");
        File.WriteAllText(Path.Combine(directory,$"checkpoint-{current.Revision}.json"),JsonSerializer.Serialize(current));
        var result=desired with { Revision=current.Revision+1 };
        // Journal is prepared before publish; recovery can reconcile by hashes.
        File.AppendAllText(Path.Combine(directory,"journal.jsonl"),JsonSerializer.Serialize(new { key, plan, state="prepared" })+Environment.NewLine);
        Write(result); File.WriteAllText(receipt,JsonSerializer.Serialize(plan)); return result;
    }
}
