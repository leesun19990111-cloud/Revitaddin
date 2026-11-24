using Autodesk.Revit.DB;
using SunnyParameterManager.Bridge;
using SunnyParameterManager.Core;

internal static class Program
{
    private static int _count;
    private static void Check(bool value, string name) { if (!value) throw new Exception("FAIL " + name); _count++; Console.WriteLine("PASS " + name); }
    private static void Reject(Action action, string name)
    {
        try { action(); } catch (InvalidOperationException) { Check(true, name); return; }
        throw new Exception("FAIL 차단하지 않음: " + name);
    }
    private sealed class Fixture
    {
        public Document Doc { get; } = new();
        public InternalDefinition Keeper { get; }
        public InternalDefinition Source { get; }
        public InternalDefinition Structure { get; }
        public InternalDefinition Other { get; }
        public Fixture(bool shared = false)
        {
            Keeper = Add("건축물번호", 10, false); Source = Add("건축물 번호", 11, shared);
            Structure = Add("구조", 12, false); Other = Add("공항코드", 13, false);
            Doc.Elements.Add(new() { Id = new(999), Category = new() { Id = new(2), BuiltInCategory = BuiltInCategory.OST_Walls } });
            Doc.Elements.Add(new ElementType { Id = new(998), Category = new() { Id = new(2), BuiltInCategory = BuiltInCategory.OST_Walls } });
        }
        public InternalDefinition Add(string name, long id, bool shared)
        {
            ParameterElement element = shared ? new SharedParameterElement() : new ParameterElement(); element.Id = new(id); Doc.Elements.Add(element);
            var definition = new InternalDefinition { Id = element.Id, Name = name };
            var key = shared ? "guid:" + ((SharedParameterElement)element).GuidValue.ToString("D") : "id:" + id;
            Doc.ParameterBindings.Items.Add(definition, new() { Categories = [Doc.ProjectInformation.Category] });
            Doc.ProjectInformation.Parameters.Add(new() { Definition = definition, Key = key, Value = name + "값" });
            return definition;
        }
        public List<ValueEdit> Plan()
        {
            var rules = new RuleSet { Version = 5, Name = "안전삭제", Spec = new() { Source = "시험", SourceSha256 = new string('a', 64), RemoveOnly = true, RemovalSafetyVersion = 1,
                RemovalTargets = [new() { Name = "건축물 번호", KeepName = "건축물번호", Categories = ["OST_ProjectInformation"] }, new() { Name = "구조", Categories = ["OST_ProjectInformation"] }] } };
            return SpecRulePlanner.Plan(rules, [new() { Path = "사본.rvt", Title = "사본", Definitions = DefinitionReader.Read(Doc) }], []).Edits;
        }
        public Parameter P(InternalDefinition def) => Doc.ProjectInformation.get_Parameter(def)!;
        public void Apply(List<ValueEdit> edits)
        {
            var writes = DefinitionRemoval.Prepare(Doc, edits);
            foreach (var write in writes) write.Set();
            foreach (var write in writes) write.Verify();
        }
    }
    public static int Main()
    {
        try
        {
            Reject(() => new FilteredElementCollector(new Document()).ToElementIds(), "Revit 대역도 필터 없는 수집을 거부");
            var f = new Fixture(); var edits = f.Plan();
            Check(edits.Count == 2 && edits.All(e => e.DefinitionFix!.Removal!.Source.ValueCount == 1), "실제 정의 리더가 두 삭제 대상의 기존 값·지문을 수집");
            f.Apply(edits);
            Check(f.Doc.ParameterBindings.Items.Keys.Select(d => d.Name).ToHashSet().SetEquals(["건축물번호", "공항코드"]) && f.P(f.Keeper).Value == "건축물번호값" && f.P(f.Other).Value == "공항코드값", "실제 삭제 실행부는 두 정의만 제거·유지 값 불변");
            f = new(true); edits = f.Plan(); f.Apply(edits);
            Check(f.Doc.GetElement(f.Source.Id) is SharedParameterElement && !f.Doc.ParameterBindings.Items.ContainsKey(f.Source), "공유 매개변수는 연결만 제거·공유 정의 강제 삭제 없음");
            foreach (var field in new[] { "이름", "카테고리", "자료형", "그룹", "바인딩", "버전", "원래 값", "남길 값", "남길 정의" })
            {
                f = new(); edits = f.Plan();
                switch (field)
                {
                    case "이름": f.Source.Name = "다른 항목"; break;
                    case "카테고리": f.Doc.ParameterBindings.Items[f.Source].Categories.Add(new() { BuiltInCategory = BuiltInCategory.OST_Walls }); break;
                    case "자료형": f.Source.DataType = new("다른 자료형"); break;
                    case "그룹": f.Source.Group = new("다른 그룹"); break;
                    case "바인딩": f.Doc.ParameterBindings.Items[f.Source] = new TypeBinding { Categories = [f.Doc.ProjectInformation.Category] }; break;
                    case "버전": f.Doc.GetElement(f.Source.Id)!.VersionGuid = Guid.NewGuid(); break;
                    case "원래 값": f.P(f.Source).Value = "다른 값"; break;
                    case "남길 값": f.P(f.Keeper).Value = "다른 값"; break;
                    case "남길 정의": f.Doc.ParameterBindings.Items.Remove(f.Keeper); break;
                }
                Reject(() => DefinitionRemoval.Prepare(f.Doc, edits), "쓰기 전 조회 근거 변경 차단: " + field);
                Check(f.Doc.ParameterBindings.Items.ContainsKey(f.Source), "검사 실패 시 삭제 시작 없음: " + field);
            }
            foreach (var id in new long[] { 1, 10, 11, 12 })
            {
                f = new(); edits = f.Plan(); f.Doc.IsWorkshared = true; f.Doc.OtherOwned.Add(id);
                Reject(() => DefinitionRemoval.Prepare(f.Doc, edits), "다른 사용자 소유 차단: " + id);
            }
            f = new(); f.Doc.ParameterBindings.Items[f.Structure].Categories.Add(new() { BuiltInCategory = BuiltInCategory.INVALID, Id = new(777) });
            Check(f.Plan().All(e => e.Original.Name != "구조"), "알 수 없는 카테고리도 버리지 않고 단독 조건에서 제외");
            f = new(); edits = f.Plan(); var writes = DefinitionRemoval.Prepare(f.Doc, edits); f.P(f.Source).Value = "쓰기 직전 변경";
            Reject(writes[0].Set, "준비 이후 쓰기 직전 값 변경 재확인");
            f = new(); edits = f.Plan(); f.Doc.ParameterBindings.RejectRemove = true;
            Reject(() => f.Apply(edits), "Revit 연결 제거 거부 전파");
            f = new(); edits = f.Plan(); f.Doc.CascadeDelete = true;
            Reject(() => f.Apply(edits), "종속 요소 동반 삭제 차단");
            foreach (var mutation in new[] { "다른 값", "남길 값", "다른 정의", "추가 요소", "삭제 요소", "추가 유형", "삭제 유형", "삭제 대상 재등장" })
            {
                f = new(); edits = f.Plan(); writes = DefinitionRemoval.Prepare(f.Doc, edits);
                foreach (var w in writes) w.Set();
                switch (mutation)
                {
                    case "다른 값": f.P(f.Other).Value = "자동 변경"; break;
                    case "남길 값": f.P(f.Keeper).Value = "자동 변경"; break;
                    case "다른 정의": f.Other.Group = new("자동 변경"); break;
                    case "추가 요소": f.Doc.Elements.Add(new() { Id = new(888) }); break;
                    case "삭제 요소": f.Doc.Elements.RemoveAll(e => e.Id.Value == 999); break;
                    case "추가 유형": f.Doc.Elements.Add(new ElementType { Id = new(887) }); break;
                    case "삭제 유형": f.Doc.Elements.RemoveAll(e => e.Id.Value == 998); break;
                    case "삭제 대상 재등장": f.Add("구조", 12, false); break;
                }
                Reject(writes[0].Verify, "확정 후 승인 외 변경 차단: " + mutation);
            }
            f = new(); edits = f.Plan();
            Reject(() => DefinitionRemoval.Prepare(f.Doc, [edits[0], edits[0]]), "중복 삭제 요청 차단");
            Reject(() => DefinitionRemoval.Prepare(f.Doc, [edits[0], new()]), "일반 편집 혼합 요청 차단");
            edits[0].DefinitionFix!.Removal = null;
            Reject(() => DefinitionRemoval.Prepare(f.Doc, edits), "구버전 근거 없는 요청 차단");
            Console.WriteLine($"TOTAL {_count} PASS · 실제 Revit DB·저장·동기화 검증은 별도 필요"); return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
