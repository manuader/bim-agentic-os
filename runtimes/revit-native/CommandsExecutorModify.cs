using Autodesk.Revit.DB;
using Bim.Core;
using RevitElement = Autodesk.Revit.DB.Element;
namespace Bim.Revit;

// P1-05 element.set_parameter and element.delete Revit executor. Same admission (copies-only guard), receipts and
// transaction group as catalogue v1 (CommandsExecutor.cs); every decision that needs no Revit object is in
// CommandsExecutorModifyRules.cs (unit-tested). A dry run runs the same transaction (failure processing included,
// so its warnings are reported), reads back and reports the change, then rolls the transaction group back: nothing
// is written and no receipt is stored. Evidence level: compiled against the Revit 2025 reference assemblies in CI;
// nothing here has run in Revit.
//
// Revit 2025 API used (https://www.revitapidocs.com/2025/):
//   Element.GetParameters(string), Element.get_Parameter(BuiltInParameter), Element.get_Parameter(Guid),
//   Parameter.Set(double | int | string | ElementId), Parameter.IsReadOnly / StorageType, Definition.GetDataType,
//   SpecTypeId.Boolean.YesNo, SpecTypeId.Int.Integer, UnitUtils.ConvertToInternalUnits with UnitTypeId.Millimeters /
//   SquareMeters / Degrees
//   Document.Delete(ICollection<ElementId>) (returns every deleted id), DocumentValidation.CanDeleteElement,
//   Element.Pinned / GroupId, Document.ActiveView, Document.ProjectInformation
//   Wall.CurtainGrid, FootPrintRoof / ExtrusionRoof.CurtainGrids, CurtainGrid.GetUGridLineIds / GetVGridLineIds / GetPanelIds /
//     GetMullionIds, Sketch.OwnerId / SketchPlane / GetAllElements, FamilyInstance.Host, Element.GetDependentElements
//     (owned sub-elements of a delete target, SubElementOwners)
// element.delete first deletes inside a probe transaction that is always rolled back, to name every element Revit
// would delete (the ids exist again after the rollback); the kept delete must remove exactly the same ids.
// Side effects: element.delete compares a pre/post snapshot of every element id (FilteredElementCollector with
// ElementIsElementTypeFilter both ways) so the kept delete removes nothing beyond its probe; element.set_parameter
// refuses a change that deletes any element unless confirm_deletions names exactly those elements (chat-floor-fixes,
// owner 2026-11-04: a floor's FLOOR_HEIGHTABOVELEVEL_PARAM deleted internal element 215987, no category): the first run
// is rolled back so the deleted elements exist again and are named by UniqueId, then a dry run reports them and a
// confirmed real run repeats the change, which must delete exactly the same ids; it reports the other ids
// Application.DocumentChanged lists as modified.
// Revit ArgumentException / InvalidOperationException from Parameter.Set or Document.Delete become args_invalid.
// Unverified in Revit (owner session required): that Document.Delete returns the same ids in the probe and the kept
// transaction; that DocumentChanged fires for a transaction committed inside an open transaction group; that elements deleted with a target (hosted doors, sketches, tags) all have a readable UniqueId after
// the probe rollback; that an element a parameter change deletes exists again, with the same id and UniqueId, after
// the first run's transaction group is rolled back, and that the repeated change deletes the same ids; that UnitUtils round trips of areas and angles stay within 1e-6 m2 and 1e-6 degrees.
internal static partial class CommandExecutor
{
    static string ModifyExecute(Document d, CommandRequest request, ReceiptStore store) => request.Envelope.Args switch
    {
        ElementSetParameterArgs args => SetParameterRun(d, request, store, args),
        ElementDeleteArgs args => DeleteRun(d, request, store, args),
        _ => throw new RevitCommandException(RevitCommandCodes.CommandUnknown, "command", request.Envelope.Command),
    };

    static Parameter ResolveParameter(RevitElement element, RevitParameterRef reference)
    {
        switch (reference)
        {
            case ParameterByName byName:
                var named = element.GetParameters(byName.Name).Where(parameter => parameter.Definition is not null).ToArray();
                CommandRulesModify.CheckNamedMatches(named.Length, byName.Name);
                return named[0];
            case ParameterByBuiltIn byBuiltIn:
                // The schema allows only upper-case letters, digits and underscores, so no numeric or comma form parses.
                if (!Enum.TryParse<BuiltInParameter>(byBuiltIn.BuiltIn, false, out var builtIn) || !Enum.IsDefined(builtIn) || builtIn == BuiltInParameter.INVALID)
                    throw new RevitCommandException(RevitCommandCodes.ReferenceNotFound, "args.parameter.built_in", "Revit has no BuiltInParameter named " + byBuiltIn.BuiltIn);
                return element.get_Parameter(builtIn) is { Definition: not null } found ? found
                    : throw new RevitCommandException(RevitCommandCodes.ReferenceNotFound, "args.parameter.built_in", "the element has no parameter " + byBuiltIn.BuiltIn);
            case ParameterByGuid byGuid:
                return element.get_Parameter(byGuid.Guid) is { Definition: not null } shared ? shared
                    : throw new RevitCommandException(RevitCommandCodes.ReferenceNotFound, "args.parameter.guid", "the element has no shared parameter " + byGuid.Guid.ToString("D"));
            default:
                throw new RevitCommandException(RevitCommandCodes.ArgsInvalid, "args.parameter", "unsupported parameter reference");
        }
    }

    static ParameterTarget Target(Document d, Parameter parameter)
    {
        ForgeTypeId? dataType = null;
        try { dataType = parameter.Definition.GetDataType(); }
        catch (Autodesk.Revit.Exceptions.ApplicationException) { dataType = null; }
        var yesNo = dataType is not null && dataType.Equals(SpecTypeId.Boolean.YesNo);
        var integer = dataType is not null && dataType.Equals(SpecTypeId.Int.Integer);
        return new ParameterTarget(ObserveParameter(d, parameter, RevitParameterSources.Instance), yesNo, integer);
    }

    static bool SetValue(Parameter parameter, RevitParameterValue value, ElementId? elementValue) => value switch
    {
        LengthValueArg length => parameter.Set(UnitUtils.ConvertToInternalUnits(length.Value.Millimetres, UnitTypeId.Millimeters)),
        AreaValueArg area => parameter.Set(UnitUtils.ConvertToInternalUnits(area.Value.SquareMetres, UnitTypeId.SquareMeters)),
        AngleValueArg angle => parameter.Set(UnitUtils.ConvertToInternalUnits(angle.Value.Degrees, UnitTypeId.Degrees)),
        TextValueArg text => parameter.Set(text.Value),
        IntegerValueArg integer => parameter.Set(integer.Value),
        YesNoValueArg yesNo => parameter.Set(yesNo.Value ? 1 : 0),
        ElementValueArg => parameter.Set(elementValue!),
        _ => false,
    };

    // Every element id in the document (instances and types), for the pre/post deletion snapshot.
    static HashSet<long> ElementIds(Document d) =>
        new FilteredElementCollector(d).WherePasses(new LogicalOrFilter(new ElementIsElementTypeFilter(false), new ElementIsElementTypeFilter(true)))
            .ToElementIds().Select(id => id.Value).ToHashSet();

    static long[] Removed(HashSet<long> before, Document d)
    {
        var after = ElementIds(d);
        return before.Where(id => !after.Contains(id)).ToArray();
    }

    // Revit's own argument and operation refusals become args_invalid at the given field.
    static T Refusing<T>(string field, Func<T> call)
    {
        try { return call(); }
        catch (Autodesk.Revit.Exceptions.ArgumentException error) { throw CommandRulesModify.RevitRefused(field, error.Message); }
        catch (Autodesk.Revit.Exceptions.InvalidOperationException error) { throw CommandRulesModify.RevitRefused(field, error.Message); }
    }

    static string Finish(Document d, CommandRequest request, ReceiptStore store, TransactionGroup group, ModifyReply reply) =>
        reply.Persist ? Persist(d, request, store, group, reply.Response) : RollBackDryRun(group, reply.Response);

    static string SetParameterRun(Document d, CommandRequest request, ReceiptStore store, ElementSetParameterArgs args)
    {
        var element = ElementByUid(d, args.ElementUid, "args.element_uid");
        CommandRulesModify.CheckParameterElement(element.UniqueId, element.GroupId is { } groupId && groupId != ElementId.InvalidElementId);
        var parameter = ResolveParameter(element, args.Parameter);
        var target = Target(d, parameter);
        var kind = CommandRulesModify.CheckSettable(target, args.Value);
        var elementValue = args.Value is ElementValueArg value ? ElementByUid(d, value.UniqueId, "args.value.uid").Id : null;
        var before = target.Parameter;
        if (CommandRulesModify.StoresReceipt(args.DryRun)) CommandReceiptStore.EnsureRoom(store.Receipts);
        var snapshot = ElementIds(d);
        var protectedUids = new[] { d.ProjectInformation?.UniqueId, store.Storage?.UniqueId }.Where(uid => uid is not null).Select(uid => uid!).ToArray();

        // First run with no deletion accepted. When the change deletes elements (inside the transaction or during failure
        // processing) that run is rolled back, which restores them, so they can be named by UniqueId; the deletions are
        // then checked (SetParameterDeletions: reported by a dry run, confirmed by confirm_deletions on a real run, else
        // refused) and the change runs again accepting exactly those ids.
        var outcome = SetParameterPass(d, request, store, args, element, elementValue, before, kind, snapshot, Array.Empty<long>(), Array.Empty<DeletedElement>(), protectedUids);
        if (outcome.Reply is { } reply) return reply;
        var named = outcome.Deleted.Select(id => d.GetElement(new ElementId(id)) is { } gone ? new DeletedElement(gone.UniqueId, id, gone.Category?.Name, gone.GetType().Name) : new DeletedElement(null, id, null, "unknown")).ToArray();
        var accepted = CommandRulesModify.SetParameterDeletions(args.DryRun, named, args.ConfirmDeletions, protectedUids);
        var second = SetParameterPass(d, request, store, args, element, elementValue, before, kind, snapshot, outcome.Deleted, accepted, protectedUids);
        return second.Reply ?? throw new RevitCommandException(RevitCommandCodes.TransactionFailed, "args.value", "the change deleted different elements on its second run; rolled back");
    }

    // A deletion found inside the set transaction: thrown so the transaction rolls back, caught by SetParameterPass.
    sealed class SetParameterDeletionsFound : Exception
    {
        public SetParameterDeletionsFound(long[] ids) : base("the parameter change deletes elements") => Ids = ids;
        public long[] Ids { get; }
    }

    // One run of the change in its own transaction group. Reply: the finished reply (persisted or rolled back as a dry
    // run). Deleted: when the run deleted ids other than acceptedIds, the group is rolled back and those ids are returned
    // (Reply null). acceptedIds empty is the first run; non-empty, the run must delete exactly them.
    static (string? Reply, long[] Deleted) SetParameterPass(Document d, CommandRequest request, ReceiptStore store, ElementSetParameterArgs args, RevitElement element,
        ElementId? elementValue, ObservedParameter before, string kind, HashSet<long> snapshot, IReadOnlyCollection<long> acceptedIds, IReadOnlyList<DeletedElement> accepted, IReadOnlyList<string> protectedUids)
    {
        // Resolved again on each run: an earlier run's rollback may have invalidated the Parameter wrapper.
        var parameter = ResolveParameter(element, args.Parameter);
        var acceptedSet = acceptedIds.ToHashSet();
        bool Unaccepted(long[] removed) => !acceptedSet.SetEquals(removed);
        var failures = new CommandFailures();
        // DocumentChanged reports the ids each committed transaction modified (unverified inside a transaction group).
        var modifiedIds = new List<long>();
        void OnChanged(object? sender, Autodesk.Revit.DB.Events.DocumentChangedEventArgs changed)
        {
            if (changed.GetDocument().Equals(d)) modifiedIds.AddRange(changed.GetModifiedElementIds().Select(id => id.Value));
        }
        using var group = StartGroup(d, request);
        try
        {
            d.Application.DocumentChanged += OnChanged;
            try
            {
                ModifyTransaction(d, request, failures, () =>
                {
                    if (!Refusing("args.value", () => SetValue(parameter, args.Value, elementValue)))
                        throw new RevitCommandException(RevitCommandCodes.ArgsInvalid, "args.value", "Revit refused the value for parameter " + before.Name);
                    d.Regenerate();
                    var removedInside = Removed(snapshot, d);
                    if (acceptedSet.Count == 0 && removedInside.Length > 0) throw new SetParameterDeletionsFound(removedInside);
                    if (CommandReadbackModify.Parameter(args.Value, ObserveParameter(d, parameter, RevitParameterSources.Instance)) is { } mismatch) throw Mismatch(mismatch);
                });
            }
            finally { d.Application.DocumentChanged -= OnChanged; }
            var removed = Removed(snapshot, d);
            // Failure processing deleted elements on a first run: roll back so they exist again and can be named.
            if (acceptedSet.Count == 0 && removed.Length > 0)
            {
                group.RollBack();
                return (null, removed);
            }
            if (acceptedSet.Count > 0 && Unaccepted(removed)) throw new RevitCommandException(RevitCommandCodes.TransactionFailed, "args.value", "the change deleted " + removed.Length + " element(s) on its second run where " + acceptedSet.Count + " were named; rolled back");
            // Failure processing may change the element: resolve the parameter again and read back before any receipt.
            if (!element.IsValidObject) throw Mismatch("the element no longer exists");
            var others = CommandRulesModify.SetParameterEffects(element.Id.Value, removed, modifiedIds, acceptedSet);
            var alsoModified = others.Select(id => d.GetElement(new ElementId(id))?.UniqueId).Where(uid => uid is not null).Select(uid => uid!).ToArray();
            var after = ObserveParameter(d, ResolveParameter(element, args.Parameter), RevitParameterSources.Instance);
            if (CommandReadbackModify.Parameter(args.Value, after) is { } afterMismatch) throw Mismatch(afterMismatch);
            // No deletion: confirmations a real run named are stale (refused); with deletions they were checked already.
            var deletions = accepted.Count > 0 ? accepted : CommandRulesModify.SetParameterDeletions(args.DryRun, Array.Empty<DeletedElement>(), args.ConfirmDeletions, protectedUids);
            if (deletions.Select(gone => gone.UniqueId).Any(uid => uid is not null && d.GetElement(uid) is { IsValidObject: true }))
                throw Mismatch("an element the change deleted still exists");
            var confirmed = !args.DryRun && deletions.Count > 0;
            var changed = !CommandRulesModify.SameValue(kind, before, after);
            var readback = CommandReadbackModify.ParameterJson(args.DryRun, element.UniqueId, kind, before, after, changed, alsoModified, deletions, confirmed);
            return (Finish(d, request, store, group, CommandResponsesModify.SetParameter(request, args.DryRun, element.UniqueId, changed, alsoModified, readback, failures.Warnings.ToArray(),
                deletions.Select(gone => gone.UniqueId!).ToArray())), Array.Empty<long>());
        }
        catch (SetParameterDeletionsFound found)
        {
            if (group.GetStatus() == TransactionStatus.Started) group.RollBack();
            return (null, found.Ids);
        }
        catch
        {
            if (group.GetStatus() == TransactionStatus.Started) group.RollBack();
            throw;
        }
    }

    static DeleteTargetFacts DeleteFacts(Document d, RevitElement element, ElementId? projectInformationId, ElementId? storeId, ElementId? activeViewId) =>
        new(element.UniqueId, DocumentValidation.CanDeleteElement(d, element.Id), element.Pinned,
            element.GroupId is { } groupId && groupId != ElementId.InvalidElementId,
            projectInformationId is not null && element.Id == projectInformationId,
            storeId is not null && element.Id == storeId,
            activeViewId is not null && element.Id == activeViewId);

    // Owners of the sub-elements Revit deletes with their owner and refuses to delete on their own
    // (DeleteTargetFacts.OwnerUid; CommandRulesModify.CheckDependents waives CanDeleteElement when the chain reaches a
    // target): the grid lines, panels and mullions of a target curtain wall or roof (CurtainGrid ids, or Panel/Mullion
    // Host), a sketch among the dependents (Sketch.OwnerId) with its sketch plane and elements, and an element without a
    // category (not a type or view) among a target's GetDependentElements. Unverified in Revit: that the owner's curtain
    // grid ...00034896 and profiled wall/roof sketch ...00034904 fall under these rules.
    static Dictionary<string, string> SubElementOwners(Document d, IReadOnlyList<RevitElement> targets, IReadOnlyList<string> dependentUids)
    {
        var owners = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void Own(ElementId id, string ownerUid)
        {
            if (d.GetElement(id) is { } element && !string.Equals(element.UniqueId, ownerUid, StringComparison.OrdinalIgnoreCase)) owners.TryAdd(element.UniqueId, ownerUid);
        }
        void Grid(CurtainGrid? grid, string ownerUid)
        {
            if (grid is null) return;
            foreach (var id in grid.GetUGridLineIds().Concat(grid.GetVGridLineIds()).Concat(grid.GetPanelIds()).Concat(grid.GetMullionIds())) Own(id, ownerUid);
        }
        foreach (var target in targets)
        {
            if (target is Wall wall) Grid(wall.CurtainGrid, target.UniqueId);
            var grids = target is FootPrintRoof footprint ? footprint.CurtainGrids : target is ExtrusionRoof extrusion ? extrusion.CurtainGrids : null;
            if (grids is not null)
                foreach (CurtainGrid grid in grids) Grid(grid, target.UniqueId);
        }
        foreach (var uid in dependentUids)
        {
            var dependent = d.GetElement(uid);
            if (dependent is Sketch sketch)
            {
                if (d.GetElement(sketch.OwnerId) is { } owner) Own(sketch.Id, owner.UniqueId);
                if (sketch.SketchPlane is { } plane) Own(plane.Id, sketch.UniqueId);
                foreach (var id in sketch.GetAllElements()) Own(id, sketch.UniqueId);
            }
            else if (dependent is Panel or Mullion && ((FamilyInstance)dependent).Host is { } host) Own(dependent.Id, host.UniqueId);
        }
        foreach (var target in targets)
            foreach (var id in target.GetDependentElements(null))
                if (d.GetElement(id) is { Category: null } element && element is not ElementType && element is not View) Own(id, target.UniqueId);
        return owners;
    }

    static string DeleteRun(Document d, CommandRequest request, ReceiptStore store, ElementDeleteArgs args)
    {
        var targets = args.ElementUids.Select((uid, i) => ElementByUid(d, uid, "args.element_uids[" + i + "]")).ToArray();
        var projectInformation = d.ProjectInformation;
        var activeViewId = d.ActiveView?.Id;
        for (var i = 0; i < targets.Length; i++)
            CommandRulesModify.CheckDeletable(DeleteFacts(d, targets[i], projectInformation?.Id, store.Storage?.Id, activeViewId), "args.element_uids[" + i + "]");
        var protectedUids = new[] { projectInformation?.UniqueId, store.Storage?.UniqueId }.Where(uid => uid is not null).Select(uid => uid!).ToArray();
        var targetUids = targets.Select(element => element.UniqueId).ToArray();
        var ids = targets.Select(element => element.Id).ToList();
        if (CommandRulesModify.StoresReceipt(args.DryRun)) CommandReceiptStore.EnsureRoom(store.Receipts);
        var failures = new CommandFailures();
        var snapshot = ElementIds(d);
        using var group = StartGroup(d, request);
        try
        {
            // Probe: what Revit deletes with the targets (Document.Delete's ids plus anything regeneration removed,
            // from the pre/post snapshot). The probe transaction is ALWAYS rolled back, so the elements exist again
            // and can be named by UniqueId and checked below.
            long[] probeDeleted;
            HashSet<long> probeSet;
            using (var probeTx = new Transaction(d, "BIM Agentic OS element.delete probe"))
            {
                if (probeTx.Start() != TransactionStatus.Started) throw new RevitCommandException(RevitCommandCodes.TransactionFailed, null, "probe transaction did not start");
                try
                {
                    probeDeleted = Refusing("args.element_uids", () => d.Delete(ids)).Select(id => id.Value).ToArray();
                    d.Regenerate();
                    probeSet = probeDeleted.Concat(Removed(snapshot, d)).ToHashSet();
                }
                finally { if (probeTx.GetStatus() == TransactionStatus.Started) probeTx.RollBack(); }
            }
            var cascade = CommandRulesModify.Cascade(targetUids, probeSet.Select(id => d.GetElement(new ElementId(id))?.UniqueId).ToArray());
            CommandRulesModify.CheckCascade(cascade, protectedUids);
            var owners = SubElementOwners(d, targets, cascade.Dependents);
            CommandRulesModify.CheckDependents(cascade.Dependents.Select(uid => DeleteFacts(d, d.GetElement(uid), projectInformation?.Id, store.Storage?.Id, activeViewId)
                with { OwnerUid = owners.TryGetValue(uid, out var owner) ? owner : null }).ToArray(), targetUids);
            if (!args.DryRun) CommandRulesModify.CheckConfirmed(cascade.Dependents, args.ConfirmDependents);
            ModifyTransaction(d, request, failures, () =>
            {
                var deleted = Refusing("args.element_uids", () => d.Delete(ids)).Select(id => id.Value).ToArray();
                CommandRulesModify.CheckSameCascade(probeDeleted, deleted);
                d.Regenerate();
            });
            // After commit (failure processing included): nothing beyond the probe set may be gone, and none of it may remain.
            CommandRulesModify.CheckNoExtraDeletions(probeSet, Removed(snapshot, d));
            var expected = targetUids.Concat(cascade.Dependents).ToArray();
            if (CommandReadbackModify.Deleted(expected.Where(uid => d.GetElement(uid) is not null).ToArray()) is { } mismatch) throw Mismatch(mismatch);
            return Finish(d, request, store, group, CommandResponsesModify.Delete(request, args.DryRun, targetUids, cascade.Dependents, args.ConfirmDependents, failures.Warnings.ToArray()));
        }
        catch
        {
            if (group.GetStatus() == TransactionStatus.Started) group.RollBack();
            throw;
        }
    }

    static TransactionGroup StartGroup(Document d, CommandRequest request)
    {
        var group = new TransactionGroup(d, "BIM Agentic OS " + request.Envelope.Command + " " + request.Envelope.OperationId);
        if (group.Start() != TransactionStatus.Started)
        {
            group.Dispose();
            throw new RevitCommandException(RevitCommandCodes.TransactionFailed, null, "transaction group did not start");
        }
        return group;
    }

    // One write transaction with the catalogue's failure handling: warnings are collected and removed, any error rolls
    // back as transaction_failed.
    static void ModifyTransaction(Document d, CommandRequest request, CommandFailures failures, Action apply)
    {
        using var tx = new Transaction(d, "BIM Agentic OS " + request.Envelope.Command);
        if (tx.Start() != TransactionStatus.Started) throw new RevitCommandException(RevitCommandCodes.TransactionFailed, null, "transaction did not start");
        var options = tx.GetFailureHandlingOptions();
        options.SetFailuresPreprocessor(failures);
        options.SetClearAfterRollback(true);
        tx.SetFailureHandlingOptions(options);
        try
        {
            apply();
            var status = tx.Commit();
            if (status != TransactionStatus.Committed)
                throw new RevitCommandException(RevitCommandCodes.TransactionFailed, null, failures.Errors.Count > 0 ? string.Join("; ", failures.Errors) : "Revit did not commit the transaction (" + status + ")");
        }
        catch
        {
            if (tx.GetStatus() == TransactionStatus.Started) tx.RollBack();
            throw;
        }
    }

    // A dry run's committed transaction is undone by rolling its group back; the report is returned, never stored.
    static string RollBackDryRun(TransactionGroup group, RevitCommandResponse response)
    {
        if (group.RollBack() != TransactionStatus.RolledBack) throw new RevitCommandException(RevitCommandCodes.TransactionFailed, null, "the dry run could not be rolled back");
        return response.ToJson();
    }
}
