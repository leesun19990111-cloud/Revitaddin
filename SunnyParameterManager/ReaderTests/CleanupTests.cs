using Autodesk.Revit.DB;
using SunnyParameterManager.Core;
using SunnyParameterManager.Bridge;

internal static class CleanupTests
{
 public static void Run(Action<bool, string> check)
 {
  void Reject(Action a, string name) { bool failed = false; try { a(); } catch (InvalidOperationException) { failed = true; } check(failed, name); }
  Parameter P(string name, long id, string value, ForgeTypeId? spec = null, bool identity = false) => new() { Id = new(id), Definition = new() { Name = name, Spec = spec ?? new("text"), Group = identity ? GroupTypeId.IdentityData : new("other") }, StorageType = spec == SpecTypeId.Boolean.YesNo || id == -1002053 ? StorageType.Integer : StorageType.String, Value = value };
  var doc = new Document { IsWorkshared = true }; doc.Worksets.Add(new() { Name = "01_건축", Id = new(11) }); doc.Worksets.Add(new() { Name = "02_구조", Id = new(12) }); doc.Worksets.Add(new() { Name = "09_매스", Id = new(19) });
  var type = new HostObjAttributes { Name = "KAC-A-CE7410_CW02_THK21(설명)", Category = new() { Id = new(-2000011) } }; type.Parameters.Clear(); type.Structure!.Width = 76.5 / 304.8; doc.Elements.Add(type);
  var inst = new Element { TypeId = type.Id, Category = type.Category }; inst.Parameters.Clear();
  inst.Parameters.Add(P("Workset", -1002053, "12")); inst.Parameters.Add(P("Room Bounding", -1001007, "1", SpecTypeId.Boolean.YesNo));
  inst.Parameters.Add(P("건축", 101, "0", SpecTypeId.Boolean.YesNo, true)); inst.Parameters.Add(P("구조", 102, "1", SpecTypeId.Boolean.YesNo, true));
  inst.Parameters.Add(P("구조", -999, "1", SpecTypeId.Boolean.YesNo, true)); doc.Elements.Add(inst);
  ModelSnapshot Read() { var model = new ModelSnapshot { Path = "검증.rvt", Title = "검증" }; model.Rows = new ParameterReader(doc, model).ReadAll(QueryPlan.FromRules(CleanupPlanner.Rules())); model.ConnectCatalogs(); return model; }
  RulePlan Plan(ModelSnapshot m) => CleanupPlanner.Plan(m.Owners.Concat(m.Rows), []);
  var m = Read(); var plan = Plan(m);
  check(plan.Edits.Count == 5, "정리: 이름·작업세트·룸 경계·건축·구조 변경");
  check(plan.Edits.Single(e => e.Original.ParameterKey == CleanupPlanner.NameKey).Value == "KAC-A-CE7410_CW02-T76.5(설명)", "총 두께·구분자·THK 정리와 설명 보존");
  check(plan.Edits.All(e => e.Original.ParameterKey != "builtin:-999"), "동명 Revit 기본 구조 속성은 변경하지 않음");
  check(!m.Rows.Single(r => r.ParameterKey == CleanupPlanner.NameKey).Editable, "유형 이름은 일반 직접 편집에 노출하지 않음");
  var actions = CleanupWriter.Prepare(doc, new() { Path = m.Path, Title = m.Title }, plan.Edits);
  foreach (var a in actions) a.Set(); foreach (var a in actions) a.Verify();
  check(type.Name == "KAC-A-CE7410_CW02-T76.5(설명)" && inst.Parameters[0].Value == "11" && inst.Parameters[1].Value == "0" && inst.Parameters[2].Value == "1" && inst.Parameters[3].Value == "0" && inst.Parameters[4].Value == "1", "실제 Writer 전체 적용·재검증 대역");
  check(Plan(Read()).Edits.Count == 0, "재검토 시 동일 값 반복 쓰기 없음");
  Reject(() => CleanupWriter.Prepare(doc, new() { Path = m.Path }, plan.Edits), "이전 조회 변경 재적용 차단");
  inst.Parameters[0].Value = "12"; m = Read(); plan = Plan(m);
  doc.OtherOwner = true; Reject(() => CleanupWriter.Prepare(doc, new() { Path = m.Path }, plan.Edits), "다른 사용자 소유권 차단"); doc.OtherOwner = false;
  inst.VersionGuid = Guid.NewGuid(); Reject(() => CleanupWriter.Prepare(doc, new() { Path = m.Path }, plan.Edits), "조회 후 요소 버전 변경 차단");
  m = Read(); plan = Plan(m); var saved = doc.Worksets[0]; doc.Worksets.RemoveAt(0);
  Reject(() => CleanupWriter.Prepare(doc, new() { Path = m.Path }, plan.Edits), "목표 작업세트 삭제 후 쓰기 차단");
  check(Plan(Read()).Notes.Any(n => n.Contains("선택 목록")), "없는 작업세트 보류 안내"); doc.Worksets.Insert(0, saved);
  inst.Parameters.Add(P("건축", 103, "0", SpecTypeId.Boolean.YesNo, true)); inst.Parameters[2].Value = "0";
  check(Plan(Read()).Edits.All(e => e.Field != "건축"), "ID 데이터 동명 후보 중복 보류"); inst.Parameters.RemoveAt(inst.Parameters.Count - 1);
  inst.Parameters[2].Definition.Group = new("other");
  check(Plan(Read()).Edits.All(e => e.Field != "건축"), "ID 데이터 아닌 사용자 동명 필드 유지"); inst.Parameters[2].Definition.Group = GroupTypeId.IdentityData;
  inst.Parameters[2].IsReadOnly = true;
  check(Plan(Read()).Notes.Any(n => n.Contains("읽기 전용")), "읽기 전용 항목 보류 안내"); inst.Parameters[2].IsReadOnly = false;
  type.Name = "KAC-A-CE7410_CW02-T21(설명)";
  var collision = new ElementType { Name = "KAC-A-CE7410_CW02-T76.5(설명)", Category = type.Category }; doc.Elements.Add(collision);
  check(Plan(Read()).Edits.All(e => e.Original.ParameterKey != CleanupPlanner.NameKey), "같은 카테고리 다른 패밀리 이름 충돌도 보류"); doc.Elements.Remove(collision);
  m = Read(); plan = Plan(m); type.Structure.Width = 100 / 304.8;
  Reject(() => CleanupWriter.Prepare(doc, new() { Path = m.Path }, plan.Edits), "실제 총 두께 변경 시 이름 쓰기 차단");
  type.Structure.VariableLayerIndex = 0;
  check(Read().Owners.Single(o => o.OwnerId == type.UniqueId).Cleanup!.ThicknessMm == null, "가변 레이어 두께 추정 금지"); type.Structure.VariableLayerIndex = -1;
  type.Structure.IsVerticallyCompound = true; type.Structure.Homogeneous = false;
  check(Read().Owners.Single(o => o.OwnerId == type.UniqueId).Cleanup!.ThicknessMm == null, "비균질 수직 구조 두께 보류"); type.Structure.Homogeneous = true;
  check(Read().Owners.Single(o => o.OwnerId == type.UniqueId).Cleanup!.ThicknessMm == 100, "균질 수직 구조 총 두께 지원");
  var family = new FamilySymbol { Name = "KAC-S-CL1110_ND-H1200", Category = new() { Id = new(-2001330) } }; family.Parameters.Clear(); family.Family.IsInPlace = true; doc.Elements.Add(family);
  var fi = new Element { TypeId = family.Id, Category = family.Category }; fi.Parameters.Clear(); fi.Parameters.Add(P("언어 무관 IFC", -1019014, "")); doc.Elements.Add(fi);
  check(Plan(Read()).Edits.Any(e => e.Original.OwnerId == fi.UniqueId && e.Value == "IfcColumn"), "내부편집 구조 기둥 실제 IFC 기본키 매핑");
  family.Family.IsInPlace = false; check(Plan(Read()).Edits.All(e => e.Original.OwnerId != fi.UniqueId), "일반 로드 패밀리 IFC 변경 제외"); family.Family.IsInPlace = true;
  fi.Category = family.Category = new() { Id = new(-2000151) };
  check(Plan(Read()).Notes.Any(n => n.Contains("용도를 확정")), "일반 모델 IFC 임의 추정 금지");
  var mass = new Element { Name = "일반 매스", Category = new() { Id = new(-2003400) } }; mass.Parameters.Clear(); mass.Parameters.Add(P("작업세트", -1002053, "12")); doc.Elements.Add(mass);
  m = Read(); var massEdits = Plan(m).Edits.Where(e => e.Original.OwnerId == mass.UniqueId).ToList();
  check(massEdits.Count == 1 && massEdits[0].Value == "09_매스", "비KAC·유형 없는 매스는 작업세트만");
  var massActions = CleanupWriter.Prepare(doc, new() { Path = m.Path }, massEdits); foreach (var a in massActions) a.Set(); foreach (var a in massActions) a.Verify();
  check(mass.Parameters[0].Value == "19", "유형 없는 매스 적용과 검증");
  m = Read(); plan = Plan(m); actions = CleanupWriter.Prepare(doc, new() { Path = m.Path }, plan.Edits); foreach (var a in actions) a.Set();
  inst.Parameters[2].Value = "0"; Reject(() => { foreach (var a in actions) a.Verify(); }, "확정 후 값 변경/DMU 불일치 차단");
  check(CleanupPlanner.NormalizeName("KAC-S-FT1130_ND_T300", null, out _) == "KAC-S-FT1130_ND-T300", "두께 불명은 수치 보존·구분자만 정리");
  check(CleanupPlanner.NormalizeName("KAC-S-CL1110_ND-H1200", 200, out _) == "KAC-S-CL1110_ND-H1200", "높이 H 규격을 두께로 바꾸지 않음");
  check(CleanupPlanner.NormalizeName("KAC-A-WA1000_ND-T10(THK20)", 30, out _) == "KAC-A-WA1000_ND-T30(THK20)", "괄호 속 THK 설명 보존");
  Reject(() => QueryPlan.RequireCoverage([new ModelSnapshot()], CleanupPlanner.Rules()), "이전 전체 조회에는 정리 근거 없으므로 재조회 필수");
  Reject(() => RulePlanner.Parse("{\"Version\":1,\"Name\":\"혼합\",\"Cleanup\":true}"), "잘못된 버전 혼합 룰셋 거부");
  check(RulePlanner.Parse("{\"Version\":4,\"Name\":\"정리\",\"Cleanup\":true}").Cleanup, "배포 룰셋 형식 읽기");
 }
}
