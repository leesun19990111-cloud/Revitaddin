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
  State.Reset(); State.Doc.IsWorkshared = State.Info.IsWorkshared = shared; State.Info.CentralPath = shared ? _central : "";
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
   r = Setup(true, true); result = Run(r);
   Check(result.Success && State.Events.First() == "create-local" && State.Events.Contains("sync") && result.RecoveryPath != _central, "중앙은 별도 로컬에서 동기화");
   Check(File.Exists(result.RecoveryPath) && result.BackupPath == "", "중앙 원본 경로 직접 덮어쓰기 없음");
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
   Console.WriteLine($"TOTAL {_count} PASS · API 대역 제어 흐름 검사이며 실제 Revit 검증 아님");
   Console.WriteLine("테스트 기록: " + _root); return 0;
  }
  catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
 }
}
