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
        if(Validate(before).Length > 0 || Validate(after).Length > 0) throw new InvalidOperationException("invalid_ir");
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
    public record State(Project Project, Dictionary<string,string> Receipts);
    State ReadState() => JsonSerializer.Deserialize<State>(File.ReadAllText(Path.Combine(directory,"project.json")))!;
    public Project Read() => ReadState().Project;
    public void Initialize(Project p) { Directory.CreateDirectory(directory); using var guard = Lock(); if(Compiler.Validate(p).Length>0) throw new InvalidOperationException("invalid_ir"); if(File.Exists(Path.Combine(directory,"project.json"))) throw new IOException("already_initialized"); Write(new(p,new())); }
    FileStream Lock() => new(Path.Combine(directory,"lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
    void Write(State state) { var tmp=Path.Combine(directory,"project.tmp"); using(var file=new FileStream(tmp,FileMode.Create,FileAccess.Write,FileShare.None)) { JsonSerializer.Serialize(file,state); file.Flush(true); } File.Move(tmp,Path.Combine(directory,"project.json"),true); }
    public Project Commit(Project desired, ExecutionPlan plan, string key)
    {
        using var guard = Lock();
        if(string.IsNullOrWhiteSpace(key)) throw new ArgumentException("idempotency_key_required");
        var state=ReadState();
        if(state.Receipts.TryGetValue(key,out var hash)) { if(hash!=plan.AfterHash || Compiler.Hash(desired)!=hash) throw new InvalidOperationException("idempotency_conflict"); return state.Project; }
        var current=state.Project; if(current.Id!=plan.ProjectId || current.Revision!=plan.ExpectedRevision || Compiler.Hash(current)!=plan.BeforeHash || Compiler.Hash(desired)!=plan.AfterHash) throw new InvalidOperationException("revision_conflict");
        if(Compiler.Validate(desired).Length>0) throw new InvalidOperationException("invalid_ir");
        File.WriteAllText(Path.Combine(directory,$"checkpoint-{current.Revision}.json"),JsonSerializer.Serialize(current));
        var result=desired with { Revision=current.Revision+1 };
        // Journal is prepared before publish; recovery can reconcile by hashes.
        File.AppendAllText(Path.Combine(directory,"journal.jsonl"),JsonSerializer.Serialize(new { key, plan, state="prepared" })+Environment.NewLine);
        state.Receipts.Add(key,plan.AfterHash);
        // Publish revision and receipt in the same atomic snapshot.
        Write(new(result,state.Receipts)); return result;
    }
}
