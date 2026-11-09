using Autodesk.Revit.DB;
using SunnyParameterManager.Core;
using System.Globalization;

namespace SunnyParameterManager.Bridge;

// Revit의 문서 간 요소 복사는 비공유 ParameterElement와 바인딩도 함께 복제한다.
// 동명 정의의 연결 설정은 기준으로 맞추고, GUID·자료형이 다르면 정의를 교체한다.
// 전후 정의 목록이 승인 내용과 다르면 묶음 전체를 취소한다.
internal static class ProjectParameterTransferWriter
{
    internal sealed record Plan(int Count, Action Set, Action Verify);

    internal static Plan Prepare(Document source, Document target, ModelSnapshot sourceExpected, DefinitionSnapshot targetExpected)
    {
        if (sourceExpected.Definitions == null || sourceExpected.Query?.TransferOnly != true)
            throw new InvalidOperationException("기준 파일의 프로젝트 매개변수 정의 조회가 없습니다. 다시 조회하세요.");
        var sourceNow = DefinitionReader.Read(source, false);
        var targetNow = DefinitionReader.Read(target, false);
        if (!ProjectParameterTransfer.SameSnapshot(sourceExpected.Definitions, sourceNow))
            throw new InvalidOperationException("기준 파일의 매개변수 정의가 검토 뒤 바뀌었습니다. 기준 파일을 다시 조회하세요.");
        if (!ProjectParameterTransfer.SameSnapshot(targetExpected, targetNow))
            throw new InvalidOperationException("대상 파일의 매개변수 정의가 검토 뒤 바뀌었습니다. 대상 파일을 다시 조회하세요.");
        var currentSource = new ModelSnapshot { Path = sourceExpected.Path, CanonicalPath = sourceExpected.CanonicalPath,
            Identity = sourceExpected.Identity, Query = sourceExpected.Query, Definitions = sourceNow };
        var currentTarget = new ModelSnapshot { Path = target.PathName, CanonicalPath = target.PathName,
            Identity = target.CreationGUID.ToString("D"), Query = sourceExpected.Query, Definitions = targetNow };
        var decisions = ProjectParameterTransfer.Plan(currentSource, currentTarget);
        var blocked = decisions.Where(d => d.Status == "충돌").Take(12).ToList();
        if (blocked.Count > 0)
            throw new InvalidOperationException("동명 또는 호환되지 않는 매개변수가 있어 이 모델 전체를 보류합니다: " +
                string.Join("; ", blocked.Select(d => d.Source.Name + " — " + d.Reason)));
        var additions = decisions.Where(d => d.Status == "추가 예정").ToList();
        var settings = decisions.Where(d => d.Status == "설정 변경 예정").ToList();
        var replacements = decisions.Where(d => d.Status == "정의 교체 예정").ToList();
        var ids = additions.Concat(replacements).Select(d => new ElementId(long.Parse(d.Source.ElementId, CultureInfo.InvariantCulture))).ToList();
        void Set()
        {
            foreach (var decision in settings)
            {
                var old = decision.Target!;
                var found = DefinitionReader.Find(target, old.Key) ??
                    throw new InvalidOperationException("설정 변경 대상 정의가 없어졌습니다: " + old.Name);
                if (target.GetElement(found.Def.Id)?.UniqueId != old.ElementUniqueId)
                    throw new InvalidOperationException("설정 변경 대상 정의가 바뀌었습니다: " + old.Name);
                var group = new ForgeTypeId(decision.Source.Group);
                if (old.Binding == decision.Source.Binding && old.Categories.SequenceEqual(decision.Source.Categories, StringComparer.Ordinal))
                    found.Def.SetGroupTypeId(group);
                else
                {
                    var set = target.Application.Create.NewCategorySet();
                    foreach (var key in decision.Source.Categories)
                    {
                        if (!Enum.TryParse<BuiltInCategory>(key, out var bic) ||
                            Category.GetCategory(target, bic) is not { AllowsBoundParameters: true } category)
                            throw new InvalidOperationException("기준 파일의 연결 카테고리를 대상에서 찾을 수 없습니다: " + key);
                        set.Insert(category);
                    }
                    ElementBinding binding = decision.Source.Binding == "유형"
                        ? target.Application.Create.NewTypeBinding(set) : target.Application.Create.NewInstanceBinding(set);
                    if (!target.ParameterBindings.ReInsert(found.Def, binding, group))
                        throw new InvalidOperationException("Revit이 기준 파일의 연결 설정을 적용하지 않았습니다: " + old.Name);
                }
            }
            foreach (var decision in replacements)
            {
                var old = decision.Target!;
                var found = DefinitionReader.Find(target, old.Key) ??
                    throw new InvalidOperationException("교체 대상 정의가 없어졌습니다: " + old.Name);
                var id = found.Def.Id;
                if (target.GetElement(id)?.UniqueId != old.ElementUniqueId)
                    throw new InvalidOperationException("교체 대상 정의가 바뀌었습니다: " + old.Name);
                // 비공유 정의는 BindingMap.Remove가 false를 반환할 수 있다.
                // 임시 삭제를 되돌려 종속 요소를 확인하고, 모델 요소가 없다면 실제 교체한다.
                ICollection<ElementId> affected;
                using (var probe = new SubTransaction(target))
                {
                    probe.Start();
                    affected = target.Delete(id);
                    probe.RollBack();
                }
                if (!affected.Any(d => d.Value == id.Value))
                    throw new InvalidOperationException("기존 정의를 제거할 수 없습니다: " + old.Name);
                foreach (var dependentId in affected.Where(d => d.Value != id.Value))
                {
                    var dependent = target.GetElement(dependentId);
                    if (dependent == null || dependent.GetType() != typeof(Element) || dependent.Category != null)
                        throw new InvalidOperationException("기존 매개변수 교체에 모델 요소의 삭제가 동반될 수 있어 중단했습니다: " + old.Name);
                }
                var deleted = target.Delete(id);
                if (!deleted.Select(d => d.Value).ToHashSet().SetEquals(affected.Select(d => d.Value)))
                    throw new InvalidOperationException("임시 검사와 실제 정의 삭제 영향이 달라 이번 묶음을 중단했습니다: " + old.Name);
            }
            if (ids.Count > 0)
            {
                target.Regenerate();
                using var options = new CopyPasteOptions();
                options.SetDuplicateTypeNamesHandler(new KeepDestinationTypes());
                var copied = ElementTransformUtils.CopyElements(source, ids, target, Transform.Identity, options);
                if (copied.Count != ids.Count || copied.Any(id => target.GetElement(id) is not ParameterElement))
                    throw new InvalidOperationException("전송 중 승인한 프로젝트 매개변수 정의 이외의 요소가 복사되었습니다.");
            }
        }
        void Verify()
        {
            var after = DefinitionReader.Read(target, false);
            if (after.Definitions.Count != targetNow.Definitions.Count + additions.Count)
                throw new InvalidOperationException("전송 뒤 프로젝트 매개변수 정의 개수가 승인한 결과와 다릅니다.");
            var modifiedNames = settings.Concat(replacements).Select(d => d.Source.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var original in targetNow.Definitions.Where(d => !modifiedNames.Contains(d.Name)))
                if (!after.Definitions.Any(d => d.ElementUniqueId == original.ElementUniqueId &&
                    d.ElementId == original.ElementId && ProjectParameterTransfer.SameDefinition(d, original)))
                    throw new InvalidOperationException("전송 중 대상의 기존 프로젝트 매개변수가 변경됐습니다: " + original.Name);
            foreach (var decision in additions.Concat(settings).Concat(replacements))
                if (after.Definitions.Count(d => ProjectParameterTransfer.SameDefinition(d, decision.Source)) != 1)
                    throw new InvalidOperationException("전송한 프로젝트 매개변수의 설정이나 카테고리 연결이 기준과 다릅니다: " + decision.Source.Name);
        }
        return new Plan(additions.Count + settings.Count + replacements.Count, Set, Verify);
    }

    private sealed class KeepDestinationTypes : IDuplicateTypeNamesHandler
    {
        public DuplicateTypeAction OnDuplicateTypeNamesFound(DuplicateTypeNamesHandlerArgs args) => DuplicateTypeAction.UseDestinationTypes;
    }
}
