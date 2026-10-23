// 실제 ModelEngine.cs의 제어 흐름만 검사하는 API 대역. 실제 Revit 저장/동기화 검증을 대신하지 않는다.
using SunnyParameterManager.Core;

namespace Autodesk.Revit.Exceptions { public class InvalidOperationException(string message) : Exception(message); }

namespace Autodesk.Revit.ApplicationServices
{
 public sealed class Application
 {
  public Application() { DB.State.App = this; }
  public event EventHandler<DB.Events.FailuresProcessingEventArgs>? FailuresProcessing;
  public int Observers => FailuresProcessing?.GetInvocationList().Length ?? 0;
  public void Raise(DB.Document doc) => FailuresProcessing?.Invoke(this, new(doc));
  public string Username => "테스터";
  public DB.Document OpenDocumentFile(DB.ModelPath path, DB.OpenOptions options) { DB.State.Events.Add("open"); return DB.State.Doc; }
 }
}
namespace Autodesk.Revit.DB.Events
{
 public sealed class FailuresProcessingEventArgs(Document doc) : EventArgs
 {
  public FailuresAccessor GetFailuresAccessor() => State.UnreadableFailure ? throw new InvalidOperationException("진단 실패") : new(doc);
 }
}
namespace Autodesk.Revit.DB
{
 public static class State
 {
  public static Document Doc = new();
  public static BasicFileInfo Info = new();
  public static List<string> Events = [];
  public static bool FailSave, FailSync, FailClose, FailCommit, FailSet, VerifyMismatch, OwnedOther, MaterialChangesAfterSet, TypeChangesAfterSet;
  public static string TypeOwnerId = "type-1";
  public static string Scope = "유형";
  public static bool MaterialMissing;
  public static bool ReadOutOfMemory;
  public static int InfoFailures, InfoCalls, InfoDisposals;
  public static Action? InfoRead;
  public static bool InfoOutOfMemory;
  public static bool RegenerateStairName, FailNameRestore;
  public static GroupEditEvidence? GroupEdit;
  public static bool GroupChangesAfterSet;
  public static bool FailRelinquish, LeaveBorrowed, LeaveWorkset, FailOwnershipCheck;
  public static HashSet<long> OwnedIds = [];
  public static ApplicationServices.Application App = null!;
  public static FailureSeverity? Failure;
  public static bool OtherDocFailure, UnreadableFailure, DeletedAfterCommit;
  public static void Reset() { FailRelinquish = LeaveBorrowed = LeaveWorkset = FailOwnershipCheck = false; OwnedIds = []; GroupEdit = null; GroupChangesAfterSet = false; Doc = new(); Info = new(); Events = []; TypeOwnerId = "type-1"; MaterialMissing = false; Failure = null; OtherDocFailure = UnreadableFailure = DeletedAfterCommit = false; FailSave = FailSync = FailClose = FailCommit = FailSet = VerifyMismatch = OwnedOther = MaterialChangesAfterSet = TypeChangesAfterSet = false; }
 }
 public enum FailureSeverity { Warning, Error, DocumentCorruption }
 public sealed class FailureMessageAccessor
 {
  public FailureSeverity GetSeverity() => State.Failure!.Value;
  public string GetDescriptionText() => "요소를 결합된 상태로 유지할 수 없습니다.";
  public IEnumerable<ElementId> GetFailingElementIds() => [new(101), new(102)];
  public IEnumerable<ElementId> GetAdditionalElementIds() => [new(102), new(103)];
 }
 public sealed class FailuresAccessor(Document doc) : IDisposable
 {
  public Document GetDocument() => doc;
  public IEnumerable<FailureMessageAccessor> GetFailureMessages() => State.Failure == null ? [] : [new()];
  public void Dispose() { }
 }
 public enum StorageType { String, Integer, Double, ElementId }
 public enum BuiltInParameter { ELEM_PARTITION_PARAM = -100 }
 public enum CheckoutStatus { OwnedByOtherUser, OwnedByCurrentUser, NotOwned }
 public enum TransactionStatus { Uninitialized, Started, Committed, RolledBack }
 public enum DetachFromCentralOption { DoNotDetach }
 public enum WorksetConfigurationOption { OpenAllWorksets }
 public enum WorksetKind { UserWorkset }
 public sealed record ElementId(long Value);
 public sealed class Definition { public object GetDataType() => new(); }
 public sealed class Parameter
 {
  public ElementId Id { get; set; } = new(1);
  public StorageType StorageType { get; set; } = StorageType.String;
  public Definition Definition { get; } = new();
  public string Value { get; set; } = "기존";
  public bool Set(string v) { State.Events.Add("set"); if (State.Doc.IsWorkshared) State.OwnedIds.Add(10); if (State.FailSet) return false; Value = State.VerifyMismatch ? "다른값" : v; return true; }
  public bool Set(int v) => Set(v.ToString());
  public bool Set(double v) => Set(v.ToString(System.Globalization.CultureInfo.InvariantCulture));
  public bool Set(ElementId v) => Set(v.Value.ToString());
 }
 public class Element { public ElementId Id { get; set; } = new(10); public string UniqueId { get; set; } = "type-1"; public virtual string Name { get; set; } = "기존 이름"; public Category? Category { get; set; } = new(); public Parameter Parameter { get; } = new(); }
 public class Category { public ElementId Id { get; set; } = new(-2000919); }
 public class ElementType : Element { public string FamilyName { get; set; } = "일체형 계단진행"; public bool CanBeRenamed { get; set; } = true; }
 public sealed class Material : Element { }
 public sealed class Document
 {
  public bool IsFamilyDocument, IsLinked, IsDetached, IsReadOnly, IsWorkshared;
  public Guid CreationGUID { get; set; } = Guid.NewGuid();
  // 파일 기반 모델에서는 실제 Revit처럼 실패해야 서버 전용 API 오용을 잡을 수 있다.
  public Guid WorksharingCentralGUID => throw new InvalidOperationException("The current document is a file-based model.");
  public string? CentralPathOverride;
  public Element Element { get; } = new();
  public Dictionary<string, Element> Types { get; } = [];
  public bool Missing;
  public ModelPath GetWorksharingCentralModelPath() => new(CentralPathOverride ?? State.Info.CentralPath);
  public Element? GetElement(string id) => Missing || State.DeletedAfterCommit && State.Events.Contains("tx-commit") ? null : Types.GetValueOrDefault(id, Element);
  public object GetUnits() => new();
  public void Save() { State.Events.Add("save"); if (State.FailSave) throw new IOException("테스트 저장 실패"); }
  public void SynchronizeWithCentral(TransactWithCentralOptions transact, SynchronizeWithCentralOptions sync)
  { State.Events.Add("sync"); if (!sync.SaveLocalBefore || !sync.SaveLocalAfter || !sync.Relinquish!.All || transact.Callback!.ShouldWaitForLockAvailability()) throw new Exception("동기화 옵션 오류"); if (State.FailSync) throw new IOException("테스트 동기화 실패"); }
  public bool Close(bool save) { State.Events.Add(save ? "close-save" : "close"); return !State.FailClose; }
 }
 public sealed class BasicFileInfo : IDisposable
 {
  public bool IsSavedInCurrentVersion = true, IsInProgress, IsWorkshared;
  public string Format = "2026", CentralPath = "", Username = "테스터";
  public static BasicFileInfo Extract(string path)
  {
   State.InfoCalls++; State.InfoRead?.Invoke();
   if (State.InfoOutOfMemory) throw new OutOfMemoryException();
   if (State.InfoCalls <= State.InfoFailures) throw new Autodesk.Revit.Exceptions.InvalidOperationException("The file is a newer format file");
   return State.Info;
  }
  public void Dispose() { State.InfoDisposals++; }
 }
 public sealed record ModelPath(string Path);
 public static class ModelPathUtils { public static ModelPath ConvertUserVisiblePathToModelPath(string p) => new(p); public static string ConvertModelPathToUserVisiblePath(ModelPath p) => p.Path; }
 public static class WorksharingUtils
 {
  public static CheckoutStatus GetCheckoutStatus(Document doc, ElementId id, out string owner) { var status = GetCheckoutStatus(doc, id); owner = status == CheckoutStatus.OwnedByOtherUser ? "다른작업자" : ""; return status; }
  public static void CreateNewLocal(ModelPath central, ModelPath local) { State.Events.Add("create-local"); File.Copy(central.Path, local.Path, false); }
  public static CheckoutStatus GetCheckoutStatus(Document doc, ElementId id) => State.FailOwnershipCheck && State.Events.Contains("relinquish") ? throw new IOException("반환 확인 실패") : State.OwnedOther || id.Value == 30 ? CheckoutStatus.OwnedByOtherUser : State.OwnedIds.Contains(id.Value) ? CheckoutStatus.OwnedByCurrentUser : CheckoutStatus.NotOwned;
  public static RelinquishedItems RelinquishOwnership(Document doc, RelinquishOptions all, TransactWithCentralOptions access)
  {
   State.Events.Add("relinquish"); if (!all.All || access.Callback!.ShouldWaitForLockAvailability()) throw new Exception("반환 옵션 오류");
   if (State.FailRelinquish) throw new IOException("중앙 반환 실패");
   if (!State.LeaveBorrowed) State.OwnedIds.Clear(); return new();
  }
 }
 public sealed class RelinquishedItems : IDisposable { public void Dispose() { } }
 public sealed class FilteredElementCollector(Document doc) : IDisposable
 {
  private bool _types;
  public FilteredElementCollector WhereElementIsElementType() { _types = true; return this; }
  public FilteredElementCollector WhereElementIsNotElementType() { _types = false; return this; }
  public ICollection<ElementId> ToElementIds() => _types ? [new(20)] : [new(10), new(30)];
  public void Dispose() { }
 }
 public sealed class OpenOptions : IDisposable { public DetachFromCentralOption DetachFromCentralOption { get; set; } public void SetOpenWorksetsConfiguration(WorksetConfiguration config) { } public void Dispose() { } }
 public sealed class WorksetConfiguration(WorksetConfigurationOption option) { }
 public sealed class FailureOptions { public FailureOptions SetForcedModalHandling(bool enabled) { if (!enabled) throw new Exception(); return this; } }
 public sealed class Transaction(Document doc, string name) : IDisposable
 {
  public TransactionStatus Start() { State.Events.Add("tx-start"); return TransactionStatus.Started; }
  public FailureOptions GetFailureHandlingOptions() => new();
  public void SetFailureHandlingOptions(FailureOptions options) { }
  public TransactionStatus Commit()
  {
   State.Events.Add("tx-commit");
   if (State.RegenerateStairName && name == "선택한 매개변수 값 변경") foreach (var t in doc.Types.Values) t.Name = "KAC-S-ST1110_ND-T1";
   if (name == "계단진행 유형의 승인한 이름 보존") { State.Events.Add("stair-name-preserve"); if (State.FailNameRestore) return TransactionStatus.RolledBack; }
   if (State.Failure != null || State.UnreadableFailure) State.App.Raise(State.OtherDocFailure ? new Document() : doc);
   return State.FailCommit ? TransactionStatus.RolledBack : TransactionStatus.Committed;
  }
  public void Dispose() { }
 }
 public sealed class TransactionGroup(Document doc, string name) : IDisposable
 {
  private TransactionStatus _status;
  private string _original = "";
  private Dictionary<string, string> _names = [];
  public TransactionStatus Start() { _original = doc.Element.Parameter.Value; _names = doc.Types.ToDictionary(p => p.Key, p => p.Value.Name); State.Events.Add("group-start"); return _status = TransactionStatus.Started; }
  public TransactionStatus GetStatus() => _status;
  public TransactionStatus Assimilate() { State.Events.Add("assimilate"); return _status = TransactionStatus.Committed; }
  public void RollBack() { State.Events.Add("rollback"); doc.Element.Parameter.Value = _original; foreach (var pair in _names) doc.Types[pair.Key].Name = pair.Value; _status = TransactionStatus.RolledBack; }
  public void Dispose() { }
 }
 public sealed class RelinquishOptions(bool all) : IDisposable { public bool All => all; public void Dispose() { } }
 public interface ICentralLockedCallback { bool ShouldWaitForLockAvailability(); }
 public sealed class SynchronizeWithCentralOptions : IDisposable
 { public bool SaveLocalBefore { get; set; } public bool SaveLocalAfter { get; set; } public string Comment { get; set; } = ""; public RelinquishOptions? Relinquish; public void SetRelinquishOptions(RelinquishOptions r) => Relinquish = r; public void Dispose() { } }
 public sealed class TransactWithCentralOptions : IDisposable { public ICentralLockedCallback? Callback; public void SetLockCallback(ICentralLockedCallback callback) => Callback = callback; public void Dispose() { } }
 public sealed class Workset { public Guid UniqueId { get; } = Guid.NewGuid(); public WorksetId Id { get; } = new(); public string Name => "프로젝트 표준"; public bool IsEditable => State.LeaveWorkset; }
 public sealed class WorksetId { public int IntegerValue => 1; }
 public sealed class FilteredWorksetCollector(Document doc) : List<Workset>([new()]), IDisposable { public FilteredWorksetCollector OfKind(WorksetKind kind) => this; public void Dispose() { } }
 public static class UnitFormatUtils { public static bool TryParse(object units, object spec, string value, out double number) => double.TryParse(value, out number); }
}
namespace Autodesk.Revit.DB.Architecture
{
 public sealed class StairsRunType : Autodesk.Revit.DB.ElementType { }
}
namespace SunnyParameterManager.Bridge
{
 using Autodesk.Revit.DB;
 internal static class CleanupWriter
 {
  internal sealed record Write(Action Set, Action Verify);
  internal static List<Write> Prepare(Document doc, ModelSnapshot model, List<ValueEdit> edits, RuleSet rules)
  {
   // 이 프로젝트는 저장/실패 경계를 검사한다. 실제 정리 Writer는 ReaderTests에서 링크해 검사한다.
   if (edits.Any(e => !e.KacCleanup)) throw new InvalidOperationException("혼합 요청 거부");
   State.Events.Add("cleanup-prepare");
   return [new(() => doc.Element.Parameter.Set(edits[0].Value), () => { State.Events.Add("cleanup-verify"); if (State.VerifyMismatch) throw new InvalidOperationException("정리 결과 불일치"); })];
  }
 }
 internal static class LinkRemovalEngine
 {
  internal static void CheckComplete(Document doc, ModelSnapshot model) => State.Events.Add("links-empty");
  internal sealed record RemovalAction(Action Delete, Action Verify);
  internal static LinkRemovalPlan Preview(Document doc, ModelSnapshot model) => new() { Model = model,
   Targets = [new() { UniqueId = "link", ElementId = 55, Revision = "r1", Root = true }],
   Affected = [new() { UniqueId = "link", ElementId = 55, Revision = "r1" }] };
  internal static RemovalAction Prepare(Document doc, ModelSnapshot model, LinkRemovalPlan plan, bool approved)
  { plan.Validate(approved); return new(() => State.Events.Add("delete-links"), () => { State.Events.Add("verify-links"); if (State.VerifyMismatch) throw new InvalidOperationException("링크 제거 결과 불일치"); }); }
 }
 internal sealed class ParameterReader(Document doc, ModelSnapshot model)
 {
  public MaterialEvidence ReadMaterial(Element owner, bool fresh = false) => State.MaterialMissing && !(State.MaterialChangesAfterSet && State.Events.Contains("set"))
   ? new() { FallbackAllowed = true, Reason = "유형 재료 없음" }
   : new() { Name = "KAC-테스트재료", Basis = "벽 외측층", Fingerprint = State.MaterialChangesAfterSet && State.Events.Contains("set") ? "material-v2" : "material-v1" };
  public List<string> Notices { get; } = [];
  public List<ParameterRow> ReadAll(QueryPlan? query = null) { model.Query = query; return State.ReadOutOfMemory ? throw new OutOfMemoryException("조회 메모리 대역") : [Read(doc.Element, doc.Element.Parameter)]; }
  public ParameterRow Read(Element owner, Parameter p) => new() { GroupEdit = State.GroupChangesAfterSet && State.Events.Contains("set") ? new() { Identity = "변경된그룹", Reason = "그룹 제한" } : State.GroupEdit, ModelPath = model.Path, ModelTitle = model.Title, OwnerId = "owner", ParameterKey = "id:1", Scope = State.Scope, TypeName = doc.Types.GetValueOrDefault(State.TypeOwnerId)?.Name ?? "W1", Family = (doc.Types.GetValueOrDefault(State.TypeOwnerId) as ElementType)?.FamilyName ?? "", TypeOwnerId = State.TypeChangesAfterSet && State.Events.Contains("set") ? "type-2" : State.TypeOwnerId, Name = "재료명", DataType = "text", Storage = "String", Editor = "텍스트", HasValue = true, Raw = p.Value, Display = p.Value };
  internal static Parameter? Resolve(Element owner, string key) => key == "id:1" ? owner.Parameter : null;
  internal static string Raw(Parameter p) => p.Value;
 }
}
