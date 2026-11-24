using Autodesk.Revit.DB;
using SunnyParameterManager.Core;
using RevitApplication = Autodesk.Revit.ApplicationServices.Application;

namespace SunnyParameterManager.Bridge;

// 프로젝트 표준 전송과 같은 결과: 다른 모델에서 조회한 같은 공유 매개변수(GUID·이름·자료형·그룹)를
// 프로젝트 정보 인스턴스 매개변수로 연결한 뒤 값을 넣는다. 기존 매개변수·정의는 바꾸지 않는다.
internal static class SharedParameterAdder
{
    private const string TextSpec = "autodesk.spec:spec.string-2.0.0";

    public static ModelEngine.WriteAction Prepare(RevitApplication app, Document doc, ParameterReader reader, ValueEdit edit)
    {
        var spec = edit.AddParameter!;
        var expected = edit.Original;
        if (!Guid.TryParse(spec.Guid, out var guid) || !string.Equals(expected.ParameterKey, "guid:" + guid.ToString("D"), StringComparison.OrdinalIgnoreCase) ||
            spec.Name != expected.Name || spec.Name.Length == 0 || spec.DataType != TextSpec || expected.DataType != TextSpec || expected.Storage != "String" ||
            expected.Scope != "프로젝트 정보" || edit.Mode != "입력")
            throw new InvalidOperationException("매개변수 추가 요청의 식별 정보가 일치하지 않습니다: " + expected.Name);
        var info = doc.ProjectInformation ?? throw new InvalidOperationException("프로젝트 정보가 없습니다.");
        if (info.UniqueId != expected.OwnerId) throw new InvalidOperationException("조회 당시 프로젝트 정보와 현재 요소가 다릅니다. 다시 조회하세요.");
        if (SharedParameterElement.Lookup(doc, guid) != null)
            throw new InvalidOperationException($"조회 후 같은 공유 매개변수({spec.Name})가 이미 프로젝트에 있습니다. 추가하지 않습니다. 다시 조회하세요.");
        if (info.GetParameters(spec.Name).Count > 0)
            throw new InvalidOperationException($"프로젝트 정보에 같은 이름의 다른 매개변수({spec.Name})가 있습니다. 추가하지 않습니다.");
        var group = spec.Group == SharedParameterAddition.OtherGroup ? new ForgeTypeId("") : new ForgeTypeId(spec.Group);
        if (spec.Group != SharedParameterAddition.OtherGroup && !ParameterUtils.IsBuiltInGroup(group)) throw new InvalidOperationException("매개변수 그룹을 확인할 수 없습니다: " + spec.Group);
        if (doc.IsWorkshared && WorksharingUtils.GetCheckoutStatus(doc, info.Id) == CheckoutStatus.OwnedByOtherUser)
            throw new InvalidOperationException("다른 사용자가 프로젝트 정보를 소유하고 있습니다.");
        var error = EditPlanner.Validate(expected, edit.Value);
        if (error.Length > 0) throw new InvalidOperationException(spec.Name + ": " + error);
        var value = edit.Value;
        var category = Category.GetCategory(doc, BuiltInCategory.OST_ProjectInformation) ?? throw new InvalidOperationException("프로젝트 정보 카테고리를 찾을 수 없습니다.");

        return new ModelEngine.WriteAction(() =>
        {
            var previous = app.SharedParametersFilename;
            var temp = Path.Combine(Path.GetTempPath(), "SunnyParameterManager-" + Guid.NewGuid().ToString("N") + ".txt");
            try
            {
                File.WriteAllText(temp, "");
                app.SharedParametersFilename = temp;
                var file = app.OpenSharedParameterFile() ?? throw new InvalidOperationException("임시 공유 매개변수 파일을 열 수 없습니다.");
                var options = new ExternalDefinitionCreationOptions(spec.Name, SpecTypeId.String.Text) { GUID = guid };
                var definition = file.Groups.Create("Sunny").Definitions.Create(options) as ExternalDefinition ?? throw new InvalidOperationException("공유 매개변수 정의를 만들 수 없습니다.");
                var categories = app.Create.NewCategorySet(); categories.Insert(category);
                if (!doc.ParameterBindings.Insert(definition, app.Create.NewInstanceBinding(categories), group))
                    throw new InvalidOperationException("Revit이 프로젝트 정보에 공유 매개변수를 추가하지 않았습니다: " + spec.Name);
            }
            finally
            {
                try { app.SharedParametersFilename = previous; } catch { }
                try { File.Delete(temp); } catch { }
            }
            var added = doc.ProjectInformation.get_Parameter(guid) ?? throw new InvalidOperationException("추가한 매개변수를 프로젝트 정보에서 찾을 수 없습니다.");
            if (added.IsReadOnly || !added.Set(value)) throw new InvalidOperationException("Revit이 추가한 매개변수 값 입력을 거부했습니다: " + spec.Name);
        }, () =>
        {
            var element = SharedParameterElement.Lookup(doc, guid) ?? throw new InvalidOperationException("확정 후 추가한 공유 매개변수가 없습니다: " + spec.Name);
            if (element.GetDefinition().Name != spec.Name) throw new InvalidOperationException("확정 후 공유 매개변수 이름이 다릅니다: " + spec.Name);
            var p = doc.ProjectInformation.get_Parameter(guid) ?? throw new InvalidOperationException("확정 후 프로젝트 정보에 매개변수가 없습니다: " + spec.Name);
            var verified = reader.Read(doc.ProjectInformation, p);
            if (verified.DataType != TextSpec || verified.Storage != "String" || verified.ParameterGroup != spec.Group || ParameterReader.Raw(p) != value)
                throw new InvalidOperationException("확정 후 추가한 매개변수의 자료형·그룹·값이 요청과 달라 이번 묶음을 취소합니다: " + spec.Name);
            if (doc.ParameterBindings.get_Item(element.GetDefinition()) is not InstanceBinding binding || binding.Categories.Size != 1 || !binding.Categories.Contains(Category.GetCategory(doc, BuiltInCategory.OST_ProjectInformation)))
                throw new InvalidOperationException("확정 후 매개변수 연결이 프로젝트 정보 인스턴스가 아닙니다: " + spec.Name);
        });
    }
}
