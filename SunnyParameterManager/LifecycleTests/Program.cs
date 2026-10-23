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
  State.InfoFailures = State.InfoCalls = State.InfoDisposals = 0; State.InfoRead = null; State.InfoOutOfMemory = false;
  State.RegenerateStairName = State.FailNameRestore = false;
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
   StairNameTests.Run(Check);
   var r = Setup(); r.Operation = "조회"; var result = Run(r);
   Check(result.Success && result.Snapshot!.Rows.Count == 1 && State.Events.SequenceEqual(new[] { "open", "close" }), "조회는 저장/트랜잭션 없이 닫기");
   r = Setup(); State.InfoFailures = 2; int waits = 0;
   using (ModelEngine.ReadFileInfo(_source, _ => { }, _ => waits++)) { }
   Check(State.InfoCalls == 3 && waits == 2 && !State.Events.Any(), "기본정보 일시 실패만 3회 읽기 재확인·모델 열기/쓰기 없음");
   r = Setup(); State.InfoFailures = 10; result = Run(r);
   Check(!result.Success && result.CanContinueAfterPreflightFailure && State.InfoCalls == 3 && !State.Events.Any() && result.Message.Contains("형식이 잘못됐다고 단정") && result.Message.Contains("원본 오류"), "반복 기본정보 실패는 명확한 진단·수정 전 차단");
   r = Setup(); State.InfoOutOfMemory = true; result = Run(r);
   Check(!result.Success && !result.CanContinueAfterPreflightFailure && State.InfoCalls == 1, "기본정보 OOM은 재시도/다음 적용 허용 금지");
   r = Setup(); State.Info.IsInProgress = true; waits = 0;
   using (ModelEngine.ReadFileInfo(_source, _ => { }, _ => { waits++; State.Info.IsInProgress = false; })) { }
   Check(waits == 1 && State.InfoCalls == 2 && State.InfoDisposals == 2, "저장 중 기본정보 폐기·재확인 후에만 반환");
   r = Setup(); State.Info.IsInProgress = true; result = Run(r);
   Check(!result.Success && State.InfoCalls == 3 && State.InfoDisposals == 3 && !State.Events.Any(), "계속 저장 중인 파일은 열지 않음");
   r = Setup(); State.InfoRead = () => { if (State.InfoCalls == 1) File.AppendAllText(_source, "변경"); };
   waits = 0; using (ModelEngine.ReadFileInfo(_source, _ => { }, _ => waits++)) { }
   Check(waits == 1 && State.InfoCalls == 2, "정보 추출 도중 파일 크기 변경은 재확인");
   File.WriteAllText(_source, "테스트 대역 원본");
   r = Setup(true); State.OwnedOther = true; r.Operation = "조회"; result = Run(r);
   Check(result.Success && result.Snapshot!.Notices.Any(n => n.Contains("다른작업자") && n.Contains("요소 ID: 10")) && !State.Events.Contains("set"), "조회 때 소유 사용자·요소 번호 사전 안내");
   r = Setup(); r.Operation = "조회"; r.Query = new() { Scopes = ["유형"], Names = ["업체명"] }; result = Run(r);
   Check(result.Success && result.Snapshot!.Query!.Names.Single() == "업체명" && State.Events.SequenceEqual(new[] { "open", "close" }), "선택 조회 조건 전달·스냅샷 보존·저장 없음");
   r = Setup(); r.Operation = "조회"; r.Query = new(); result = Run(r);
   Check(!result.Success && !State.Events.Contains("open"), "잘못된 선택 조회는 파일 열기 전 차단");
   r = Setup(); r.Query = new() { Scopes = ["유형"], Names = ["업체명"] }; result = Run(r);
   Check(!result.Success && !State.Events.Contains("open"), "선택 조회 조건과 쓰기 요청 혼합 차단");
   var progress = new List<Response>();
   r = Setup(); r.Operation = "조회"; result = new ModelEngine(_root, progress.Add).Run(new App(), r);
   Check(result.Success && progress.Any(p => p.Stage == "문서 닫기") && progress.All(p => p.Snapshot == null), "조회 진행 기록에는 대용량 전체 결과를 중복 직렬화하지 않기");
   r = Setup(); r.Operation = "조회"; State.ReadOutOfMemory = true; result = Run(r); State.ReadOutOfMemory = false;
   Check(!result.Success && result.Snapshot == null && result.ReadRestartRequired && !result.CanRestartForRead, "조회 OOM은 부분 결과 폐기·재시작 필요 표시만 하고 문서 없음 판정은 Worker 담당");
   Check(State.Events.SequenceEqual(new[] { "open", "close" }), "조회 OOM도 저장 없이 문서 닫기 시도");
   r = Setup(); r.Operation = "조회"; State.ReadOutOfMemory = true; State.FailClose = true; result = Run(r); State.ReadOutOfMemory = false;
   Check(result.RequiresAttention && !result.CanRestartForRead && result.ReadRestartRequired, "조회 OOM 후 닫기 실패도 안전 재시작을 추정하지 않기");
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
   r = Setup(true, true); r.Edits[0].DefinitionFix = new() { Action = SpecRulePlanner.ActionRemove }; result = Run(r);
   Check(!result.Success && File.Exists(result.BackupPath) && !State.Events.Contains("tx-start") && !State.Events.Contains("sync"), "중앙 정의 삭제도 실제 편집 전 별도 작업 로컬 백업·잘못된 요청 미저장");
   Request StairRequest()
   {
    var request = Setup(); State.Doc.Types["type-1"] = new Autodesk.Revit.DB.Architecture.StairsRunType { Name = "KAC-S-ST1110_ND-T150" };
    request.Edits = EditPlanner.Stage(new ParameterReader(State.Doc, new() { Path = request.Path }).ReadAll(), "새값");
    State.RegenerateStairName = true; return request;
   }
   r = StairRequest(); result = Run(r);
   Check(result.Success && State.Doc.Types["type-1"].Name == "KAC-S-ST1110_ND-T150" && State.Events.IndexOf("stair-name-preserve") < State.Events.IndexOf("save"), "실제 저장 엔진은 계단 이름 보존 후 기존 값·유형 검증·저장");
   Check(result.Message.Contains("계단진행 유형 1개") && State.Events.Count(s => s == "tx-commit") == 2, "계단 이름 보존은 한 번만 수행하고 완료 기록에 표시");
   r = StairRequest(); State.FailNameRestore = true; result = Run(r);
   Check(!result.Success && State.Events.Contains("rollback") && !State.Events.Contains("save") && State.Doc.Element.Parameter.Value == "기존" && State.Doc.Types["type-1"].Name == "KAC-S-ST1110_ND-T150", "이름 보존 실패는 전체 값·이름 롤백하고 저장 금지");
   r = StairRequest(); State.TypeChangesAfterSet = true; result = Run(r);
   Check(!result.Success && State.Events.Contains("rollback") && !State.Events.Contains("save"), "이름 보존 성공해도 실제 소속 유형 변경은 여전히 차단");
   r = Setup(true, true); State.GroupEdit = new() { Identity = "새 그룹", Reason = "그룹 편집 필요" }; result = Run(r);
   Check(!result.Success && !State.Events.Contains("tx-start") && !State.Events.Contains("sync"), "새 그룹 제한은 트랜잭션·중앙 동기화 전에 차단");
   r = Setup(true, true); State.GroupChangesAfterSet = true; result = Run(r);
   Check(!result.Success && State.Events.Contains("rollback") && !State.Events.Contains("save") && !State.Events.Contains("sync") && State.Doc.Element.Parameter.Value == "기존", "확정 후 그룹 변화는 전체 묶음 롤백·저장 및 동기화 금지");
   r = Setup(true, true); State.GroupEdit = new() { Identity = "그룹", CanVary = true };
   r.Edits[0].Original.GroupEdit = State.GroupEdit with { }; result = Run(r);
   Check(result.Success && State.Events.Contains("sync"), "변화 없는 그룹별 허용 근거는 기존 중앙 동기화 경계 통과");
   Check(result.CentralSynchronized && result.OwnershipReleased && State.OwnedIds.Count == 0 && State.Events.IndexOf("relinquish") > State.Events.IndexOf("sync") && State.Events.IndexOf("close") > State.Events.IndexOf("relinquish"), "동기화→소유권 반환→확인→닫기 및 별도 완료 상태");
   r = Setup(true, true); State.OwnedIds.Add(20); result = Run(r);
   Check(result.Success && !State.OwnedIds.Contains(20), "이번 대상 이외 기존 현재 사용자 유형 소유권도 반환");
   r = Setup(true); result = Run(r);
   Check(result.Success && result.CentralSynchronized && result.OwnershipReleased, "기존 로컬 적용도 중앙 동기화와 모든 소유권 반환");
   r = Setup(true, true); State.FailRelinquish = true; result = Run(r);
   Check(!result.Success && result.CentralSynchronized && !result.OwnershipReleased && result.Applied == 1 && result.RequiresAttention && !State.Events.Contains("close") && State.Events.Count(s => s == "sync") == 1, "동기화 성공 후 반환 통신 실패는 중앙 반영 사실 보존·문서 유지·재실행 금지");
   Check(result.Stage.Contains("소유권 반환 확인 필요") && result.Message.Contains("동기화 자체는 완료"), "소유권 실패를 중앙 미저장으로 오표시하지 않음");
   r = Setup(true, true); State.LeaveBorrowed = true; State.OwnedIds.Add(20); result = Run(r);
   Check(!result.Success && result.RequiresAttention && result.Message.Contains("요소 2개") && result.Message.Contains("20"), "반환 호출 성공해도 유형·인스턴스 잔존 시 완료 금지");
   r = Setup(true, true); State.LeaveWorkset = true; result = Run(r);
   Check(!result.Success && result.RequiresAttention && result.Message.Contains("프로젝트 표준") && result.Message.Contains("작업세트 1개"), "사용자 작업세트 외 표준 작업세트 잔존도 차단");
   r = Setup(true, true); State.FailOwnershipCheck = true; result = Run(r);
   Check(!result.Success && result.RequiresAttention && !result.OwnershipReleased && !State.Events.Contains("close"), "반환 뒤 검사 오류도 정상 완료로 추정하지 않음");
   r = Setup(true, true); State.FailSync = true; result = Run(r);
   Check(!result.Success && !result.CentralSynchronized && !State.Events.Contains("relinquish"), "동기화 실패는 미반영 변경 보존하고 반환 강행하지 않음");
   r = Setup(true, true); State.FailSet = true; result = Run(r);
   Check(!result.Success && !result.RequiresAttention && result.OwnershipReleased && State.Events.IndexOf("relinquish") > State.Events.IndexOf("rollback") && State.OwnedIds.Count == 0 && !State.Events.Contains("sync"), "변경 롤백 후 남은 차용권은 저장 없이 반환");
   r = Setup(true, true); State.FailSet = State.FailRelinquish = true; result = Run(r);
   Check(!result.Success && result.RequiresAttention && !State.Events.Contains("close") && result.Message.Contains("미저장 작업 취소 후 소유권"), "롤백 후 반환 실패도 문서 보존·확인 요청");
   r = Setup(true, true); State.Doc.CreationGUID = Guid.NewGuid(); result = Run(r);
   Check(!result.Success && !State.Events.Contains("relinquish"), "대상 모델 식별 실패는 소유권 반환 부작용 없음");
   r = Setup(); result = Run(r);
   Check(result.Success && !result.CentralSynchronized && !result.OwnershipReleased && !State.Events.Contains("relinquish"), "일반 비작업공유 파일은 소유권 API 호출 없음");
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
   Check(result.Message.Contains("파일 사용자: 다른 사용자") && result.Message.Contains("현재 Revit 사용자: 테스터") && result.Message.Contains("중앙 경로"), "보관 로컬은 사용자와 원래 중앙 안내·사칭 우회 없음");
   r = Setup(true); State.Info.CentralPath = Path.Combine(_root, "이동된중앙.rvt"); result = Run(r);
   Check(!result.Success && result.Message.Contains("기록된 중앙 경로") && result.Message.Contains("선택 파일") && !State.Events.Contains("open"), "선택 파일 존재해도 기록된 중앙 부재는 구분해서 안내");
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
   Check(result.Message.Contains("원래 값") && result.Message.Contains("외부 변경") && result.Message.Contains("1개 변경 항목"), "적용 전 불일치 집계와 실제 전후 값 안내");
   r = Setup(); State.Doc.Missing = true;
   for (var i = 0; i < 20; i++) r.Edits.Add(new() { Original = new() { ModelPath = r.Path, OwnerId = "없음" + i, ElementId = i.ToString(), Name = "필드" + i }, Value = "x" });
   result = Run(r);
   Check(!result.Success && result.Message.Contains("21개 변경 항목") && result.Message.Contains("외 9개") && result.CanContinueAfterPreflightFailure && !State.Events.Contains("set"), "여러 삭제 대상을 묶어 보고·예시12건 제한·쓰기 없음");
   r = Setup(); r.Edits.Add(r.Edits[0]); result = Run(r);
   Check(!result.Success && !State.Events.Contains("set"), "중복 요청 대상 차단");
   r = Setup(); State.Doc.Missing = true; result = Run(r);
   Check(!result.Success && !State.Events.Contains("tx-start"), "삭제된 요소 차단");
   r = Setup(true); State.OwnedOther = true; result = Run(r);
   Check(!result.Success && !State.Events.Contains("tx-start"), "다른 사용자 소유권 강제 취득 금지");
   Check(result.CanContinueAfterPreflightFailure && result.Message.Contains("다른작업자") && result.Message.Contains("반환") && State.Events.Contains("close"), "소유권 차단은 문서 닫기 후 다음 모델 승인 후보");
   r = Setup(true); State.OwnedOther = true; State.FailClose = true; result = Run(r);
   Check(!result.CanContinueAfterPreflightFailure && result.RequiresAttention, "소유권 차단 뒤 문서 닫기 실패는 계속 금지");
   r = Setup(); State.FailSet = true; result = Run(r);
   Check(!result.Success && State.Events.Contains("rollback") && !State.Events.Contains("save"), "입력 거부 시 전체 롤백");
   Check(!result.CanContinueAfterPreflightFailure, "편집 시작 후 실패는 보수적 중단 유지");
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
   r = Setup(true, true); r.Edits[0].KacCleanup = true; result = Run(r);
   Check(result.Success && State.Events.IndexOf("cleanup-prepare") < State.Events.IndexOf("tx-start") && State.Events.IndexOf("cleanup-verify") < State.Events.IndexOf("save") && State.Events.Contains("sync"), "정리 전용 검증 후 기존 중앙 저장 수명 재사용");
   r = Setup(true, true); r.Edits[0].KacCleanup = true; r.Confirmed = false; result = Run(r);
   Check(!result.Success && !State.Events.Contains("open"), "유형 정리도 최종 승인 전 파일 열기 금지");
   r = Setup(true, true); r.Edits[0].KacCleanup = true; State.VerifyMismatch = true; result = Run(r);
   Check(!result.Success && State.Events.Contains("rollback") && !State.Events.Contains("save") && !State.Events.Contains("sync"), "정리 확정 재검증 실패는 전체 묶음 취소");
   r = Setup(true, true); r.Edits[0].KacCleanup = true; State.FailSync = true; result = Run(r);
   Check(result.RequiresAttention && !State.Events.Contains("close") && State.Events.Count(e => e == "sync") == 1, "정리 중앙 동기화 실패도 로컬 보존·자동 재시도 금지");
   r = Setup(true, true); r.Edits[0].KacCleanup = true; State.Failure = FailureSeverity.Error; result = Run(r);
   Check(!result.Success && State.Events.Contains("rollback") && !State.Events.Contains("save"), "정리 중 Revit 오류를 억제하지 않고 묶음 취소");
   Console.WriteLine($"TOTAL {_count} PASS · API 대역 제어 흐름 검사이며 실제 Revit 검증 아님");
   Console.WriteLine("테스트 기록: " + _root); return 0;
  }
  catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
 }
}
