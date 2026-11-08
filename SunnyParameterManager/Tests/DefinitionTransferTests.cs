using SunnyParameterManager.Core;

internal static class DefinitionTransferTests
{
    public static void Run(Action<bool, string> check, Action<Action, string> reject)
    {
        var rules = RulePlanner.Parse("{\"Version\":10,\"Name\":\"프로젝트 매개변수 표준 전송\",\"ProjectParameterTransfer\":true,\"Rules\":[]}");
        check(rules.ProjectParameterTransfer && QueryPlan.FromRules(rules).TransferOnly, "버전10 전용 룰셋 조회");
        reject(() => RulePlanner.Parse("{\"Version\":10,\"Name\":\"잘못된 혼합\",\"ProjectParameterTransfer\":true,\"Cleanup\":true,\"Rules\":[]}"), "정의 전송과 정리 혼합 차단");
        var guid = Guid.NewGuid().ToString("D");
        var def = new ParameterDefinitionInfo { Name = "업체명", Guid = guid, Key = "guid:" + guid, Revision = "r1",
            DataType = "autodesk.spec:spec.string-2.0.0", Group = "autodesk.parameter.group:identityData-1.0.0", Binding = "인스턴스",
            Categories = ["OST_Walls", "OST_ProjectInformation"] };
        var source = new ModelSnapshot { Path = "source.rvt", CanonicalPath = "source.rvt", Title = "기준", Identity = "source",
            Definitions = new DefinitionSnapshot { Definitions = [def], AllCategories = ["OST_Walls", "OST_ProjectInformation"] } };
        var target = new ModelSnapshot { Path = "target.rvt", CanonicalPath = "target.rvt", Title = "대상", Identity = "target",
            Definitions = new DefinitionSnapshot { AllCategories = ["OST_Walls", "OST_ProjectInformation"] } };
        check(DefinitionTransferPlanner.DefinitionQuery().TransferOnly, "정의 전송은 경량 조회");
        var planned = DefinitionTransferPlanner.PlanDefinitions(source, target, [def]).Single();
        check(planned.Edit?.DefinitionFix?.Guid == guid && planned.Edit.DefinitionFix.Categories.Count == 2, "같은 GUID·카테고리 정의 추가");
        target.Definitions.Definitions.Add(new ParameterDefinitionInfo { Name = def.Name, Guid = guid, Key = def.Key,
            DataType = def.DataType, Group = def.Group, Binding = def.Binding, Categories = [.. def.Categories] });
        check(DefinitionTransferPlanner.PlanDefinitions(source, target, [def]).Single().Status == "이미 있음", "같은 정의는 변경 없음");
        target.Definitions.Definitions[0].Binding = "유형";
        check(DefinitionTransferPlanner.PlanDefinitions(source, target, [def]).Single().Status == "제외", "같은 GUID의 다른 연결 차단");
        target.Definitions.Definitions.Clear();
        target.Definitions.Definitions.Add(new ParameterDefinitionInfo { Name = def.Name, Guid = Guid.NewGuid().ToString("D") });
        check(DefinitionTransferPlanner.PlanDefinitions(source, target, [def]).Single().Status == "제외", "동명 다른 GUID 차단");
        target.Definitions.Definitions.Clear(); target.Definitions.AllCategories.Remove("OST_Walls");
        check(DefinitionTransferPlanner.PlanDefinitions(source, target, [def]).Single().Status == "제외", "없는 카테고리 차단");
        target.Definitions.AllCategories.Add("OST_Walls");
        var general = new ParameterDefinitionInfo { Name = "일반", Key = "id:1", Revision = "r2", Categories = ["OST_Walls"] };
        source.Definitions.Definitions.Add(general);
        check(DefinitionTransferPlanner.PlanDefinitions(source, target, [general]).Single().Status == "제외", "GUID 없는 일반 매개변수 사유 표시");
        reject(() => DefinitionTransferPlanner.PlanDefinitions(source, source, [def]), "기준 파일 자체를 대상으로 전송 금지");
        var query = DefinitionTransferPlanner.ValueQuery([def]);
        check(query.TransferOnly && query.Scopes.Contains("인스턴스") && query.Scopes.Contains("프로젝트 정보") && query.Keys.Contains(def.Key), "선택한 값만 조회");
        ParameterRow Row(string model, string owner) => new() { ModelPath = model, ModelTitle = model, OwnerId = owner,
            Scope = "인스턴스", CategoryKey = "-2000011", Family = "기본벽", TypeName = "W1", ParameterKey = def.Key,
            Name = def.Name, DataType = def.DataType, Storage = "String", Editor = "텍스트", HasValue = true, Raw = model, Display = model };
        source.Rows = [Row(source.Path, "same-uid"), Row(source.Path, "other-uid")];
        target.Rows = [Row(target.Path, "same-uid")];
        var values = DefinitionTransferPlanner.PlanValues(source, target, [def]);
        check(values.Count(v => v.Edit != null) == 1 && values.Count(v => v.Status == "제외") == 1, "인스턴스 동일 UniqueId만 값 전송");
        check(values.Single(v => v.Edit != null).Edit!.RawValue == source.Path, "원시값 복사 계획");
    }
}
