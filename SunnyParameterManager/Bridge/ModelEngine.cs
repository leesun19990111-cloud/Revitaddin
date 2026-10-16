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
        var result = new Response { Id = req.Id };
        void Step(string stage) { result.Stage = stage; progress(result); }
        try
        {
            if (req.Operation is not ("조회" or "적용")) throw new InvalidOperationException("지원하지 않는 작업입니다.");
            if (req.Operation == "적용" && (!req.Confirmed || req.Edits.Count == 0)) throw new InvalidOperationException("변경 확인이 없거나 변경 내용이 없습니다.");
            var source = Canonical(req.Path);
            if (!source.EndsWith(".rvt", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("RVT 파일만 선택하세요.");
            using var info = BasicFileInfo.Extract(source);
            if (!info.IsSavedInCurrentVersion) throw new InvalidOperationException($"Revit 2026 형식만 처리합니다. 선택 파일: {info.Format}. 자동 업그레이드는 하지 않습니다.");
            if (info.IsInProgress) throw new InvalidOperationException("저장 중인 파일은 처리하지 않습니다.");
            var central = info.IsWorkshared ? Canonical(info.CentralPath) : "";
            var isCentral = info.IsWorkshared && string.Equals(source, central, StringComparison.OrdinalIgnoreCase);
            if (info.IsWorkshared && !isCentral && !string.Equals(info.Username, app.Username, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("다른 사용자의 로컬 파일입니다. 원래 중앙파일을 선택하세요.");
            if (req.Operation == "적용" && info.IsWorkshared && !isCentral && !req.LocalChangesConfirmed)
                throw new InvalidOperationException("기존 로컬의 미동기화 작업까지 중앙에 포함된다는 확인이 필요합니다.");
            if (req.Operation == "적용" && !string.Equals(source, req.CanonicalPath, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("조회 이후 실제 파일 경로가 달라졌습니다.");
            var lockKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes((central.Length > 0 ? central : source).ToUpperInvariant())));
            gate = new Mutex(false, "Local\\SunnyParameterManager-" + lockKey);
            try { locked = gate.WaitOne(0); } catch (AbandonedMutexException) { locked = true; throw new InvalidOperationException("이 모델의 이전 작업이 비정상 종료됐습니다. 중앙 상태와 복구 기록을 확인한 뒤 다시 시도하세요."); }
            if (!locked) throw new InvalidOperationException("이 모델은 다른 매개변수 관리자 작업에서 사용 중입니다.");
            if (req.Operation == "적용" && !isCentral)
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
            if (req.Operation == "조회")
            {
                Step("매개변수 읽기");
                var reader = new ParameterReader(doc, metadata);
                metadata.Rows = reader.ReadAll();
                metadata.Notices = reader.Notices;
                if (metadata.Kind == "로컬") metadata.Notices.Add("기존 로컬을 적용하면 이 파일에 저장된 미동기화 변경도 중앙에 포함됩니다.");
                result.Snapshot = metadata;
                result.Success = true;
                result.Message = $"{metadata.Rows.Count:N0}개 매개변수 값을 읽었습니다. 원본을 저장하지 않았습니다.";
            }
            else
            {
                if (identity != req.Identity) throw new InvalidOperationException("조회했던 모델과 다른 문서입니다. 다시 조회하세요.");
                if (req.Edits.Any(e => !string.Equals(e.Original.ModelPath, req.Path, StringComparison.OrdinalIgnoreCase)) || req.Edits.Select(e => e.Original.Key).Distinct().Count() != req.Edits.Count)
                    throw new InvalidOperationException("서로 다른 모델 또는 중복 변경이 한 요청에 포함되어 있습니다.");
                Step("변경 전 재검증");
                var reader = new ParameterReader(doc, metadata);
                var writes = req.Edits.Select(edit => Prepare(doc, reader, edit)).ToList();
                using var diagnostics = new TransactionDiagnostics(app, doc);
                using (var group = new TransactionGroup(doc, "Sunny 매개변수 관리자"))
                {
                    if (group.Start() != TransactionStatus.Started) throw new InvalidOperationException("편집 묶음을 시작할 수 없습니다.");
                    try
                    {
                        using var tx = new Transaction(doc, "선택한 매개변수 값 변경");
                        if (tx.Start() != TransactionStatus.Started) throw new InvalidOperationException("편집을 시작할 수 없습니다.");
                        tx.SetFailureHandlingOptions(tx.GetFailureHandlingOptions().SetForcedModalHandling(true));
                        Step("값 적용 중");
                        foreach (var w in writes) w.Set();
                        var committed = tx.Commit();
                        if (committed != TransactionStatus.Committed) throw new InvalidOperationException("Revit이 변경을 확정하지 못했습니다: " + committed + "\n" + diagnostics.Details);
                        Step("확정 결과 재검증");
                        if (diagnostics.HasErrors)
                            throw new InvalidOperationException("매개변수 적용 중 Revit 모델 오류가 발생하여 이번 묶음을 취소합니다. 오류 창에서 결합 해제·요소 삭제 등을 선택했더라도 이 작업으로 저장하지 않습니다.\n" + diagnostics.Details + "\n원본에서 문제 요소를 검토한 뒤 다시 조회하세요.");
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
                    sync.SetRelinquishOptions(new RelinquishOptions(false));
                    using var transact = new TransactWithCentralOptions();
                    transact.SetLockCallback(new NoWait());
                    doc.SynchronizeWithCentral(transact, sync);
                }
                result.Applied = writes.Count;
                result.Success = true;
                result.Message = doc.IsWorkshared ? "중앙 동기화가 완료되었습니다. 다른 사용자의 로컬은 최신 내용 가져오기가 필요합니다. 소유권은 자동으로 일괄 반납하지 않았습니다." : "파일 저장이 완료되었습니다.";
                Step(doc.IsWorkshared ? "중앙 동기화 완료" : "저장 완료");
                preserve = false;
            }
            Step("문서 닫기");
            if (!doc.Close(false)) { preserve = true; throw new InvalidOperationException("작업은 끝났지만 문서를 닫지 못했습니다."); }
            doc = null;
            result.Stage = "완료";
        }
        catch (Exception ex)
        {
            result.Message = (result.Success ? result.Message + "\n후처리 안내: " : "") + (string.IsNullOrEmpty(result.Stage) ? "" : "[" + result.Stage + "] ") + ex.Message + (preserve ? "\n변경 또는 중앙 반영 여부를 확인해야 합니다. 작업용 Revit과 로컬을 보존합니다. 자동 재실행하지 마세요." : "");
            result.RequiresAttention = preserve;
            // 저장/동기화 성공 뒤 닫기만 실패한 경우 이미 성공한 변경을 실패로 거짓 보고하지 않는다.
            if (!preserve && doc != null)
            {
                try { if (!doc.Close(false)) result.RequiresAttention = true; }
                catch { result.RequiresAttention = true; }
            }
            if (!result.Success) result.Stage = preserve ? "반영 결과 확인 필요" : "실패 — 이번 변경 미저장";
        }
        finally { if (locked) gate?.ReleaseMutex(); gate?.Dispose(); }
        return result;
    }

    private sealed record WriteAction(Action Set, Action Verify);
    private static WriteAction Prepare(Document doc, ParameterReader reader, ValueEdit edit)
    {
        var expected = edit.Original;
        var owner = doc.GetElement(expected.OwnerId) ?? throw new InvalidOperationException("요소가 삭제되었습니다: " + expected.TypeName);
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
