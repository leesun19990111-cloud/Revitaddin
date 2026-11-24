using SunnyParameterManager.Core;
using System.Text.Json;
using System.IO;

internal static class SpecRuleTests
{
    public static void Run(Action<bool, string> check, Action<Action, string> reject)
    {
        const string Text = "autodesk.spec:spec.string-2.0.0", Bool = "autodesk.spec:spec.bool-1.0.0", Length = "autodesk.spec.aec:length-2.0.0";
        const string Other = SharedParameterAddition.OtherGroup, IdData = "autodesk.parameter.group:identityData-1.0.0", TextGroup = "autodesk.parameter.group:text-1.0.0";
        const string Gb = "cf5750fe-0000-0000-0000-000000000001", Gs = "aaaaaaaa-0000-0000-0000-000000000002", Gx = "bbbbbbbb-0000-0000-0000-000000000003";
        var all = new List<string> { "OST_ProjectInformation", "OST_Rooms", "OST_Walls", "OST_Views" };
        ParameterDefinitionInfo Def(string name, string guid, string type, string group, string binding, int values, params string[] cats) => new()
        {
            Name = name, Guid = guid, Key = guid.Length > 0 ? "guid:" + guid : "id:" + Math.Abs(name.GetHashCode()), ElementId = Math.Abs(name.GetHashCode()).ToString(),
            ElementUniqueId = "u-" + name + guid, DataType = type, Group = group, Binding = binding, ValueCount = values, Categories = [.. cats],
        };
        ModelSnapshot Model(string title, params ParameterDefinitionInfo[] defs) => new()
        {
            Path = @"C:\시험\" + title + ".rvt", Title = title, Identity = title,
            Definitions = new() { AllCategories = [.. all], Definitions = [.. defs], CategoryNames = all.ToDictionary(c => c, c => c[4..]) },
        };
        RuleSet Rules() => new()
        {
            Version = 5, Name = "시험 속성규격",
            Spec = new()
            {
                Source = "시험.xlsx", SourceSha256 = new string('a', 64), MergeDuplicates = true, AddMissing = true,
                Parameters =
                [
                    new() { Name = "건축물번호", Legacy = ["건축물 번호"], DataType = "문자", Group = "기타", Categories = ["OST_ProjectInformation"], SourceRow = 10 },
                    new() { Name = "건축물구조", Legacy = ["구조"], DataType = "문자", Group = "문자", Categories = ["OST_ProjectInformation"], SourceRow = 4 },
                    new() { Name = "구조", DataType = "예/아니요", Group = "ID 데이터", AllCategories = true, SourceRow = 31 },
                    new() { Name = "천장높이", Legacy = ["천장고"], DataType = "길이", Group = "치수", Categories = ["OST_Rooms"], SourceRow = 63 },
                    new() { Name = "제작정보", Binding = "유형", DataType = "문자", Group = "기타", AllCategories = true, ExcludeCategories = ["OST_ProjectInformation"], SourceRow = 49 },
                ],
            },
        };

        var rules = Rules();
        check(RulePlanner.Parse(JsonSerializer.Serialize(rules)).Spec!.Parameters.Count == 5, "버전5 JSON 왕복");
        var query = QueryPlan.FromRules(rules);
        check(query.IncludeDefinitions && query.Scopes.SequenceEqual(["프로젝트 정보"]), "버전5 정의 조회 계획");
        check(!new QueryPlan { Scopes = ["프로젝트 정보"], Names = ["건축물번호"] }.Covers(query), "정의 미조회 결과는 버전5를 충족하지 않음");
        reject(() => QueryPlan.RequireCoverage([new ModelSnapshot { Title = "a", Query = query }], rules), "정의 목록 없는 조회 결과 차단");

        // 기준 모델: 규격대로 된 공유 매개변수
        var good = Model("기준",
            Def("건축물번호", Gb, Text, Other, "인스턴스", 1, "OST_ProjectInformation"),
            Def("건축물구조", Gs, Text, TextGroup, "인스턴스", 1, "OST_ProjectInformation"),
            Def("구조", Gx, Bool, IdData, "인스턴스", 0, [.. all]),
            Def("제작정보", "dddddddd-0000-0000-0000-000000000004", Text, Other, "유형", 5, "OST_Rooms", "OST_Walls", "OST_Views"));
        // 문제 모델: 띄어쓰기 중복, 이전 이름 구조(텍스트), 그룹 다름, 카테고리 부족, 데이터 유형 다름
        var bad = Model("문제",
            Def("건축물 번호", "", Text, Other, "인스턴스", 1, "OST_ProjectInformation"),
            Def("구조", "", Text, TextGroup, "인스턴스", 1, "OST_ProjectInformation"),
            Def("구조", Gx, Bool, Other, "인스턴스", 0, "OST_Walls"),
            Def("천장고", "", Length, Other, "인스턴스", 3, "OST_Rooms"),
            Def("제작정보", "dddddddd-0000-0000-0000-000000000004", Text, Other, "인스턴스", 0, "OST_Walls"),
            Def("비고A", "", Text, Other, "인스턴스", 0, "OST_Walls"), Def("비고 a", "", Text, Other, "인스턴스", 0, "OST_Walls"));
        var plan = SpecRulePlanner.Plan(rules, [good, bad], []);
        var edits = plan.Edits.Where(e => e.Model == "문제").ToList();
        string Actions(string name) => string.Join(",", edits.Where(e => e.Field == name).Select(e => e.DefinitionFix!.Action).OrderBy(x => x));
        check(plan.Edits.All(e => e.Model == "문제"), "규격대로 된 모델은 정리 없음");
        check(Actions("건축물번호") == "매개변수 추가,중복 병합·제거", "띄어쓰기 중복: 기준 GUID 추가 후 병합");
        var add = edits.Single(e => e.Field == "건축물번호" && e.DefinitionFix!.Action == SpecRulePlanner.ActionAdd).DefinitionFix!;
        check(add.Guid == Gb && add.Categories.SequenceEqual(["OST_ProjectInformation"]) && add.Group == Other && add.DataType == Text, "추가 매개변수 GUID·카테고리·그룹·자료형");
        var merge = edits.Single(e => e.Field == "건축물번호" && e.DefinitionFix!.Action == SpecRulePlanner.ActionMerge).DefinitionFix!;
        check(merge.SourceName == "건축물 번호" && merge.TargetKey == "guid:" + Gb && merge.ValueCount == 1, "병합 대상과 기준 연결");
        check(Actions("건축물구조") == "매개변수 추가,중복 병합·제거", "이전 이름 구조(텍스트)를 건축물구조로 병합");
        check(Actions("구조") == "그룹 변경,카테고리 추가", "예/아니요 구조는 별도 규격으로 그룹·all 카테고리 정리");
        check(edits.Single(e => e.Field == "구조" && e.DefinitionFix!.Action == SpecRulePlanner.ActionCategories).DefinitionFix!.Categories.SequenceEqual(all.OrderBy(x => x, StringComparer.Ordinal)), "all = 연결 가능한 모든 카테고리");
        check(Actions("천장높이") == "" && plan.Notes.Any(n => n.Contains("천장높이") && n.Contains("추가할 GUID가 없습니다")), "기준 GUID 없으면 이전 이름 보고만");
        check(Actions("제작정보") == "유형/인스턴스 전환", "값 없는 인스턴스 → 유형 전환");
        var binding = edits.Single(e => e.Field == "제작정보").DefinitionFix!;
        check(binding.Binding == "유형" && !binding.Categories.Contains("OST_ProjectInformation") && binding.Categories.Contains("OST_Rooms"), "전환 시 all-제외 카테고리 반영");
        check(plan.Notes.Any(n => n.Contains("규격 외 중복") && n.Contains("비고A")), "규격 외 띄어쓰기·대소문자 중복 보고");
        check(plan.Notes[0].Contains("정리 예정"), "요약 첫 줄");
        check(edits.All(e => e.Original.Scope == SpecRulePlanner.DefinitionScope && e.Reason.Contains("시험.xlsx")), "정의 정리 행 표시와 근거");
        check(edits.Select(e => e.Original.Key).Distinct().Count() == edits.Count, "정의 정리 키 고유");

        // 값이 있으면 전환·병합하지 않고 보고만
        var valued = Model("값있음", Def("제작정보", "dddddddd-0000-0000-0000-000000000004", Text, Other, "인스턴스", 7, [.. all]),
            Def("건축물번호", Gb, Text, Other, "인스턴스", 1, "OST_ProjectInformation"), Def("건축물 번호", "", Text, Other, "유형", 2, "OST_ProjectInformation"));
        var p2 = SpecRulePlanner.Plan(rules, [good, valued], []);
        check(!p2.Edits.Any(e => e.Model == "값있음" && e.Field == "제작정보") && p2.Notes.Any(n => n.Contains("값이 사라지므로")), "값 있는 유형/인스턴스 불일치는 보고만");
        check(!p2.Edits.Any(e => e.Model == "값있음" && e.DefinitionFix!.Action == SpecRulePlanner.ActionMerge) && p2.Notes.Any(n => n.Contains("옮길 수 없습니다")), "유형/인스턴스 다른 중복 값은 이동하지 않음");
        var unknown = Model("확인불가", Def("건축물번호", Gb, Text, Other, "인스턴스", 1, "OST_ProjectInformation"), Def("건축물 번호", "", Text, Other, "인스턴스", -1, "OST_ProjectInformation"));
        check(!SpecRulePlanner.Plan(rules, [unknown], []).Edits.Any(), "값 존재 확인 불가 정의는 제거하지 않음");

        // 데이터 유형 불일치는 보고만
        var wrongType = Model("자료형", Def("천장높이", "", Text, "autodesk.parameter.group:geometry-1.0.0", "인스턴스", 0, "OST_Rooms"));
        check(SpecRulePlanner.Plan(rules, [wrongType], []).Notes.Any(n => n.Contains("데이터 유형이 규격과 다릅니다")), "데이터 유형 불일치 보고");

        // GUID가 같은 수로 갈리면 기준을 정하지 않는다
        var a = Model("A", Def("건축물번호", Gb, Text, Other, "인스턴스", 0, "OST_ProjectInformation"));
        var b = Model("B", Def("건축물번호", Gx, Text, Other, "인스턴스", 0, "OST_ProjectInformation"));
        var c = Model("C");
        var p3 = SpecRulePlanner.Plan(rules, [a, b, c], []);
        check(p3.Notes.Any(n => n.Contains("규격 GUID 확인 필요")) && !p3.Edits.Any(e => e.Model == "C" && e.Field == "건축물번호"), "GUID 동률이면 추가하지 않음");

        // 허용 끄면 보고만
        var off = Rules(); off.Spec!.MergeDuplicates = false; off.Spec.AddMissing = false;
        check(!SpecRulePlanner.Plan(off, [good, bad], []).Edits.Any(e => e.DefinitionFix!.Action is SpecRulePlanner.ActionAdd or SpecRulePlanner.ActionMerge), "추가·병합 허용 끄면 보고만");

        // 혼합·검증
        reject(() => SpecRulePlanner.Plan(rules, [good, bad], [new ValueEdit { Original = new() { ModelPath = "x" } }]), "값 변경과 정의 정리 혼합 차단");
        reject(() => RulePlanner.Plan(rules, [], [], false), "버전5는 행 기반 평가 차단");
        reject(() => RulePlanner.Plan(new RuleSet { Version = 1, Name = "v1", Rules = [new() { Name = "r", Match = new() { Scope = "프로젝트 정보" }, Set = [new() { Name = "a", DataType = "t", Storage = "String", Value = "1" }] }] }, [], [plan.Edits[0]], false), "다른 룰셋에 정의 정리 혼합 차단");
        reject(() => SpecRulePlanner.Plan(rules, [new ModelSnapshot { Title = "미조회" }], []), "정의 미조회 모델 차단");
        foreach (var (mutate, label) in new (Action<RuleSet>, string)[]
        {
            (r => r.Spec!.Parameters[0].Categories = ["벽"], "카테고리 이름 형식"),
            (r => r.Spec!.Parameters[1].Name = "건축물번호", "이름 중복"),
            (r => r.Spec!.Parameters[1].Name = "건축물 번호", "띄어쓰기만 다른 이름 중복"),
            (r => r.Spec!.Parameters[0].DataType = "숫자", "알 수 없는 데이터 유형"),
            (r => r.Spec!.Parameters[0].Group = "그래픽", "알 수 없는 그룹"),
            (r => r.Spec!.Parameters[0].AllCategories = true, "all과 개별 카테고리 동시"),
            (r => r.Spec!.Parameters[0].Legacy = ["건축물번호"], "자기 이름을 이전 이름으로"),
            (r => r.Spec!.Parameters[3].Legacy = ["건축물 번호"], "같은 이전 이름 중복"),
            (r => r.Space = new(), "다른 룰셋 혼합"),
            (r => r.Version = 3, "버전 불일치"),
            (r => r.Spec!.SourceSha256 = "x", "출처 해시"),
        })
        {
            var r = Rules(); mutate(r);
            reject(() => RulePlanner.Validate(r), "속성규격 검증 " + label);
        }
        check(SpecRulePlanner.BaseId("autodesk.parameter.group:text-1.0.0") == "autodesk.parameter.group:text" && SpecRulePlanner.GroupLabel(IdData) == "ID 데이터" && SpecRulePlanner.TypeLabel(Bool) == "예/아니요", "식별자 표기 변환");

        // 실제 생성 룰셋(있을 때만): 개인 엑셀/룰셋은 저장소에 넣지 않는다.
        var personal = Environment.GetEnvironmentVariable("SUNNY_SPEC_RULES");
        if (!string.IsNullOrEmpty(personal) && File.Exists(personal))
        {
            var real = RulePlanner.Parse(File.ReadAllText(personal));
            check(real.Version == 5 && real.Spec!.Parameters.Count > 100, "개인 속성규격 룰셋 검증");
            var realPlan = SpecRulePlanner.Plan(real, [good, bad], []);
            check(realPlan.Edits.Any(e => e.Field == "건축물번호" && e.DefinitionFix!.Action == SpecRulePlanner.ActionMerge), "개인 룰셋으로 건축물 번호 병합 계획");
            Console.WriteLine(string.Join("\n", realPlan.Notes.Take(12)));
        }
    }
}

internal static class SpecRemoveOnlyTests
{
    public static void Run(Action<bool, string> check, Action<Action, string> reject)
    {
        const string Text = "autodesk.spec:spec.string-2.0.0", Other = SharedParameterAddition.OtherGroup;
        ParameterDefinitionInfo Def(string name, string key, int values, params string[] cats) => new() { Name = name, Key = key, ElementId = "1", ElementUniqueId = "u" + key, Revision = Guid.NewGuid().ToString("D"), ValueFingerprint = DefinitionRemovalChecks.Fingerprint(new Dictionary<string,string>()), DataType = Text, Group = Other, Binding = "인스턴스", ValueCount = values, Categories = [.. cats] };
        ModelSnapshot Model(string t, params ParameterDefinitionInfo[] d) => new() { Path = t + ".rvt", Title = t, Definitions = new() { AllCategories = ["OST_ProjectInformation", "OST_Rooms"], Definitions = [.. d] } };
        RuleSet Rules() => new() { Version = 5, Name = "삭제", Spec = new() { Source = "x", SourceSha256 = new string('a', 64), RemoveOnly = true, RemovalSafetyVersion = 1,
            RemovalTargets = [new() { Name = "건축물 번호", KeepName = "건축물번호", Categories = ["OST_ProjectInformation"] }, new() { Name = "구조", Categories = ["OST_ProjectInformation"] }] } };
        var both = Model("둘다", Def("건축물번호", "guid:a", 1, "OST_ProjectInformation"), Def("건축물 번호", "id:9", 1, "OST_ProjectInformation"), Def("공항코드", "guid:b", 1, "OST_ProjectInformation"));
        var plan = SpecRulePlanner.Plan(Rules(), [both], []);
        check(plan.Edits.Count == 1 && plan.Edits[0].DefinitionFix!.Action == SpecRulePlanner.ActionRemove && plan.Edits[0].Original.ParameterKey == "id:9" && plan.Edits[0].DefinitionFix!.TargetKey == "guid:a", "띄어쓰기 매개변수만 삭제, 건축물번호 유지");
        check(SpecRulePlanner.Plan(Rules(), [Model("남길것없음", Def("건축물 번호", "id:9", 1, "OST_ProjectInformation"))], []).Edits.Count == 0, "건축물번호 없으면 삭제 안 함");
        check(SpecRulePlanner.Plan(Rules(), [Model("남길것룸", Def("건축물번호", "id:1", 1, "OST_Rooms"), Def("건축물 번호", "id:9", 1, "OST_ProjectInformation"))], []).Edits.Count == 0, "남길 정의가 프로젝트 정보에 없으면 삭제 안 함");
        check(SpecRulePlanner.Plan(Rules(), [Model("룸포함", Def("건축물번호", "guid:a", 0, "OST_ProjectInformation"), Def("건축물 번호", "id:9", 0, "OST_ProjectInformation", "OST_Rooms"))], []).Edits.Count == 0, "프로젝트 정보 외 카테고리 있으면 삭제 안 함");
        check(SpecRulePlanner.Plan(Rules(), [Model("규격만", Def("건축물번호", "guid:a", 0, "OST_ProjectInformation"))], []).Edits.Count == 0, "삭제 대상 없으면 변경 없음");
        var bad = Rules(); bad.Spec!.MergeDuplicates = true;
        reject(() => RulePlanner.Validate(bad), "삭제 전용과 병합 혼합 금지");
        var old = Rules(); old.Spec!.RemovalSafetyVersion = 0;
        reject(() => RulePlanner.Validate(old), "이전 안전검증 없는 삭제 룰셋 거부");
        var variants = Model("다른공백", Def("건축물번호", "id:1", 0, "OST_ProjectInformation"), Def("건축물  번호", "id:2", 0, "OST_ProjectInformation"), Def("건축물 번호 ", "id:3", 0, "OST_ProjectInformation"), Def("구 조", "id:4", 0, "OST_ProjectInformation"));
        check(SpecRulePlanner.Plan(Rules(), [variants], []).Edits.Count == 0, "정확한 이름 이외 공백 변형은 삭제하지 않음");
        var structure = Model("구조삭제", Def("구조", "id:4", 1, "OST_ProjectInformation"));
        check(SpecRulePlanner.Plan(Rules(), [structure], []).Edits.Single().DefinitionFix!.Removal!.Keeper == null, "프로젝트 정보 단독 구조는 남길 이름 조건 없이 삭제");
        var structural = Model("구조유지", Def("구조", "id:4", 1, "OST_ProjectInformation", "OST_Walls"));
        check(SpecRulePlanner.Plan(Rules(), [structural], []).Edits.Count == 0, "다른 카테고리 포함 구조는 유지");
        var two = Model("둘삭제", Def("건축물번호", "id:1", 1, "OST_ProjectInformation"), Def("건축물 번호", "id:2", 1, "OST_ProjectInformation"), Def("구조", "id:3", 1, "OST_ProjectInformation"));
        var deletion = SpecRulePlanner.Plan(Rules(), [two], []);
        check(deletion.Edits.Count == 2 && deletion.Edits.All(e => e.DefinitionFix!.Removal!.AllowedCategories.SequenceEqual(["OST_ProjectInformation"])), "두 삭제 대상에 개별 정의·값·카테고리 근거 포함");
        check(SpecRulePlanner.Plan(Rules(), [two], deletion.Edits.Take(1)).Edits.Count == 2, "같은 삭제 계획 재검토 및 부분 선택 허용");
        reject(() => SpecRulePlanner.Plan(Rules(), [two], [new() { Original = new() { ModelPath = two.Path, OwnerId = "other", ParameterKey = "id:99" }, DefinitionFix = new() { Action = SpecRulePlanner.ActionGroup } }]), "이전 다른 정의 정리가 삭제 계획에 누적되지 않음");
        var changed = JsonSerializer.Deserialize<ValueEdit>(JsonSerializer.Serialize(deletion.Edits[0]))!; changed.DefinitionFix!.Removal!.Source.Group = "변조";
        reject(() => SpecRulePlanner.Plan(Rules(), [two], [changed]), "이전 삭제 근거 변조도 누적 거부");
        two.Definitions!.Definitions[1].ValueFingerprint = "";
        check(SpecRulePlanner.Plan(Rules(), [two], []).Edits.Count == 1, "이전 조회로 안전 지문 없는 대상만 보류");
        check(QueryPlan.FromRules(Rules()).Names.ToHashSet().SetEquals(["건축물 번호", "건축물번호", "구조"]), "삭제 조회는 정확한 삭제·유지 이름 모두 포함");
        var d0 = Def("구조", "id:4", 0, "OST_ProjectInformation"); var dc = DefinitionRemovalChecks.Copy(d0);
        DefinitionRemovalChecks.CheckFresh(d0, dc, new Dictionary<string,string>());
        check(DefinitionRemovalChecks.Fingerprint(new Dictionary<string,string> { ["b"] = "2", ["a"] = "1" }) == DefinitionRemovalChecks.Fingerprint(new Dictionary<string,string> { ["a"] = "1", ["b"] = "2" }), "기존 값 지문은 수집 순서와 무관");
        dc.Categories.Add("OST_Rooms");
        reject(() => DefinitionRemovalChecks.CheckFresh(d0, dc, new Dictionary<string,string>()), "삭제 전 카테고리 추가 차단");
        dc = DefinitionRemovalChecks.Copy(d0); dc.Name = "다른 항목";
        reject(() => DefinitionRemovalChecks.CheckFresh(d0, dc, new Dictionary<string,string>()), "삭제 전 이름 교체 차단");
        dc = DefinitionRemovalChecks.Copy(d0); dc.Revision = Guid.NewGuid().ToString("D");
        reject(() => DefinitionRemovalChecks.CheckFresh(d0, dc, new Dictionary<string,string>()), "정의 버전 변경 차단");
        reject(() => DefinitionRemovalChecks.CheckFresh(d0, d0, new Dictionary<string,string> { ["p"] = "외부 변경" }), "기존 값 변경 차단");
        var personal = Environment.GetEnvironmentVariable("SUNNY_REMOVE_RULES");
        if (!string.IsNullOrEmpty(personal)) check(SpecRulePlanner.Plan(RulePlanner.Parse(File.ReadAllText(personal)), [both], []).Edits.Count == 1, "개인 삭제 룰셋 검증");
    }
}
