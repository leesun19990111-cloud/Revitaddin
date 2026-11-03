using Autodesk.Revit.DB;
using SunnyParameterManager.Bridge;
using SunnyParameterManager.Core;

internal static class TypeCodeReaderTests
{
    public static void Run(Action<bool, string> check)
    {
        void Reject(Action action, string label)
        {
            try { action(); check(false, label); }
            catch (InvalidOperationException) { check(true, label); }
        }
        var doc = new Document();
        var type = new ElementType { Name = "KAC-A-WA123_벽-T200", Category = new() { CategoryType = CategoryType.Annotation, Name = "주석", Id = new(23) } };
        var unrelated = new ElementType { Name = "KAC-A-WA1234_다른벽", Category = type.Category };
        doc.Elements.Add(type); doc.Elements.Add(unrelated);
        var rules = new RuleSet { Version = 7, Name = "세 자리 유형 코드 보정", TypeCodeFix = true };
        ModelSnapshot Read()
        {
            var snapshot = new ModelSnapshot { Path = "모델.rvt", Title = "모델" };
            snapshot.Rows = new ParameterReader(doc, snapshot).ReadAll(QueryPlan.FromRules(rules));
            return snapshot;
        }
        var read = Read();
        var plan = TypeCodePlanner.Plan(read.Owners.Concat(read.Rows), []);
        check(read.Owners.Count == 2 && read.Rows.Count(r => r.ParameterKey == CleanupPlanner.NameKey) == 1 &&
              plan.Edits.Single().Value == "KAC-A-WA1230_벽-T200", "주석 포함 모든 유형에서 세 자리 이름만 조회");
        var actions = CleanupWriter.Prepare(doc, new ModelSnapshot { Path = read.Path, Title = read.Title }, plan.Edits, rules);
        foreach (var action in actions) action.Set();
        foreach (var action in actions) action.Verify();
        check(type.Name == "KAC-A-WA1230_벽-T200" && unrelated.Name == "KAC-A-WA1234_다른벽", "이름 한 곳만 변경하고 다른 유형 유지");
        Reject(() => CleanupWriter.Prepare(doc, new ModelSnapshot { Path = read.Path, Title = read.Title }, plan.Edits, rules), "기존 조회를 다시 적용하지 않음");
        type.Name = "KAC-A-WA123_벽-T200";
        read = Read(); plan = TypeCodePlanner.Plan(read.Owners.Concat(read.Rows), []);
        actions = CleanupWriter.Prepare(doc, new ModelSnapshot { Path = read.Path, Title = read.Title }, plan.Edits, rules);
        foreach (var action in actions) action.Set();
        unrelated.Name = "승인되지 않은 변경";
        Reject(() => { foreach (var action in actions) action.Verify(); }, "확정 후 다른 유형 변경 차단");
    }
}
