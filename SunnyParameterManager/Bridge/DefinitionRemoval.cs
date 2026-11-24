using Autodesk.Revit.DB;
using SunnyParameterManager.Core;

namespace SunnyParameterManager.Bridge;

// 삭제 전용 승인만 처리한다. 실제 쓰기 직전과 확정 후에 같은 근거를 다시 확인한다.
internal static class DefinitionRemoval
{
    internal static List<ModelEngine.WriteAction> Prepare(Document doc, IReadOnlyCollection<ValueEdit> edits)
    {
        var proofs = edits.Select(DefinitionRemovalChecks.ValidateRequest).ToList();
        if (proofs.Count == 0 || proofs.Select(p => p.Source.Key).Distinct().Count() != proofs.Count ||
            proofs.Any(p => p.Keeper != null && proofs.Any(d => d.Source.Key == p.Keeper.Key)))
            throw new InvalidOperationException("삭제 전용 요청이 중복되거나 남길 정의와 삭제할 정의가 겹칩니다.");
        foreach (var proof in proofs) Check(doc, proof);
        var definitionsBefore = DefinitionReader.Read(doc, false).Definitions.ToDictionary(d => d.Key);
        var idsBefore = AllElementIds(doc);
        var removedKeys = proofs.Select(p => p.Source.Key).ToHashSet();
        var allowedIds = proofs.Select(p => long.Parse(p.Source.ElementId, System.Globalization.CultureInfo.InvariantCulture)).ToHashSet();
        var projectValues = ProjectValues(doc, removedKeys);

        void VerifyAll()
        {
            var after = DefinitionReader.Read(doc, false).Definitions.ToDictionary(d => d.Key);
            if (removedKeys.Any(after.ContainsKey) || !after.Keys.ToHashSet().SetEquals(definitionsBefore.Keys.Except(removedKeys)) ||
                after.Any(pair => !DefinitionRemovalChecks.SameMetadata(definitionsBefore[pair.Key], pair.Value)))
                throw new InvalidOperationException("삭제 확정 후 승인하지 않은 매개변수 정의 변화가 발생했습니다. 이번 묶음을 취소합니다.");
            foreach (var keeper in proofs.Where(p => p.Keeper != null).Select(p => p.Keeper!).DistinctBy(p => p.Key)) CheckDefinition(doc, keeper);
            if (doc.ProjectInformation.Parameters.Cast<Parameter>().Any(p => removedKeys.Contains(ParameterReader.Key(p))))
                throw new InvalidOperationException("삭제 대상이 프로젝트 정보에 남아 있어 이번 묶음을 취소합니다.");
            if (projectValues != ProjectValues(doc, removedKeys))
                throw new InvalidOperationException("삭제 확정 후 다른 프로젝트 정보 값이 달라져 이번 묶음을 취소합니다.");
            var idsAfter = AllElementIds(doc);
            if (idsAfter.Except(idsBefore).Any() || idsBefore.Except(idsAfter).Any(id => !allowedIds.Contains(id)))
                throw new InvalidOperationException("승인한 매개변수 이외의 요소가 추가·삭제되어 이번 묶음을 취소합니다.");
        }
        var writes = new List<ModelEngine.WriteAction>();
        foreach (var proof in proofs)
        {
            writes.Add(new(() =>
            {
                Check(doc, proof);
                var found = DefinitionReader.Find(doc, proof.Source.Key)!.Value;
                var id = found.Def.Id;
                var shared = doc.GetElement(id) is SharedParameterElement;
                if (!doc.ParameterBindings.Remove(found.Def)) throw new InvalidOperationException("Revit이 삭제 대상 연결 제거를 거부했습니다: " + proof.Source.Name);
                if (!shared && doc.GetElement(id) != null)
                {
                    var deleted = doc.Delete(id);
                    if (deleted.Any(d => d.Value != id.Value)) throw new InvalidOperationException("매개변수 삭제에 다른 요소 삭제가 동반되어 이번 묶음을 취소합니다.");
                }
            }, writes.Count == 0 ? VerifyAll : () => { }));
        }
        return writes;
    }

    private static HashSet<long> AllElementIds(Document doc)
    {
        // Revit은 필터가 없는 collector의 ToElementIds/순회를 허용하지 않는다.
        // 유형과 배치 요소를 각각 필터링해 기존의 전체 요소 보존 검사를 유지한다.
        using var types = new FilteredElementCollector(doc).WhereElementIsElementType();
        using var instances = new FilteredElementCollector(doc).WhereElementIsNotElementType();
        return types.ToElementIds().Concat(instances.ToElementIds()).Select(id => id.Value).ToHashSet();
    }

    private static string ProjectValues(Document doc, HashSet<string> excluded)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Parameter p in doc.ProjectInformation.Parameters)
        {
            var key = ParameterReader.Key(p);
            if (!excluded.Contains(key)) values.Add(key, System.Text.Json.JsonSerializer.Serialize(new
            { p.Definition.Name, Storage = p.StorageType.ToString(), DataType = p.Definition.GetDataType()?.TypeId, p.HasValue, Raw = ParameterReader.Raw(p) }));
        }
        return DefinitionRemovalChecks.Fingerprint(values);
    }

    private static void Check(Document doc, DefinitionRemovalEvidence proof)
    {
        CheckDefinition(doc, proof.Source);
        if (proof.Keeper != null) CheckDefinition(doc, proof.Keeper);
        if (doc.IsWorkshared && WorksharingUtils.GetCheckoutStatus(doc, doc.ProjectInformation.Id) == CheckoutStatus.OwnedByOtherUser)
            throw new InvalidOperationException("다른 사용자가 프로젝트 정보를 소유하고 있습니다. 반환 후 다시 조회하세요.");
    }
    private static void CheckDefinition(Document doc, ParameterDefinitionInfo expected)
    {
        var found = DefinitionReader.Find(doc, expected.Key) ?? throw new InvalidOperationException("조회 당시 매개변수 정의가 없습니다: " + expected.Name);
        var actual = DefinitionReader.Describe(doc, found.Def, found.Binding);
        DefinitionRemovalChecks.CheckFresh(expected, actual, DefinitionReader.Values(doc, found.Def, found.Binding));
        if (doc.IsWorkshared && WorksharingUtils.GetCheckoutStatus(doc, found.Def.Id) == CheckoutStatus.OwnedByOtherUser)
            throw new InvalidOperationException("다른 사용자가 매개변수 정의를 소유하고 있습니다: " + expected.Name);
    }
}
