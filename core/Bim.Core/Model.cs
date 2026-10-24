using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
namespace Bim.Core;

public readonly partial record struct Length
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
            if(e.Kind == "wall" && !GeometryCodec.IsValidWall(e.GeometryMetres)) issues.Add(new("wall_geometry",e.Id,"Invalid wall axis/height/thickness"));
        }
        // Remove prerequisites before their dependents. Remaining nodes contain a cycle.
        // Iterative traversal avoids stack growth for deep valid dependency chains.
        var prerequisites = new Dictionary<Guid,int>();
        var dependents = new Dictionary<Guid,List<Guid>>();
        foreach(var id in ids) { prerequisites.Add(id,0); dependents.Add(id,new()); }
        var visitedIds = new HashSet<Guid>();
        foreach(var e in p.Elements) {
            if(!visitedIds.Add(e.Id)) continue; // Duplicate identities already have an issue.
            foreach(var dependency in e.Dependencies) {
                if(!ids.Contains(dependency)) continue; // Dangling references already have an issue.
                prerequisites[e.Id]++;
                dependents[dependency].Add(e.Id);
            }
        }
        var ready = new Queue<Guid>(prerequisites.Where(pair=>pair.Value==0).Select(pair=>pair.Key));
        var processed = 0;
        while(ready.TryDequeue(out var id)) {
            processed++;
            foreach(var dependent in dependents[id]) if(--prerequisites[dependent]==0) ready.Enqueue(dependent);
        }
        if(processed != ids.Count) issues.Add(new("dependency_cycle",null,"Cyclic logical dependencies"));
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
    // Deterministic crash injection for ADR0007 recovery tests only; null outside tests.
    internal Action<string>? CrashAfter { get; init; }
    public record State(Project Project, Dictionary<string,string> Receipts);
    State ReadState() => JsonSerializer.Deserialize<State>(File.ReadAllText(Path.Combine(directory,"project.json")))!;
    public Project Read() => ReadState().Project;
    public void Initialize(Project p) { Directory.CreateDirectory(directory); using var guard = Lock(); if(Compiler.Validate(p).Length>0) throw new InvalidOperationException("invalid_ir"); if(File.Exists(Path.Combine(directory,"project.json"))) throw new IOException("already_initialized"); Write(new(p,new())); }
    FileStream Lock() => new(Path.Combine(directory,"lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
    void Write(State state) { var tmp=Path.Combine(directory,"project.tmp"); using(var file=new FileStream(tmp,FileMode.Create,FileAccess.Write,FileShare.None)) { JsonSerializer.Serialize(file,state); file.Flush(true); } CrashAfter?.Invoke("after_tmp"); File.Move(tmp,Path.Combine(directory,"project.json"),true); }
    // Same encoding File.WriteAllText/AppendAllText use (UTF-8, no BOM, throw on invalid), so persisted bytes are unchanged.
    static readonly UTF8Encoding Utf8=new(false,true);
    // Temp + Flush(true) + replace, like Write. The ".json.tmp" suffix never matches IfcReconcile.IsConflictCopy (extension differs).
    void WriteCheckpoint(string path,string json) { var tmp=path+".tmp"; using(var file=new FileStream(tmp,FileMode.Create,FileAccess.Write,FileShare.None)) { file.Write(Utf8.GetBytes(json)); file.Flush(true); } CrashAfter?.Invoke("after_checkpoint_tmp"); File.Move(tmp,path,true); }
    void AppendJournal(string line) { using var file=new FileStream(Path.Combine(directory,"journal.jsonl"),FileMode.Append,FileAccess.Write,FileShare.Read); file.Write(Utf8.GetBytes(line)); file.Flush(true); }
    public Project Commit(Project desired, ExecutionPlan plan, string key)
    {
        using var guard = Lock();
        if(string.IsNullOrWhiteSpace(key)) throw new ArgumentException("idempotency_key_required");
        var state=ReadState();
        if(state.Receipts.TryGetValue(key,out var hash)) { if(hash!=plan.AfterHash || Compiler.Hash(desired)!=hash) throw new InvalidOperationException("idempotency_conflict"); return state.Project; }
        var current=state.Project; if(current.Id!=plan.ProjectId || current.Revision!=plan.ExpectedRevision || Compiler.Hash(current)!=plan.BeforeHash || Compiler.Hash(desired)!=plan.AfterHash) throw new InvalidOperationException("revision_conflict");
        if(Compiler.Validate(desired).Length>0) throw new InvalidOperationException("invalid_ir");
        if(JsonSerializer.Serialize(Compiler.Diff(current,desired))!=JsonSerializer.Serialize(plan)) throw new InvalidOperationException("plan_mismatch");
        WriteCheckpoint(Path.Combine(directory,$"checkpoint-{current.Revision}.json"),JsonSerializer.Serialize(current));
        CrashAfter?.Invoke("after_checkpoint");
        var result=desired with { Revision=current.Revision+1 };
        // Journal is prepared before publish; recovery can reconcile by hashes.
        AppendJournal(JsonSerializer.Serialize(new { key, plan, state="prepared" })+Environment.NewLine);
        CrashAfter?.Invoke("after_journal");
        state.Receipts.Add(key,plan.AfterHash);
        // Publish revision and receipt in the same atomic snapshot.
        Write(new(result,state.Receipts)); return result;
    }
}
