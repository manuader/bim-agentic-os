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
// element.delete first deletes inside a probe transaction that is always rolled back, to name every element Revit
// would delete (the ids exist again after the rollback); the kept delete must remove exactly the same ids.
// Unverified in Revit (owner session required): that Document.Delete returns the same ids in the probe and the kept
// transaction; that elements deleted with a target (hosted doors, sketches, tags) all have a readable UniqueId after
// the probe rollback; that UnitUtils round trips of areas and angles stay within 1e-6 m2 and 1e-6 degrees.
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

    static string SetParameterRun(Document d, CommandRequest request, ReceiptStore store, ElementSetParameterArgs args)
    {
        var element = ElementByUid(d, args.ElementUid, "args.element_uid");
        var parameter = ResolveParameter(element, args.Parameter);
        var target = Target(d, parameter);
        var kind = CommandRulesModify.CheckSettable(target, args.Value);
        var elementValue = args.Value is ElementValueArg value ? ElementByUid(d, value.UniqueId, "args.value.uid").Id : null;
        var before = target.Parameter;
        if (!args.DryRun) CommandReceiptStore.EnsureRoom(store.Receipts);
        var failures = new CommandFailures();
        using var group = StartGroup(d, request);
        try
        {
            ModifyTransaction(d, request, failures, () =>
            {
                if (!SetValue(parameter, args.Value, elementValue))
                    throw new RevitCommandException(RevitCommandCodes.ArgsInvalid, "args.value", "Revit refused the value for parameter " + before.Name);
                d.Regenerate();
                if (CommandReadbackModify.Parameter(args.Value, ObserveParameter(d, parameter, RevitParameterSources.Instance)) is { } mismatch) throw Mismatch(mismatch);
            });
            // Failure processing may change the element: resolve the parameter again and read back before any receipt.
            if (!element.IsValidObject) throw Mismatch("the element no longer exists");
            var after = ObserveParameter(d, ResolveParameter(element, args.Parameter), RevitParameterSources.Instance);
            if (CommandReadbackModify.Parameter(args.Value, after) is { } afterMismatch) throw Mismatch(afterMismatch);
            var changed = !CommandRulesModify.SameValue(kind, before, after);
            var readback = CommandReadbackModify.ParameterJson(args.DryRun, element.UniqueId, kind, before, after, changed);
            var modified = !args.DryRun && changed ? new[] { element.UniqueId } : Array.Empty<string>();
            var response = RevitCommandResponse.Success(Receipt(request), Array.Empty<string>(), modified, Array.Empty<string>(), readback, failures.Warnings.ToArray());
            return args.DryRun ? RollBackDryRun(group, response) : Persist(d, request, store, group, response);
        }
        catch
        {
            if (group.GetStatus() == TransactionStatus.Started) group.RollBack();
            throw;
        }
    }

    static string DeleteRun(Document d, CommandRequest request, ReceiptStore store, ElementDeleteArgs args)
    {
        var targets = args.ElementUids.Select((uid, i) => ElementByUid(d, uid, "args.element_uids[" + i + "]")).ToArray();
        var projectInformation = d.ProjectInformation;
        var activeViewId = d.ActiveView?.Id;
        for (var i = 0; i < targets.Length; i++)
        {
            var element = targets[i];
            CommandRulesModify.CheckDeletable(new DeleteTargetFacts(element.UniqueId, DocumentValidation.CanDeleteElement(d, element.Id), element.Pinned,
                element.GroupId is { } groupId && groupId != ElementId.InvalidElementId,
                projectInformation is not null && element.Id == projectInformation.Id,
                store.Storage is not null && element.Id == store.Storage.Id,
                activeViewId is not null && element.Id == activeViewId), "args.element_uids[" + i + "]");
        }
        var protectedUids = new[] { projectInformation?.UniqueId, store.Storage?.UniqueId }.Where(uid => uid is not null).Select(uid => uid!).ToArray();
        var targetUids = targets.Select(element => element.UniqueId).ToArray();
        var ids = targets.Select(element => element.Id).ToList();
        if (!args.DryRun) CommandReceiptStore.EnsureRoom(store.Receipts);
        var failures = new CommandFailures();
        using var group = StartGroup(d, request);
        try
        {
            // Probe: what Revit deletes with the targets, named while the elements exist again after the rollback.
            long[] probe;
            using (var probeTx = new Transaction(d, "BIM Agentic OS element.delete probe"))
            {
                if (probeTx.Start() != TransactionStatus.Started) throw new RevitCommandException(RevitCommandCodes.TransactionFailed, null, "probe transaction did not start");
                try { probe = d.Delete(ids).Select(id => id.Value).ToArray(); }
                finally { if (probeTx.GetStatus() == TransactionStatus.Started) probeTx.RollBack(); }
            }
            var cascade = CommandRulesModify.Cascade(targetUids, probe.Select(id => d.GetElement(new ElementId(id))?.UniqueId).ToArray());
            CommandRulesModify.CheckCascade(cascade, protectedUids);
            if (!args.DryRun) CommandRulesModify.CheckConfirmed(cascade.Dependents, args.ConfirmDependents);
            ModifyTransaction(d, request, failures, () =>
            {
                var deleted = d.Delete(ids).Select(id => id.Value).ToArray();
                CommandRulesModify.CheckSameCascade(probe, deleted);
            });
            var expected = targetUids.Concat(cascade.Dependents).ToArray();
            if (CommandReadbackModify.Deleted(expected.Where(uid => d.GetElement(uid) is not null).ToArray()) is { } mismatch) throw Mismatch(mismatch);
            var readback = CommandReadbackModify.DeleteJson(args.DryRun, targetUids, cascade.Dependents, CommandRulesModify.Confirmed(cascade.Dependents, args.ConfirmDependents));
            var response = RevitCommandResponse.Success(Receipt(request), Array.Empty<string>(), Array.Empty<string>(), args.DryRun ? Array.Empty<string>() : expected, readback, failures.Warnings.ToArray());
            return args.DryRun ? RollBackDryRun(group, response) : Persist(d, request, store, group, response);
        }
        catch
        {
            if (group.GetStatus() == TransactionStatus.Started) group.RollBack();
            throw;
        }
    }

    static RevitCommandReceipt Receipt(CommandRequest request) =>
        new(request.Envelope.OperationId, request.Envelope.Command, request.Envelope.Document.UniqueId, request.RequestSha256, false);

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
