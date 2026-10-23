using Autodesk.Revit.DB;
using SunnyParameterManager.Core;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using RevitApplication = Autodesk.Revit.ApplicationServices.Application;

namespace SunnyParameterManager.Bridge;

public sealed class ModelEngine(string folder, Action<Response> progress)
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
    public Response Run(RevitApplication app, Request req)
    {
        Document? doc = null;
        Mutex? gate = null;
        bool locked = false, preserve = false;
        bool editingStarted = false;
        int restoredStairNames = 0;
        var result = new Response { Id = req.Id };
        var removingLinks = req.Operation == "링크 삭제";
        var removingDefinitions = req.Operation == "적용" && req.Edits.Any(e => e.DefinitionFix?.Action == SpecRulePlanner.ActionRemove);
        var modifying = req.Operation == "적용" || removingLinks;
        void Step(string stage) { result.Stage = stage; progress(new Response { Id = result.Id, Stage = stage, RecoveryPath = result.RecoveryPath, Applied = result.Applied, CentralSynchronized = result.CentralSynchronized, OwnershipReleased = result.OwnershipReleased }); }
        try
        {
            if (req.Operation is not ("조회" or "적용" or "링크 검사" or "링크 삭제")) throw new InvalidOperationException("지원하지 않는 작업입니다.");
            if (req.Query != null)
            {
                if (req.Operation != "조회") throw new InvalidOperationException("선택 조회 조건은 조회 요청에만 사용할 수 있습니다.");
                req.Query.Validate();
            }
            if (req.Operation == "적용" && (!req.Confirmed || req.Edits.Count == 0)) throw new InvalidOperationException("변경 확인이 없거나 변경 내용이 없습니다.");
            if (removingLinks)
            {
                if (!req.Confirmed || req.LinkRemoval == null || req.Edits.Count != 0) throw new InvalidOperationException("링크 삭제 확인/검사 정보가 없거나 값 변경이 섞여 있습니다.");
                req.LinkRemoval.Validate(req.DependentsConfirmed);
                if (req.LinkRemoval.Model.Path != req.Path || req.LinkRemoval.Model.Identity != req.Identity || req.LinkRemoval.Model.CanonicalPath != req.CanonicalPath)
                    throw new InvalidOperationException("링크 삭제 검사와 요청 모델이 다릅니다.");
            }
            else if (req.LinkRemoval != null || req.Operation == "링크 검사" && req.Edits.Count > 0) throw new InvalidOperationException("다른 작업이 섞인 요청입니다.");
            var source = Canonical(req.Path);
            if (!source.EndsWith(".rvt", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("RVT 파일만 선택하세요.");
            Step("파일 기본정보·저장 상태 확인");
            using var info = ReadFileInfo(source, Step);
            if (!info.IsSavedInCurrentVersion) throw new InvalidOperationException($"Revit 2026 형식만 처리합니다. 선택 파일: {info.Format}. 자동 업그레이드는 하지 않습니다.");
            if (info.IsInProgress) throw new InvalidOperationException("저장 중인 파일은 처리하지 않습니다.");
            var central = "";
            if (info.IsWorkshared)
            {
                Step("기록된 중앙파일 경로 확인");
                try { central = Canonical(info.CentralPath); }
                catch (IOException ex)
                {
                    throw new InvalidOperationException("선택한 파일에 기록된 중앙모델 경로에 접근할 수 없습니다. 이동·이름 변경 또는 서버 연결을 확인하세요. " +
                        "이 파일을 다른 중앙으로 임의 재연결하거나 복사본을 중앙으로 간주하지 않았습니다. Revit에서 정상 중앙 연결을 확인한 파일을 다시 등록하세요.\n선택 파일: " + source +
                        "\n기록된 중앙 경로: " + info.CentralPath + "\n원본 오류: " + ex.Message, ex);
                }
            }
            var isCentral = info.IsWorkshared && string.Equals(source, central, StringComparison.OrdinalIgnoreCase);
            if (info.IsWorkshared && !isCentral && !string.Equals(info.Username, app.Username, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"다른 사용자의 로컬 파일입니다. 원래 중앙파일을 선택하세요.\n파일 사용자: {info.Username} / 현재 Revit 사용자: {app.Username}\n중앙 경로: {central}\n보관용 복사본·스캔 전 파일을 현재 중앙으로 추정하지 않습니다. 사용자 이름을 바꾸어 우회하지 마세요.");
            if (modifying && info.IsWorkshared && !isCentral && !req.LocalChangesConfirmed)
                throw new InvalidOperationException("기존 로컬의 미동기화 작업까지 중앙에 포함된다는 확인이 필요합니다.");
            if (modifying && !string.Equals(source, req.CanonicalPath, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("조회 이후 실제 파일 경로가 달라졌습니다.");
            var lockKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes((central.Length > 0 ? central : source).ToUpperInvariant())));
            gate = new Mutex(false, "Local\\SunnyParameterManager-" + lockKey);
            try { locked = gate.WaitOne(0); } catch (AbandonedMutexException) { locked = true; throw new InvalidOperationException("이 모델의 이전 작업이 비정상 종료됐습니다. 중앙 상태와 복구 기록을 확인한 뒤 다시 시도하세요."); }
            if (!locked) throw new InvalidOperationException("이 모델은 다른 매개변수 관리자 작업에서 사용 중입니다.");
            if (modifying && !isCentral)
            {
                Step("변경 전 파일 백업");
                var backupDir = Path.Combine(folder, "Backups", req.Id);
                Directory.CreateDirectory(backupDir);
                result.BackupPath = Path.Combine(backupDir, Path.GetFileName(source));
                File.Copy(source, result.BackupPath, false);
            }
            var openPath = source;
            if (isCentral)
            {
                Step("작업용 로컬 준비");
                var localDir = Path.Combine(folder, "Locals", req.Id);
                Directory.CreateDirectory(localDir);
                openPath = Path.Combine(localDir, Path.GetFileName(source));
                WorksharingUtils.CreateNewLocal(ModelPathUtils.ConvertUserVisiblePathToModelPath(source), ModelPathUtils.ConvertUserVisiblePathToModelPath(openPath));
            }
            if ((removingLinks || removingDefinitions) && isCentral)
            {
                Step(removingLinks ? "링크 삭제 전 복구용 로컬 백업" : "매개변수 삭제 전 복구용 로컬 백업");
                var backupDir = Path.Combine(folder, "Backups", req.Id);
                Directory.CreateDirectory(backupDir);
                result.BackupPath = Path.Combine(backupDir, Path.GetFileName(openPath));
                File.Copy(openPath, result.BackupPath, false);
            }
            result.RecoveryPath = openPath;
            Step("모델 열기");
            using var options = new OpenOptions { DetachFromCentralOption = DetachFromCentralOption.DoNotDetach };
            if (info.IsWorkshared) options.SetOpenWorksetsConfiguration(new WorksetConfiguration(WorksetConfigurationOption.OpenAllWorksets));
            doc = app.OpenDocumentFile(ModelPathUtils.ConvertUserVisiblePathToModelPath(openPath), options);
            Step("모델 식별 및 중앙 연결 확인");
            if (doc.IsFamilyDocument || doc.IsLinked || doc.IsDetached || doc.IsReadOnly) throw new InvalidOperationException("일반 프로젝트 원본만 지원합니다. 패밀리·링크·분리·읽기 전용 문서는 처리하지 않습니다.");
            if (doc.IsWorkshared != info.IsWorkshared) throw new InvalidOperationException("파일의 작업공유 상태가 달라졌습니다.");
            if (doc.IsWorkshared && !string.Equals(Canonical(ModelPathUtils.ConvertModelPathToUserVisiblePath(doc.GetWorksharingCentralModelPath())), central, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("원래 중앙모델과 연결이 다릅니다.");
            // WorksharingCentralGUID는 Revit Server 전용이다. 파일 기반 중앙/로컬에 호출하면 예외가 난다.
            // 작업마다 새로 만드는 로컬의 경로 대신, 생성 식별자와 위에서 검증한 실제 중앙 경로를 결합한다.
            var creationId = doc.CreationGUID;
            if (creationId == Guid.Empty) throw new InvalidOperationException("모델 생성 식별자를 확인할 수 없습니다. 조회를 중단합니다.");
            var identity = "파일-v2/" + creationId.ToString("D") + "/" + (doc.IsWorkshared ? "중앙:" + central.ToUpperInvariant() : "일반");
            var metadata = new ModelSnapshot { Path = req.Path, CanonicalPath = source, Title = Path.GetFileNameWithoutExtension(source), CentralPath = central, Kind = isCentral ? "중앙" : doc.IsWorkshared ? "로컬" : "일반", Identity = identity };
            if (req.Operation == "링크 검사")
            {
                Step("링크·가져오기와 삭제 영향 검사 — 원본 저장 안 함");
                result.LinkRemoval = LinkRemovalEngine.Preview(doc, metadata);
                result.Success = true;
                result.Message = $"링크·가져오기 {result.LinkRemoval.Targets.Count:N0}개, 삭제 영향 {result.LinkRemoval.Affected.Count:N0}개를 검사했습니다. 원본을 저장하지 않았습니다.";
            }
            else if (req.Operation == "조회")
            {
                Step("매개변수 읽기");
                var reader = new ParameterReader(doc, metadata);
                metadata.Rows = reader.ReadAll(req.Query);
                metadata.Notices = reader.Notices;
                var ownership = OwnershipConflicts(doc, metadata.Rows);
                if (ownership.Length > 0) metadata.Notices.Add(ownership);
                if (metadata.Kind == "로컬") metadata.Notices.Add("기존 로컬을 적용하면 이 파일에 저장된 미동기화 변경도 중앙에 포함됩니다.");
                result.Snapshot = metadata;
                result.Success = true;
                result.Message = metadata.MaterialAuditItems is { } audit ? $"재료 점검 {audit.Count:N0}건을 읽었습니다. 원본을 저장하지 않았습니다." : $"{metadata.Rows.Count:N0}개 매개변수 값을 읽었습니다. 원본을 저장하지 않았습니다.";
            }
            else
            {
                if (identity != req.Identity) throw new InvalidOperationException("조회했던 모델과 다른 문서입니다. 다시 조회하세요.");
                if (req.Edits.Any(e => !string.Equals(e.Original.ModelPath, req.Path, StringComparison.OrdinalIgnoreCase)) || req.Edits.Select(e => e.Original.Key).Distinct().Count() != req.Edits.Count)
                    throw new InvalidOperationException("서로 다른 모델 또는 중복 변경이 한 요청에 포함되어 있습니다.");
                Step("변경 전 재검증");
                var ownership = OwnershipConflicts(doc, req.Edits.Select(e => e.Original));
                if (ownership.Length > 0) throw new InvalidOperationException(ownership);
                var reader = new ParameterReader(doc, metadata);
                List<WriteAction> writes;
                if (removingLinks)
                {
                    var removal = LinkRemovalEngine.Prepare(doc, metadata, req.LinkRemoval!, req.DependentsConfirmed);
                    writes = [new(removal.Delete, removal.Verify)];
                }
                else if (req.Edits.Any(e => e.DefinitionFix != null)) writes = DefinitionFixer.Prepare(app, doc, req.Edits);
                else if (req.Edits.Any(e => e.KacCleanup)) writes = CleanupWriter.Prepare(doc, metadata, req.Edits).Select(w => new WriteAction(w.Set, w.Verify)).ToList();
                else writes = PrepareAll(app, doc, reader, req.Edits);
                var stairNames = removingLinks ? [] : StairTypeNames.Capture(doc, req.Edits);
                using var diagnostics = new TransactionDiagnostics(app, doc);
                using (var group = new TransactionGroup(doc, "Sunny 매개변수 관리자"))
                {
                    if (group.Start() != TransactionStatus.Started) throw new InvalidOperationException("편집 묶음을 시작할 수 없습니다.");
                    try
                    {
                        using var tx = new Transaction(doc, removingLinks ? "모든 링크·가져오기 완전 제거" : req.Edits.Any(e => e.DefinitionFix != null) ? "속성규격 매개변수 정의 정리" : "선택한 매개변수 값 변경");
                        if (tx.Start() != TransactionStatus.Started) throw new InvalidOperationException("편집을 시작할 수 없습니다.");
                        editingStarted = true;
                        tx.SetFailureHandlingOptions(tx.GetFailureHandlingOptions().SetForcedModalHandling(true));
                        Step(removingLinks ? "승인한 링크·가져오기 삭제 중" : "값 적용 중");
                        foreach (var w in writes) w.Set();
                        var committed = tx.Commit();
                        if (committed != TransactionStatus.Committed) throw new InvalidOperationException("Revit이 변경을 확정하지 못했습니다: " + committed + "\n" + diagnostics.Details);
                        Step("확정 결과 재검증");
                        if (diagnostics.HasErrors)
                            throw new InvalidOperationException("작업 중 Revit 모델 오류가 발생하여 이번 묶음을 취소합니다. 오류 창에서 결합 해제·요소 삭제 등을 선택했더라도 이 작업으로 저장하지 않습니다.\n" + diagnostics.Details + "\n원본에서 문제 요소를 검토한 뒤 다시 조회하세요.");
                        restoredStairNames = StairTypeNames.Restore(doc, stairNames);
                        if (diagnostics.HasErrors) throw new InvalidOperationException("계단 유형 이름 보존 중 Revit 오류가 발생하여 이번 묶음을 취소합니다.\n" + diagnostics.Details);
                        foreach (var w in writes) w.Verify();
                        if (group.Assimilate() != TransactionStatus.Committed) throw new InvalidOperationException("편집 묶음을 확정하지 못했습니다.");
                    }
                    catch { if (group.GetStatus() == TransactionStatus.Started) group.RollBack(); throw; }
                }
                // 여기 이후의 실패는 모델이 이미 바뀌었다. 문서를 버리거나 자동 재시도하지 않는다.
                preserve = true;
                Step("로컬 저장 중");
                doc.Save();
                if (doc.IsWorkshared)
                {
                    Step("중앙 동기화 중 — 중단 시 결과 확인 필요");
                    using var sync = new SynchronizeWithCentralOptions { SaveLocalBefore = true, SaveLocalAfter = true, Comment = "Sunny Parameter Manager " + req.Id };
                    using var relinquish = new RelinquishOptions(true);
                    sync.SetRelinquishOptions(relinquish);
                    using var transact = new TransactWithCentralOptions();
                    transact.SetLockCallback(new NoWait());
                    doc.SynchronizeWithCentral(transact, sync);
                    result.CentralSynchronized = true;
                    result.Applied = removingLinks ? req.LinkRemoval!.Affected.Count : writes.Count;
                    OwnershipRelease.Complete(doc, Step);
                    result.OwnershipReleased = true;
                }
                if (removingLinks)
                {
                    Step("저장·동기화 후 남은 링크 확인");
                    LinkRemovalEngine.CheckComplete(doc, metadata);
                }
                result.Applied = removingLinks ? req.LinkRemoval!.Affected.Count : writes.Count;
                result.Success = true;
                result.Message = doc.IsWorkshared ? "중앙 동기화 및 차용 요소·모든 작업세트 소유권 반환 호출이 완료되었습니다. 반환 후 검사에서 현재 사용자 소유권 잔존이 발견되지 않았습니다. 다른 사용자의 로컬은 최신 내용 가져오기가 필요합니다." : "파일 저장이 완료되었습니다.";
                if (restoredStairNames > 0) result.Message += $"\n매개변수 확정 중 재생성된 계단진행 유형 {restoredStairNames:N0}개의 이름을 승인한 이름으로 보존하고 재검증했습니다.";
                if (removingLinks) result.Message = $"링크 정의·가져오기 완전 제거 및 승인한 종속 요소 삭제: 총 {result.Applied:N0}개. 외부 원본 파일은 변경하지 않았습니다.\n" + result.Message;
                Step(doc.IsWorkshared ? "중앙 동기화·소유권 반환 완료" : "저장 완료");
                preserve = false;
            }
            Step("문서 닫기");
            if (!doc.Close(false)) { preserve = true; throw new InvalidOperationException("작업은 끝났지만 문서를 닫지 못했습니다."); }
            doc = null;
            result.Stage = "완료";
        }
        catch (Exception ex)
        {
            // 저장 전 실패도 롤백 뒤 획득한 차용권이 남을 수 있다. 동기화 실패 문서는 여기서 반환하지 않는다.
            if (editingStarted && !preserve && doc?.IsWorkshared == true)
            {
                try { OwnershipRelease.Complete(doc, _ => { }); result.OwnershipReleased = true; }
                catch (Exception releaseError) { preserve = true; result.Message += "\n미저장 작업 취소 후 소유권 반환 확인 실패: " + releaseError.Message + "\n"; }
            }
            if (req.Operation == "조회" && ex is OutOfMemoryException)
            {
                result.Success = false; result.Snapshot = null; result.ReadRestartRequired = true;
            }
            result.Message = result.Message + (result.Success ? "\n후처리 안내: " : "") + (string.IsNullOrEmpty(result.Stage) ? "" : "[" + result.Stage + "] ") + ex.Message +
                (result.CentralSynchronized && !result.OwnershipReleased ? "\n중앙 동기화 자체는 완료되었습니다. 소유권 반환/잔존 확인이 끝나지 않았습니다. 변경을 다시 적용하지 마세요." : "") +
                (preserve ? "\n변경 또는 소유권 반환 상태를 확인해야 합니다. 작업용 Revit과 로컬을 보존합니다. 자동 재실행하지 마세요." : "");
            result.RequiresAttention = preserve;
            // 저장/동기화 성공 뒤 닫기만 실패한 경우 이미 성공한 변경을 실패로 거짓 보고하지 않는다.
            if (!preserve && doc != null)
            {
                try { if (!doc.Close(false)) result.RequiresAttention = true; }
                catch { result.RequiresAttention = true; }
            }
            if (!result.Success) result.Stage = result.CentralSynchronized && !result.OwnershipReleased ? "중앙 반영 완료 · 소유권 반환 확인 필요" : preserve ? "반영 결과 확인 필요" : "실패 — 이번 변경 미저장";
            // 수정 전 차단만 다음 모델 진행 후보다. 실제 빈 문서 여부는 Worker에서 다시 검사한다.
            result.CanContinueAfterPreflightFailure = req.Operation == "적용" && !editingStarted && !result.Success &&
                !result.RequiresAttention && !result.CentralSynchronized && result.Applied == 0 && ex is not OutOfMemoryException;
        }
        finally { if (locked) gate?.ReleaseMutex(); gate?.Dispose(); }
        return result;
    }

    internal sealed record WriteAction(Action Set, Action Verify);

    private static List<WriteAction> PrepareAll(RevitApplication app, Document doc, ParameterReader reader, IEnumerable<ValueEdit> edits)
    {
        var writes = new List<WriteAction>(); var issues = new List<string>(); int count = 0;
        foreach (var edit in edits)
        {
            try { writes.Add(edit.AddParameter != null ? SharedParameterAdder.Prepare(app, doc, reader, edit) : Prepare(doc, reader, edit)); }
            catch (InvalidOperationException ex)
            {
                count++;
                if (issues.Count < 12) issues.Add(ex.Message);
            }
        }
        if (count > 0) throw new InvalidOperationException($"적용 전 재검증에서 {count:N0}개 변경 항목을 보류했습니다. 이 모델에는 값을 쓰지 않았습니다.\n" +
            string.Join("\n\n", issues) + (count > issues.Count ? $"\n외 {count - issues.Count:N0}개 항목. 이 파일을 다시 조회하세요." : ""));
        return writes;
    }

    internal static BasicFileInfo ReadFileInfo(string source, Action<string> step, Action<int>? wait = null)
    {
        // 원본 읽기만 제한적으로 재시도한다. 문서 열기·적용·저장·동기화는 반복하지 않는다.
        wait ??= Thread.Sleep;
        Exception? last = null;
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            BasicFileInfo? info = null;
            try
            {
                var before = new FileInfo(source); var size = before.Length; var time = before.LastWriteTimeUtc;
                info = BasicFileInfo.Extract(source);
                var after = new FileInfo(source);
                if (info.IsInProgress || size != after.Length || time != after.LastWriteTimeUtc)
                    throw new IOException("파일 저장 중이거나 기본정보를 읽는 동안 파일이 변경되었습니다.");
                return info;
            }
            catch (Exception ex) when (ex is IOException || ex is Autodesk.Revit.Exceptions.InvalidOperationException || ex is System.InvalidOperationException)
            {
                info?.Dispose(); last = ex;
                if (attempt < 3) { step($"파일 기본정보 읽기 재확인 ({attempt + 1}/3) — 원본 저장 안 함"); wait(500); }
            }
            catch { info?.Dispose(); throw; }
        }
        throw new InvalidOperationException("파일 기본정보를 3회 확인했지만 읽을 수 없었습니다. 파일 형식이 잘못됐다고 단정할 수 없습니다. " +
            "다른 작업자의 저장·동기화가 끝났는지, 서버 연결과 Revit 2026에서 정상 열리는지 확인한 뒤 이 파일만 다시 조회하세요. " +
            "형식 확인을 건너뛰거나 자동 업그레이드하지 않았습니다.\n파일: " + source + "\n원본 오류: " + last?.Message, last);
    }

    private static string OwnershipConflicts(Document doc, IEnumerable<ParameterRow> rows)
    {
        if (!doc.IsWorkshared) return "";
        int count = 0; var examples = new List<string>();
        foreach (var row in rows.DistinctBy(r => r.OwnerId))
        {
            var element = doc.GetElement(row.OwnerId);
            if (element == null || WorksharingUtils.GetCheckoutStatus(doc, element.Id, out var borrower) != CheckoutStatus.OwnedByOtherUser) continue;
            count++;
            if (examples.Count < 12) examples.Add($"소유 사용자: {(string.IsNullOrWhiteSpace(borrower) ? "확인 불가" : borrower)} / 요소 ID: {element.Id.Value} / {row.TargetName}");
        }
        return count == 0 ? "" : $"다른 사용자 소유로 감지된 대상 {count:N0}개가 있습니다. 강제로 변경하지 않습니다.\n" +
            string.Join("\n", examples) + (count > examples.Count ? $"\n외 {count - examples.Count:N0}개" : "") +
            "\n해당 사용자에게 중앙 동기화 및 차용 요소·작업세트 반환을 요청한 뒤 다시 조회하세요. 파일을 단순히 닫는 것만으로는 반환되지 않을 수 있습니다. " +
            "소유권 표시는 로컬 캐시 기준이므로 반환 후 새 조회로 재확인하며, 같은 계정의 다른 작업 중 로컬도 확인하세요.";
    }
    private static WriteAction Prepare(Document doc, ParameterReader reader, ValueEdit edit)
    {
        var expected = edit.Original;
        if (edit.AddParameter != null) throw new InvalidOperationException("매개변수 추가 항목은 별도 검증 경로에서만 처리합니다.");
        var owner = doc.GetElement(expected.OwnerId) ?? throw new InvalidOperationException($"조회 당시 요소가 현재 모델에 없습니다. 삭제·교체 여부를 확인하고 다시 조회하세요: {expected.TypeName} / 요소 ID: {expected.ElementId} / 항목: {expected.Name}. 동명 요소에 대신 입력하지 않습니다.");
        var p = ParameterReader.Resolve(owner, expected.ParameterKey) ?? throw new InvalidOperationException("매개변수가 없거나 중복됩니다: " + expected.Name);
        var actual = reader.Read(owner, p);
        EditPlanner.CheckFresh(expected, actual);
        if (edit.MaterialSource != null) KacRulePlanner.CheckMaterial(edit, reader.ReadMaterial(owner, true));
        if (doc.IsWorkshared && WorksharingUtils.GetCheckoutStatus(doc, owner.Id) == CheckoutStatus.OwnedByOtherUser)
            throw new InvalidOperationException("다른 사용자가 소유하고 있습니다: " + expected.TypeName);
        if (edit.Mode is not ("입력" or "원시값")) throw new InvalidOperationException("알 수 없는 값 입력 형식입니다.");
        if (edit.Mode == "입력")
        {
            var error = EditPlanner.Validate(actual, edit.Value);
            if (error.Length > 0) throw new InvalidOperationException(actual.Name + ": " + error);
        }
        string raw;
        Action setter;
        if (actual.Editor == "선택")
        {
            if (edit.Mode != "입력") throw new InvalidOperationException("모델 간 참조 ID 직접 복사는 허용하지 않습니다.");
            var choice = actual.Choices.Single(c => c.Name == edit.Value);
            if (edit.ReferenceKey != choice.Key) throw new InvalidOperationException("조회 후 참조 대상이 달라졌습니다.");
            if (p.Id.Value == (long)BuiltInParameter.ELEM_PARTITION_PARAM)
            {
                var ws = new FilteredWorksetCollector(doc).OfKind(WorksetKind.UserWorkset).Single(w => w.UniqueId.ToString("D") == choice.Key);
                raw = ws.Id.IntegerValue.ToString(Invariant); setter = () => Require(p.Set(ws.Id.IntegerValue));
            }
            else
            {
                var target = doc.GetElement(choice.Key) as Material ?? throw new InvalidOperationException("재료가 없어졌습니다.");
                raw = target.Id.Value.ToString(Invariant); setter = () => Require(p.Set(target.Id));
            }
        }
        else if (p.StorageType == StorageType.String)
        {
            raw = edit.Mode == "원시값" ? edit.RawValue : edit.Value; setter = () => Require(p.Set(raw));
        }
        else if (p.StorageType == StorageType.Integer)
        {
            var input = edit.Mode == "원시값" ? edit.RawValue : actual.Editor == "예/아니요" ? edit.Value == "예" ? "1" : "0" : edit.Value;
            var number = int.Parse(input, NumberStyles.Integer, Invariant);
            if (actual.Editor == "예/아니요" && number is not (0 or 1)) throw new InvalidOperationException("예/아니요 값은 0 또는 1이어야 합니다.");
            raw = number.ToString(Invariant); setter = () => Require(p.Set(number));
        }
        else if (p.StorageType == StorageType.Double)
        {
            double number;
            if (edit.Mode == "원시값" || actual.Editor == "숫자") number = double.Parse(edit.Mode == "원시값" ? edit.RawValue : edit.Value, Invariant);
            else if (!UnitFormatUtils.TryParse(doc.GetUnits(), p.Definition.GetDataType(), edit.Value, out number)) throw new InvalidOperationException("단위 숫자를 해석할 수 없습니다: " + edit.Value);
            if (!double.IsFinite(number)) throw new InvalidOperationException("유한한 숫자만 허용합니다.");
            raw = number.ToString("R", Invariant); setter = () => Require(p.Set(number));
        }
        else throw new InvalidOperationException("지원하지 않는 값입니다.");
        return new WriteAction(setter, () =>
        {
            var freshOwner = doc.GetElement(expected.OwnerId) ?? throw new InvalidOperationException("확정 후 대상 요소가 없어져 이번 묶음을 취소합니다. 요소 ID: " + expected.ElementId);
            var freshParameter = ParameterReader.Resolve(freshOwner, expected.ParameterKey) ?? throw new InvalidOperationException("확정 후 대상 매개변수가 없어졌습니다: " + expected.Name + " / 요소 ID: " + expected.ElementId);
            var verified = reader.Read(freshOwner, freshParameter);
            EditPlanner.CheckGroupFresh(expected, verified);
            if (expected.TypeOwnerId != verified.TypeOwnerId || expected.TypeKey != verified.TypeKey)
                throw new InvalidOperationException($"Revit 확정 후 요소의 유형 정보가 달라 이번 묶음을 취소합니다.\n요소 ID: {expected.ElementId} / 항목: {expected.Name}\n적용 전: {expected.Family} / {expected.TypeName} / 유형 식별자 {expected.TypeOwnerId}\n적용 후: {verified.Family} / {verified.TypeName} / 유형 식별자 {verified.TypeOwnerId}\n같은 작업을 반복하지 말고 원본 요소 확인 후 다시 조회하세요.");
            if (expected.OwnerId != verified.OwnerId || expected.ParameterKey != verified.ParameterKey || expected.DataType != verified.DataType || expected.Storage != verified.Storage)
                throw new InvalidOperationException("확정 후 대상 매개변수의 식별 정보가 달라 이번 묶음을 취소합니다: " + expected.Name + " / 요소 ID: " + expected.ElementId);
            // 같은 묶음의 다른 수동 편집이 재료를 바꾼 경우도 전체 롤백한다.
            if (edit.MaterialSource != null) KacRulePlanner.CheckMaterial(edit, reader.ReadMaterial(freshOwner, true));
            var check = ParameterReader.Raw(freshParameter);
            var same = freshParameter.StorageType == StorageType.Double ? Math.Abs(double.Parse(check, Invariant) - double.Parse(raw, Invariant)) <= Math.Max(1e-9, Math.Abs(double.Parse(raw, Invariant)) * 1e-10) : check == raw;
            if (!same) throw new InvalidOperationException("Revit 확정 후 값이 요청과 달라 이번 묶음을 취소합니다: " + expected.Name);
        });
    }

    private static void Require(bool ok) { if (!ok) throw new InvalidOperationException("Revit이 매개변수 값 변경을 거부했습니다."); }
    private sealed class NoWait : ICentralLockedCallback { public bool ShouldWaitForLockAvailability() => false; }

    internal static string Canonical(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || !File.Exists(path)) throw new IOException("파일을 찾을 수 없습니다: " + path);
        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var buffer = new StringBuilder(32768);
        var length = GetFinalPathNameByHandle(handle.DangerousGetHandle(), buffer, (uint)buffer.Capacity, 0);
        if (length == 0 || length >= buffer.Capacity) throw new IOException("실제 파일 경로를 확인할 수 없습니다.");
        var resolved = buffer.ToString();
        if (resolved.StartsWith("\\\\?\\UNC\\", StringComparison.OrdinalIgnoreCase)) return "\\\\" + resolved[8..];
        return resolved.StartsWith("\\\\?\\", StringComparison.Ordinal) ? resolved[4..] : resolved;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(IntPtr file, StringBuilder path, uint length, uint flags);
}
