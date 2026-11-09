using Autodesk.Revit.DB;
using SunnyParameterManager.Core;
using System.Globalization;

namespace SunnyParameterManager.Bridge;

// Revit의 문서 간 요소 복사는 비공유 ParameterElement와 바인딩도 함께 복제한다.
// 동명 기존 정의는 덮어쓰지 않으며, 전후 정의 목록이 승인 내용과 다르면 묶음 전체를 취소한다.
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
        var additions = decisions.Where(d => d.Status == "추가 예정").Select(d => d.Source).ToList();
        var ids = additions.Select(d => new ElementId(long.Parse(d.ElementId, CultureInfo.InvariantCulture))).ToList();
        void Set()
        {
            using var options = new CopyPasteOptions();
            options.SetDuplicateTypeNamesHandler(new KeepDestinationTypes());
            var copied = ElementTransformUtils.CopyElements(source, ids, target, Transform.Identity, options);
            if (copied.Count != ids.Count || copied.Any(id => target.GetElement(id) is not ParameterElement))
                throw new InvalidOperationException("전송 중 승인한 프로젝트 매개변수 정의 이외의 요소가 복사되었습니다.");
        }
        void Verify()
        {
            var after = DefinitionReader.Read(target, false);
            if (after.Definitions.Count != targetNow.Definitions.Count + additions.Count)
                throw new InvalidOperationException("전송 뒤 매개변수 정의 개수가 승인한 추가 수와 다릅니다.");
            foreach (var original in targetNow.Definitions)
                if (!after.Definitions.Any(d => d.ElementUniqueId == original.ElementUniqueId &&
                    d.ElementId == original.ElementId && ProjectParameterTransfer.SameDefinition(d, original)))
                    throw new InvalidOperationException("전송 중 대상의 기존 프로젝트 매개변수가 변경됐습니다: " + original.Name);
            foreach (var added in additions)
                if (after.Definitions.Count(d => ProjectParameterTransfer.SameDefinition(d, added)) != 1)
                    throw new InvalidOperationException("새 프로젝트 매개변수의 설정이나 카테고리 연결이 기준과 다릅니다: " + added.Name);
        }
        return new Plan(additions.Count, Set, Verify);
    }

    private sealed class KeepDestinationTypes : IDuplicateTypeNamesHandler
    {
        public DuplicateTypeAction OnDuplicateTypeNamesFound(DuplicateTypeNamesHandlerArgs args) => DuplicateTypeAction.UseDestinationTypes;
    }
}
