// 실제 ModelEngine.cs의 제어 흐름만 검사하는 API 대역. 실제 Revit 저장/동기화 검증을 대신하지 않는다.
using SunnyParameterManager.Core;

namespace Autodesk.Revit.ApplicationServices
{
 public sealed class Application
 {
  public string Username => "테스터";
  public DB.Document OpenDocumentFile(DB.ModelPath path, DB.OpenOptions options) { DB.State.Events.Add("open"); return DB.State.Doc; }
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
  public static bool MaterialMissing;
  public static void Reset() { Doc = new(); Info = new(); Events = []; TypeOwnerId = "type-1"; MaterialMissing = false; FailSave = FailSync = FailClose = FailCommit = FailSet = VerifyMismatch = OwnedOther = MaterialChangesAfterSet = TypeChangesAfterSet = false; }
 }
 public enum StorageType { String, Integer, Double, ElementId }
 public enum BuiltInParameter { ELEM_PARTITION_PARAM = -100 }
 public enum CheckoutStatus { OwnedByOtherUser, NotOwned }
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
  public bool Set(string v) { State.Events.Add("set"); if (State.FailSet) return false; Value = State.VerifyMismatch ? "다른값" : v; return true; }
  public bool Set(int v) => Set(v.ToString());
  public bool Set(double v) => Set(v.ToString(System.Globalization.CultureInfo.InvariantCulture));
  public bool Set(ElementId v) => Set(v.Value.ToString());
 }
 public class Element { public ElementId Id { get; } = new(10); public Parameter Parameter { get; } = new(); }
 public sealed class Material : Element { }
 public sealed class Document
 {
  public bool IsFamilyDocument, IsLinked, IsDetached, IsReadOnly, IsWorkshared;
  public Guid CreationGUID { get; set; } = Guid.NewGuid();
  // 파일 기반 모델에서는 실제 Revit처럼 실패해야 서버 전용 API 오용을 잡을 수 있다.
  public Guid WorksharingCentralGUID => throw new InvalidOperationException("The current document is a file-based model.");
  public string? CentralPathOverride;
  public Element Element { get; } = new();
  public bool Missing;
  public ModelPath GetWorksharingCentralModelPath() => new(CentralPathOverride ?? State.Info.CentralPath);
  public Element? GetElement(string id) => Missing ? null : Element;
  public object GetUnits() => new();
  public void Save() { State.Events.Add("save"); if (State.FailSave) throw new IOException("테스트 저장 실패"); }
  public void SynchronizeWithCentral(TransactWithCentralOptions transact, SynchronizeWithCentralOptions sync)
  { State.Events.Add("sync"); if (!sync.SaveLocalBefore || !sync.SaveLocalAfter || sync.Relinquish!.All || transact.Callback!.ShouldWaitForLockAvailability()) throw new Exception("동기화 옵션 오류"); if (State.FailSync) throw new IOException("테스트 동기화 실패"); }
  public bool Close(bool save) { State.Events.Add(save ? "close-save" : "close"); return !State.FailClose; }
 }
 public sealed class BasicFileInfo : IDisposable
 {
  public bool IsSavedInCurrentVersion = true, IsInProgress, IsWorkshared;
  public string Format = "2026", CentralPath = "", Username = "테스터";
  public static BasicFileInfo Extract(string path) => State.Info;
  public void Dispose() { }
 }
 public sealed record ModelPath(string Path);
 public static class ModelPathUtils { public static ModelPath ConvertUserVisiblePathToModelPath(string p) => new(p); public static string ConvertModelPathToUserVisiblePath(ModelPath p) => p.Path; }
 public static class WorksharingUtils
 {
  public static void CreateNewLocal(ModelPath central, ModelPath local) { State.Events.Add("create-local"); File.Copy(central.Path, local.Path, false); }
  public static CheckoutStatus GetCheckoutStatus(Document doc, ElementId id) => State.OwnedOther ? CheckoutStatus.OwnedByOtherUser : CheckoutStatus.NotOwned;
 }
 public sealed class OpenOptions : IDisposable { public DetachFromCentralOption DetachFromCentralOption { get; set; } public void SetOpenWorksetsConfiguration(WorksetConfiguration config) { } public void Dispose() { } }
 public sealed class WorksetConfiguration(WorksetConfigurationOption option) { }
 public sealed class FailureOptions { public FailureOptions SetForcedModalHandling(bool enabled) { if (!enabled) throw new Exception(); return this; } }
 public sealed class Transaction(Document doc, string name) : IDisposable
 {
  public TransactionStatus Start() { State.Events.Add("tx-start"); return TransactionStatus.Started; }
  public FailureOptions GetFailureHandlingOptions() => new();
  public void SetFailureHandlingOptions(FailureOptions options) { }
  public TransactionStatus Commit() { State.Events.Add("tx-commit"); return State.FailCommit ? TransactionStatus.RolledBack : TransactionStatus.Committed; }
  public void Dispose() { }
 }
 public sealed class TransactionGroup(Document doc, string name) : IDisposable
 {
  private TransactionStatus _status;
  private string _original = "";
  public TransactionStatus Start() { _original = doc.Element.Parameter.Value; State.Events.Add("group-start"); return _status = TransactionStatus.Started; }
  public TransactionStatus GetStatus() => _status;
  public TransactionStatus Assimilate() { State.Events.Add("assimilate"); return _status = TransactionStatus.Committed; }
  public void RollBack() { State.Events.Add("rollback"); doc.Element.Parameter.Value = _original; _status = TransactionStatus.RolledBack; }
  public void Dispose() { }
 }
 public sealed class RelinquishOptions(bool all) { public bool All => all; }
 public interface ICentralLockedCallback { bool ShouldWaitForLockAvailability(); }
 public sealed class SynchronizeWithCentralOptions : IDisposable
 { public bool SaveLocalBefore { get; set; } public bool SaveLocalAfter { get; set; } public string Comment { get; set; } = ""; public RelinquishOptions? Relinquish; public void SetRelinquishOptions(RelinquishOptions r) => Relinquish = r; public void Dispose() { } }
 public sealed class TransactWithCentralOptions : IDisposable { public ICentralLockedCallback? Callback; public void SetLockCallback(ICentralLockedCallback callback) => Callback = callback; public void Dispose() { } }
 public sealed class Workset { public Guid UniqueId { get; } = Guid.NewGuid(); public WorksetId Id { get; } = new(); }
 public sealed class WorksetId { public int IntegerValue => 1; }
 public sealed class FilteredWorksetCollector(Document doc) : List<Workset> { public FilteredWorksetCollector OfKind(WorksetKind kind) => this; }
 public static class UnitFormatUtils { public static bool TryParse(object units, object spec, string value, out double number) => double.TryParse(value, out number); }
}
namespace SunnyParameterManager.Bridge
{
 using Autodesk.Revit.DB;
 internal sealed class ParameterReader(Document doc, ModelSnapshot model)
 {
  public MaterialEvidence ReadMaterial(Element owner, bool fresh = false) => State.MaterialMissing && !(State.MaterialChangesAfterSet && State.Events.Contains("set"))
   ? new() { FallbackAllowed = true, Reason = "유형 재료 없음" }
   : new() { Name = "KAC-테스트재료", Basis = "벽 외측층", Fingerprint = State.MaterialChangesAfterSet && State.Events.Contains("set") ? "material-v2" : "material-v1" };
  public List<string> Notices { get; } = [];
  public List<ParameterRow> ReadAll() => [Read(doc.Element, doc.Element.Parameter)];
  public ParameterRow Read(Element owner, Parameter p) => new() { ModelPath = model.Path, ModelTitle = model.Title, OwnerId = "owner", ParameterKey = "id:1", Scope = "유형", TypeName = "W1", TypeOwnerId = State.TypeChangesAfterSet && State.Events.Contains("set") ? "type-2" : State.TypeOwnerId, Name = "재료명", DataType = "text", Storage = "String", Editor = "텍스트", HasValue = true, Raw = p.Value, Display = p.Value };
  internal static Parameter? Resolve(Element owner, string key) => key == "id:1" ? owner.Parameter : null;
  internal static string Raw(Parameter p) => p.Value;
 }
}
