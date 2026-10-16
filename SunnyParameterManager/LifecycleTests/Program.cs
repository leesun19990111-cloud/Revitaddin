using Autodesk.Revit.DB;
using SunnyParameterManager.Core;
using SunnyParameterManager.Bridge;
using App = Autodesk.Revit.ApplicationServices.Application;

internal static class Program
{
 private static int _count;
 private static string _root = "", _source = "", _central = "";
 private static void Check(bool ok, string label) { if (!ok) throw new Exception("FAIL " + label); Console.WriteLine("PASS " + label); _count++; }
 private static Request Setup(bool shared = false, bool central = false)
 {
  State.Reset(); State.Scope = "유형"; State.Doc.IsWorkshared = State.Info.IsWorkshared = shared; State.Info.CentralPath = shared ? _central : "";
  var path = central ? _central : _source;
  var original = new ParameterReader(State.Doc, new ModelSnapshot { Path = path }).ReadAll().Single();
  return new Request { Path = path, CanonicalPath = ModelEngine.Canonical(path), Operation = "적용", Identity = "파일-v2/" + State.Doc.CreationGUID.ToString("D") + "/" + (shared ? "중앙:" + ModelEngine.Canonical(_central).ToUpperInvariant() : "일반"), Confirmed = true, LocalChangesConfirmed = true, Edits = EditPlanner.Stage([original], "새값") };
 }
 private static Response Run(Request r) => new ModelEngine(_root, _ => { }).Run(new App(), r);
 public static int Main()
 {
  try
  {
   _root = Path.Combine(Path.GetTempPath(), "SunnyParameterManager-tests-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(_root);
   _source = Path.Combine(_root, "normal.rvt"); _central = Path.Combine(_root, "central.rvt");
   // 실제 RVT가 아닌 대역 검사용 텍스트 파일. 사용자 파일은 사용하지 않는다.
   File.WriteAllText(_source, "테스트 대역 원본"); File.WriteAllText(_central, "테스트 대역 중앙");
   var r = Setup(); r.Operation = "조회"; var result = Run(r);
   Check(result.Success && result.Snapshot!.Rows.Count == 1 && State.Events.SequenceEqual(new[] { "open", "close" }), "조회는 저장/트랜잭션 없이 닫기");
   r = Setup(true, true); r.Operation = "조회"; result = Run(r);
   Check(result.Success && result.Snapshot!.Kind == "중앙" && result.Snapshot.Rows.Count == 1 && State.Events.SequenceEqual(new[] { "create-local", "open", "close" }), "파일 기반 중앙 조회는 서버 전용 GUID 없이 읽고 저장하지 않기");
   var centralIdentity = result.Snapshot!.Identity;
   r.Id = Guid.NewGuid().ToString("N"); r.Operation = "적용"; r.Identity = centralIdentity; State.Events.Clear(); result = Run(r);
   Check(result.Success && State.Events.Contains("create-local") && State.Events.Contains("sync"), "중앙 조회 후 새 작업 로컬에서도 같은 모델 식별");
   r = Setup(true); r.Operation = "조회"; result = Run(r);
   Check(result.Success && result.Snapshot!.Kind == "로컬" && State.Events.SequenceEqual(new[] { "open", "close" }), "기존 파일 기반 로컬 조회도 저장/동기화 없음");
   r = Setup(true, true); State.Doc.CreationGUID = Guid.NewGuid(); result = Run(r);
   Check(!result.Success && result.Message.Contains("다른 문서") && !State.Events.Contains("tx-start"), "같은 경로의 중앙모델 교체 시 편집 차단");
   r = Setup(true, true); State.Doc.CentralPathOverride = _source; result = Run(r);
   Check(!result.Success && result.Message.Contains("중앙모델과 연결") && !State.Events.Contains("tx-start"), "열린 로컬의 중앙 연결 변경 차단");
   r = Setup(true); State.Info.CentralPath = _source; result = Run(r);
   Check(!result.Success && result.Message.Contains("다른 문서") && !State.Events.Contains("tx-start"), "CreationGUID가 같아도 중앙 경로 변경 차단");
   r = Setup(); r.Operation = "조회"; State.Doc.CreationGUID = Guid.Empty; result = Run(r);
   Check(!result.Success && result.Message.Contains("식별자") && State.Events.SequenceEqual(new[] { "open", "close" }), "빈 모델 식별자를 성공한 조회로 사용하지 않기");
   r = Setup(); r.Identity = State.Doc.CreationGUID.ToString("D") + "/일반"; result = Run(r);
   Check(!result.Success && result.Message.Contains("다시 조회") && !State.Events.Contains("tx-start"), "이전 버전 조회 기록은 새로 조회한 뒤 적용");
   r = Setup(); result = Run(r);
   Check(result.Success && result.Applied == 1 && State.Doc.Element.Parameter.Value == "새값", "일반 파일 변경 성공");
   Check(State.Events.SequenceEqual(new[] { "open", "group-start", "tx-start", "set", "tx-commit", "assimilate", "save", "close" }), "일반 파일 단계 순서");
   Check(File.ReadAllText(result.BackupPath) == "테스트 대역 원본", "변경 전 파일 백업");
   foreach (var scope in new[] { "뷰", "시트" })
   {
    r = Setup(); State.Scope = scope;
    r.Edits = EditPlanner.Stage(new ParameterReader(State.Doc, new ModelSnapshot { Path = r.Path }).ReadAll(), "새 구분");
    result = Run(r);
    Check(result.Success && result.Applied == 1 && State.Doc.Element.Parameter.Value == "새 구분" && State.Events.Contains("save"), scope + " 범위도 실제 저장 엔진의 검증·입력·저장 흐름 통과");
   }
   r = Setup(true, true); result = Run(r);
   Check(result.Success && State.Events.First() == "create-local" && State.Events.Contains("sync") && result.RecoveryPath != _central, "중앙은 별도 로컬에서 동기화");
   Check(File.Exists(result.RecoveryPath) && result.BackupPath == "", "중앙 원본 경로 직접 덮어쓰기 없음");
   Request Removal(bool shared = false, bool central = false)
   {
    var request = Setup(shared, central); request.Operation = "링크 삭제"; request.Edits.Clear();
    request.LinkRemoval = LinkRemovalEngine.Preview(State.Doc, new() { Path = request.Path, CanonicalPath = request.CanonicalPath, Identity = request.Identity });
    return request;
   }
   r = Setup(); r.Operation = "링크 검사"; r.Edits.Clear(); result = Run(r);
   Check(result.Success && result.LinkRemoval != null && !State.Events.Contains("save") && !State.Events.Contains("sync"), "링크 검사만으로 원본 저장·동기화 없음");
   r = Removal(); result = Run(r);
   Check(result.Success && result.Applied == 1 && File.Exists(result.BackupPath) && State.Events.Contains("delete-links") && State.Events.Contains("verify-links") && State.Events.Contains("save"), "링크 삭제의 일반파일 백업·검증·저장");
   r = Removal(true, true); result = Run(r);
   Check(result.Success && State.Events.Contains("sync") && State.Events.IndexOf("links-empty") > State.Events.IndexOf("sync") && File.Exists(result.BackupPath) && File.ReadAllText(result.BackupPath) == "테스트 대역 중앙", "중앙 링크 삭제도 삭제 전 로컬 사본 보관·동기화 후 남은 링크 확인");
   r = Removal(); r.Confirmed = false; result = Run(r); Check(!result.Success && State.Events.Count == 0, "삭제 승인 누락은 모델 열기 전 차단");
   r = Removal(); r.LinkRemoval = null; result = Run(r); Check(!result.Success && State.Events.Count == 0, "삭제 검사 누락 차단");
   r = Removal(); r.Edits.Add(new()); result = Run(r); Check(!result.Success && State.Events.Count == 0, "링크 삭제와 매개변수 입력 혼합 차단");
   r = Removal(); r.LinkRemoval!.Model.Path = "다른 모델"; result = Run(r); Check(!result.Success && State.Events.Count == 0, "다른 모델의 삭제 계획 차단");
   r = Removal(); r.LinkRemoval!.Affected.Add(new() { UniqueId = "tag", ElementId = 56, Revision = "r1" }); result = Run(r);
   Check(!result.Success && State.Events.Count == 0, "종속 요소 승인 없이는 파일 열기 전 차단");
   r = Removal(true); r.LocalChangesConfirmed = false; result = Run(r); Check(!result.Success && State.Events.Count == 0, "기존 로컬 링크 삭제도 미동기화 작업 동반 승인 필요");
   r = Removal(); State.VerifyMismatch = true; result = Run(r); Check(!result.Success && State.Events.Contains("rollback") && !State.Events.Contains("save"), "삭제 결과 불일치 묶음 롤백·저장 금지");
   r = Removal(); State.Failure = FailureSeverity.Error; result = Run(r); Check(!result.Success && State.Events.Contains("rollback") && !State.Events.Contains("save"), "삭제 중 Revit 오류도 묶음 취소");
   r = Removal(true, true); State.FailSync = true; result = Run(r); Check(!result.Success && result.RequiresAttention && !State.Events.Contains("close") && File.Exists(result.BackupPath), "링크 삭제 후 동기화 실패는 문서·백업 보존");
   r = Removal(true, true); State.FailClose = true; result = Run(r); Check(result.Success && result.RequiresAttention && result.Applied == 1, "링크 삭제 동기화 성공 후 닫기 실패는 중복 재시도 금지");
   r = Setup(true); r.LocalChangesConfirmed = false; result = Run(r);
   Check(!result.Success && !State.Events.Any(), "기존 로컬의 다른 변경 포함 승인 필수");
   r = Setup(true); State.Info.Username = "다른 사용자"; result = Run(r);
   Check(!result.Success && !State.Events.Any(), "다른 사용자 로컬 차단");
   r = Setup(); r.Confirmed = false; result = Run(r);
   Check(!result.Success && !State.Events.Any(), "실제 변경 명시 확인 필수");
   r = Setup(); State.Info.IsSavedInCurrentVersion = false; result = Run(r);
   Check(!result.Success && !State.Events.Any(), "자동 버전 업그레이드 금지");
   r = Setup(); State.Info.IsInProgress = true; result = Run(r);
   Check(!result.Success && !State.Events.Any(), "저장 진행 중 파일 차단");
   r = Setup(); r.CanonicalPath = _central; result = Run(r);
   Check(!result.Success && !State.Events.Any(), "실제 경로 변경 차단");
   r = Setup(); r.Identity = "다른 문서"; result = Run(r);
   Check(!result.Success && State.Events.SequenceEqual(new[] { "open", "close" }), "문서 교체 시 편집 전 중단");
   r = Setup(); State.Doc.Element.Parameter.Value = "외부 변경"; result = Run(r);
   Check(!result.Success && !State.Events.Contains("tx-start") && State.Doc.Element.Parameter.Value == "외부 변경", "최신값 덮어쓰기 차단");
   r = Setup(); r.Edits.Add(r.Edits[0]); result = Run(r);
   Check(!result.Success && !State.Events.Contains("set"), "중복 요청 대상 차단");
   r = Setup(); State.Doc.Missing = true; result = Run(r);
   Check(!result.Success && !State.Events.Contains("tx-start"), "삭제된 요소 차단");
   r = Setup(true); State.OwnedOther = true; result = Run(r);
   Check(!result.Success && !State.Events.Contains("tx-start"), "다른 사용자 소유권 강제 취득 금지");
   r = Setup(); State.FailSet = true; result = Run(r);
   Check(!result.Success && State.Events.Contains("rollback") && !State.Events.Contains("save"), "입력 거부 시 전체 롤백");
   r = Setup(); State.FailCommit = true; result = Run(r);
   Check(!result.Success && State.Doc.Element.Parameter.Value == "기존" && !State.Events.Contains("save"), "확정 거부 시 원래 값 보존");
   r = Setup(); State.VerifyMismatch = true; result = Run(r);
   Check(!result.Success && State.Doc.Element.Parameter.Value == "기존" && State.Events.Contains("rollback"), "확정 후 값 재검증 실패 롤백");
   r = Setup(); State.FailSave = true; result = Run(r);
   Check(!result.Success && result.RequiresAttention && !State.Events.Contains("close") && State.Doc.Element.Parameter.Value == "새값", "저장 실패 시 열린 변경 보존");
   r = Setup(true, true); State.FailSync = true; result = Run(r);
   Check(!result.Success && result.RequiresAttention && !State.Events.Contains("close") && State.Events.Count(e => e == "sync") == 1, "동기화 실패 시 닫기/자동 재시도 금지");
   Check(File.Exists(result.RecoveryPath), "동기화 실패 복구 로컬 유지");
   r = Setup(true, true); State.FailClose = true; result = Run(r);
   Check(result.Success && result.RequiresAttention && result.Applied == 1, "동기화 성공 후 닫기 실패는 반영 성공으로 보고");
   r = Setup(); State.Doc.IsReadOnly = true; result = Run(r);
   Check(!result.Success && !State.Events.Contains("tx-start"), "실제 읽기전용 원본 강제 해제 금지");
   r = Setup(); r.Edits[0].Value = "테스트재료"; r.Edits[0].MaterialSource = new() { Name = "KAC-테스트재료", Basis = "벽 외측층", Fingerprint = "material-v1" }; result = Run(r);
   Check(result.Success && State.Doc.Element.Parameter.Value == "테스트재료", "재료 근거 재검증 후 저장");
   r = Setup(); r.Edits[0].Value = "테스트재료"; r.Edits[0].MaterialSource = new() { Name = "KAC-테스트재료", Basis = "벽 외측층", Fingerprint = "old-material" }; result = Run(r);
   Check(!result.Success && !State.Events.Contains("tx-start") && !State.Events.Contains("save"), "조회 후 재료 변경은 쓰기 전 차단");
   r = Setup(); r.Edits[0].Value = "테스트재료"; r.Edits[0].MaterialSource = new() { Name = "KAC-테스트재료", Basis = "벽 외측층", Fingerprint = "material-v1" }; State.MaterialChangesAfterSet = true; result = Run(r);
   Check(!result.Success && State.Events.Contains("rollback") && !State.Events.Contains("save") && State.Doc.Element.Parameter.Value == "기존", "같은 적용 묶음의 재료 변경도 확정 후 전체 롤백");
   r = Setup(); State.TypeOwnerId = "type-2"; result = Run(r);
   Check(!result.Success && !State.Events.Contains("tx-start"), "조회 뒤 이름이 같아도 소속 유형 ID 변경은 편집 전 차단");
   r = Setup(); State.TypeChangesAfterSet = true; result = Run(r);
   Check(!result.Success && State.Events.Contains("rollback") && !State.Events.Contains("save"), "트랜잭션 확정 중 소속 유형 변경은 묶음 롤백");
   Check(result.Message.Contains("type-1") && result.Message.Contains("type-2") && result.Message.Contains("요소 ID"), "유형 변경 진단에 적용 전후 식별자와 요소 번호 표시");
   r = Setup(); State.Failure = FailureSeverity.Error; result = Run(r);
   Check(!result.Success && State.Events.Contains("rollback") && !State.Events.Contains("save") && !State.Events.Contains("sync"), "Revit에서 해결한 오류도 매개변수 작업으로 저장하지 않기");
   Check(result.Message.Contains("결합된 상태") && result.Message.Contains("101, 102, 103"), "실제 Revit 오류와 중복 없는 관련 요소 번호 보고");
   Check(State.App.Observers == 0, "오류 뒤 진단 이벤트 구독 해제");
   r = Setup(); State.Failure = FailureSeverity.Error; State.FailCommit = true; result = Run(r);
   Check(!result.Success && result.Message.Contains("RolledBack") && result.Message.Contains("101"), "Revit 확정 취소에도 오류 상세 유지");
   r = Setup(); State.Failure = FailureSeverity.Warning; result = Run(r);
   Check(result.Success && State.Events.Contains("save") && State.App.Observers == 0, "경고만 있는 정상 작업은 저장 및 이벤트 해제");
   r = Setup(); State.Failure = FailureSeverity.Error; State.OtherDocFailure = true; result = Run(r);
   Check(result.Success, "다른 문서 오류는 현재 파일 실패로 오인하지 않기");
   r = Setup(); State.UnreadableFailure = true; result = Run(r);
   Check(!result.Success && !State.Events.Contains("save") && State.App.Observers == 0, "진단 불명은 이벤트 예외 없이 저장 차단");
   r = Setup(); State.FailSet = true; result = Run(r);
   Check(!result.Success && State.App.Observers == 0, "값 입력 실패도 진단 구독 해제");
   r = Setup(); State.DeletedAfterCommit = true; result = Run(r);
   Check(!result.Success && result.Message.Contains("대상 요소가 없어져") && State.Events.Contains("rollback"), "확정 후 원래 요소를 재조회하여 삭제된 대상 저장 차단");
   r = Setup(); r.Edits[0].Value = "ND"; r.Edits[0].MaterialSource = new() { FallbackAllowed = true, Reason = "유형 재료 없음" }; State.MaterialMissing = true; result = Run(r);
   Check(result.Success && State.Doc.Element.Parameter.Value == "ND", "미확인 재료 ND 적용과 전후 재검증 성공");
   r = Setup(); r.Edits[0].Value = "ND"; r.Edits[0].MaterialSource = new() { FallbackAllowed = true, Reason = "유형 재료 없음" }; result = Run(r);
   Check(!result.Success && !State.Events.Contains("tx-start"), "ND 계획 후 재료 지정 시 쓰기 전 차단");
   r = Setup(); r.Edits[0].Value = "ND"; r.Edits[0].MaterialSource = new() { FallbackAllowed = true, Reason = "유형 재료 없음" }; State.MaterialMissing = State.MaterialChangesAfterSet = true; result = Run(r);
   Check(!result.Success && State.Events.Contains("rollback") && !State.Events.Contains("save") && State.Doc.Element.Parameter.Value == "기존", "ND 확정 중 재료 발견 시 묶음 롤백");
   Console.WriteLine($"TOTAL {_count} PASS · API 대역 제어 흐름 검사이며 실제 Revit 검증 아님");
   Console.WriteLine("테스트 기록: " + _root); return 0;
  }
  catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
 }
}
