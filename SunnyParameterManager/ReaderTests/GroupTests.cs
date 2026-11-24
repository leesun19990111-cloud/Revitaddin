using Autodesk.Revit.DB;
using SunnyParameterManager.Core;
using SunnyParameterManager.Bridge;
using System.Text.Json;

internal static class GroupTests
{
 public static void Run(Action<bool, string> check)
 {
  void Reject(Action action, string name) { bool failed = false; try { action(); } catch (InvalidOperationException) { failed = true; } check(failed, name); }
  var doc = new Document(); var model = new ModelSnapshot { Path = "그룹검증.rvt", Title = "그룹검증" };
  var type = new ElementType { Name = "KAC-A-BM9900_ND-T30" }; type.Parameters.Clear(); doc.Elements.Add(type);
  var group = new Group { Name = "캐노피 트러스" }; group.Parameters.Clear(); doc.Elements.Add(group);
  var owner = new Element { TypeId = type.Id }; owner.Parameters.Clear(); doc.Elements.Add(owner);
  var definition = new InternalDefinition { Name = "건축", Group = GroupTypeId.IdentityData, Spec = SpecTypeId.Boolean.YesNo };
  var p = new Parameter { Id = new(100), Definition = definition, StorageType = StorageType.Integer, Value = "0", HasValue = false }; owner.Parameters.Add(p);
  var reader = new ParameterReader(doc, model);
  ParameterRow Read() => reader.Read(owner, p, false);
  var ungrouped = Read(); check(ungrouped.Editable && ungrouped.GroupEdit == null, "그룹 아닌 요소는 기존 편집 허용");
  owner.GroupId = group.Id;
  var blocked = Read(); check(!blocked.Editable && blocked.GroupEdit!.ElementId == group.Id.Value.ToString() && blocked.BlockReason.Contains(group.Name), "그룹 정렬 매개변수는 그룹명·ID와 함께 차단");
  Reject(() => EditPlanner.Stage([blocked], "예"), "직접 입력도 그룹 제한 우회 불가");
  definition.VariesAcrossGroups = true;
  var allowed = Read(); check(allowed.Editable && allowed.GroupEdit!.CanVary, "기존 그룹별 값 변경 허용 필드만 편집 가능");
  check(EditPlanner.Stage([allowed], "아니요").Count == 1, "값 미지정 false는 기존 false로 오인하여 누락하지 않음");
  EditPlanner.CheckFresh(allowed, Read()); check(true, "동일 그룹·허용 설정 신선도 통과");
  Reject(() => EditPlanner.CheckFresh(ungrouped, allowed), "조회 후 그룹 편입 및 구버전 무근거 승인 차단");
  p.IsReadOnly = true; check(!Read().Editable, "그룹별 허용도 실제 읽기전용 우회 금지"); p.IsReadOnly = false;
  definition.VariesAcrossGroups = false; Reject(() => EditPlanner.CheckFresh(allowed, Read()), "조회 후 그룹별 변경 설정 해제 차단"); definition.VariesAcrossGroups = true;
  owner.GroupId = ElementId.InvalidElementId; Reject(() => EditPlanner.CheckFresh(allowed, Read()), "조회 후 그룹 해제도 재검토 요구"); owner.GroupId = group.Id;
  var group2 = new Group { Name = group.Name }; doc.Elements.Add(group2); owner.GroupId = group2.Id;
  Reject(() => EditPlanner.CheckFresh(allowed, Read()), "이름 같아도 다른 그룹 이동 차단"); owner.GroupId = group.Id;
  p.Id = new(-1002053); check(!Read().Editable && Read().GroupEdit!.Reason.Contains("기본 속성"), "그룹 내부 기본 작업세트는 개별 수정 제외"); p.Id = new(100);
  group.GroupId = group2.Id; check(!Read().Editable && Read().GroupEdit!.Reason.Contains("중첩"), "중첩 그룹은 상위 구속 추정 없이 제외"); group.GroupId = ElementId.InvalidElementId;
  p.Definition = new Definition(); check(!Read().Editable, "변경 허용 정의 확인 불가 시 차단"); p.Definition = definition;
  owner.GroupId = new(99999); check(!Read().Editable && Read().GroupEdit!.Reason.Contains("찾을 수"), "유효하지 않은 그룹 연결도 안전 제외"); owner.GroupId = group.Id;
  owner.FailGroupRead = true; check(!Read().Editable && Read().GroupEdit!.Reason.Contains("확인 실패"), "그룹 조회 오류를 비그룹으로 오인하지 않음"); owner.FailGroupRead = false;
  check(reader.Read(type, p, false).Editable, "유형 매개변수는 그룹 구성원의 개별 값으로 오인하지 않음");
  var restored = JsonSerializer.Deserialize<ParameterRow>(JsonSerializer.Serialize(allowed))!;
  check(restored.GroupEdit == allowed.GroupEdit, "작업 보관 그룹 근거 직렬화 왕복");
  model.Rows = reader.ReadAll(QueryPlan.FromRules(CleanupPlanner.Rules())); model.ConnectCatalogs();
  var edits = CleanupPlanner.Plan(model.Owners.Concat(model.Rows), []).Edits.Where(e => e.Field == "건축").ToList();
  check(edits.Count == 1, "정리 룰셋은 그룹별 허용 ID 필드만 정상 준비");
  var actions = CleanupWriter.Prepare(doc, new() { Path = model.Path }, edits);
  foreach (var a in actions) a.Set(); foreach (var a in actions) a.Verify();
  check(p.Value == "1", "그룹별 허용 ID 필드 정리 Writer 입력·확정 검증");
  owner.GroupId = group2.Id; Reject(() => { foreach (var a in actions) a.Verify(); }, "확정 후 그룹 이동도 검증 실패"); owner.GroupId = group.Id;
  definition.VariesAcrossGroups = false; p.Value = "0";
  model.Rows = reader.ReadAll(QueryPlan.FromRules(CleanupPlanner.Rules()));
  var plan = CleanupPlanner.Plan(model.Owners.Concat(model.Rows), []);
  check(plan.Edits.All(e => e.Field != "건축") && plan.Notes.Any(n => n.Contains(group.Name) && n.Contains(owner.Id.Value.ToString()) && n.Contains("건축")), "제한 필드는 정리 예정에서 제외하고 상세 사유 유지");
  Reject(() => CleanupWriter.Prepare(doc, new() { Path = model.Path }, edits), "이전 허용 승인은 현재 제한 시 쓰기 전에 거부");
  check(owner.GroupId == group.Id && !definition.VariesAcrossGroups && p.Value == "0", "검사·준비 과정이 그룹·설정·값을 변경하지 않음");
 }
}
