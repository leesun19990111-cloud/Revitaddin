using System.Collections;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
namespace System.Windows.Interop { public class WindowInteropHelper(object w) { public IntPtr Owner {get;set;} } }
namespace WallSplitter {
 // 실제 서비스의 로컬 저장 루트만 테스트 작업 폴더로 한정한다.
 public static class Environment { public enum SpecialFolder {LocalApplicationData} public static string GetFolderPath(SpecialFolder _) => Path.GetFullPath("fixtures"); }
 internal class ChangeLogEntry { public string SourceDocumentTitle=""; }
 internal class ChangeLog { public static void Append(IEnumerable<ChangeLogEntry> l) { S.Events.Add("log"); S.Logs+=l.Count(); } }
 public class NamerWindow { public static NamerWindow? Instance; }
 public class ElementPickerWindow(string h,List<Element> c,Func<Element,string> display) {
  public string Title=""; public Element? Result=>S.Link; public bool? ShowDialog()=>true;
 }
}
namespace Autodesk.Revit.UI {
 public enum Result {Succeeded,Failed,Cancelled}
 public enum TaskDialogResult {Cancel,Yes,No,CommandLink1,CommandLink2}
 [Flags] public enum TaskDialogCommonButtons {Cancel=1,Yes=2,No=4}
 public enum TaskDialogCommandLinkId {CommandLink1,CommandLink2}
 public class UIApplication { public UIDocument ActiveUIDocument=new(); public App Application=new(); public IntPtr MainWindowHandle=>IntPtr.Zero; }
 public class UIDocument { public Document Document=S.Host; }
 public class App { public IEnumerable<Document> Documents=>S.OpenDocs; public Document OpenDocumentFile(ModelPath p,OpenOptions o){S.Hit("open");return S.Local;} }
 public class TaskDialog(string t) {
  public string MainInstruction="",MainContent=""; public TaskDialogCommonButtons CommonButtons;public TaskDialogResult DefaultButton;
  public void AddCommandLink(TaskDialogCommandLinkId a,string b){}
  public TaskDialogResult Show(){ if((CommonButtons & TaskDialogCommonButtons.Yes)!=0 && DefaultButton!=TaskDialogResult.No)throw new Exception("확인 기본값은 아니요");return S.Answers.Count>0?S.Answers.Dequeue():TaskDialogResult.Yes;}
  public static void Show(string t,string s){S.Messages.Add(s);}
 }
}
namespace Autodesk.Revit.DB {
 public class ElementId {}
 public class Element {public string Name="링크";public bool IsValidObject=true;public ElementId Id=new();}
 public class ModelPath {public string Value="";}
 public class FilePath:ModelPath {}
 public static class ModelPathUtils {public static string ConvertModelPathToUserVisiblePath(ModelPath p)=>p.Value;public static ModelPath ConvertUserVisiblePathToModelPath(string p)=>new FilePath{Value=p};}
 public class ExternalFileReference {public ModelPath GetAbsolutePath()=>S.Cloud?new ModelPath{Value="cloud"}:new FilePath{Value=S.Central};}
 public class RevitLinkType:Element {
  public bool IsNestedLink,LocallyUnloaded;
  public ExternalFileReference GetExternalFileReference()=>new();
  public static bool IsLoaded(Document h,ElementId id)=>S.Loaded;
  public bool IsNotLoadedIntoMultipleOpenDocuments()=>!S.Multiple;
  public void UnloadLocally(object? cb){if(cb!=null)throw new Exception("좌표 저장 금지");S.Loaded=false;LocallyUnloaded=true;S.Hit("unload-local");}
  public void Unload(object? cb){S.Loaded=false;S.Hit("unload-global");}
  public void RevertLocalUnloadStatus(){S.Hit("restore-local");S.Loaded=true;LocallyUnloaded=false;}
  public void Load(){S.Hit("restore-global");S.Loaded=true;}
 }
 public class FilteredElementCollector(Document d):IEnumerable<Element>{
  public FilteredElementCollector OfClass(Type t)=>this;
  public IEnumerator<Element> GetEnumerator()=>S.Links.GetEnumerator();IEnumerator IEnumerable.GetEnumerator()=>GetEnumerator();
 }
 public class Document {
  public string PathName="",Title="파일";public bool IsReadOnly,IsModifiable,IsFamilyDocument,IsLinked,IsWorkshared=true,IsValidObject=true;
  public ModelPath GetWorksharingCentralModelPath()=>new FilePath{Value=this==S.Host?S.HostCentral:S.LocalCentral};
  public void Save(){S.Hit("save");S.Saved=true;}
  public void SynchronizeWithCentral(TransactWithCentralOptions t,SynchronizeWithCentralOptions o){if(t.Callback?.ShouldWaitForLockAvailability()!=false)throw new Exception("잠금 무한 대기");S.Hit("sync");S.Synced=true;}
  public bool Close(bool save){if(save)throw new Exception("암묵 저장 금지");S.Hit("close");IsValidObject=false;return true;}
 }
 public class Disposable:IDisposable {public virtual void Dispose(){}}
 public class BasicFileInfo:Disposable {
  public bool IsWorkshared=true,IsCentral=true,IsLocal,IsSavedInCurrentVersion=true;
  public static BasicFileInfo Extract(string p)=>p==S.Host.PathName?new(){IsLocal=!S.HostIsCentral}:new(){IsCentral=!S.NotCentral,IsSavedInCurrentVersion=!S.OldVersion};
 }
 public class WorksharingUtils {
  public static void CreateNewLocal(ModelPath source,ModelPath target){S.Hit("copy");S.Local.PathName=target.Value;File.Copy(source.Value,target.Value);}
  public static void RelinquishOwnership(Document d,RelinquishOptions r,TransactWithCentralOptions t){S.Hit("relinquish");}
 }
 public class OpenOptions:Disposable {public DetachFromCentralOption DetachFromCentralOption;public void SetOpenWorksetsConfiguration(WorksetConfiguration w){}}
 public enum DetachFromCentralOption {DoNotDetach}
 public enum WorksetConfigurationOption {OpenAllWorksets}
 public class WorksetConfiguration(WorksetConfigurationOption o):Disposable {}
 public enum TransactionStatus {Started,Committed,RolledBack}
 public class TransactionGroup(Document d,string n):Disposable {
  bool started;
  public TransactionStatus Start(){started=true;S.Hit("group-start");return TransactionStatus.Started;}
  public TransactionStatus Assimilate(){S.Hit("group-commit");started=false;return TransactionStatus.Committed;}
  public void RollBack(){S.Events.Add("rollback");S.Changed=false;started=false;}
  public override void Dispose(){if(started)RollBack();}
 }
 public interface ICentralLockedCallback {bool ShouldWaitForLockAvailability();}
 public class TransactWithCentralOptions:Disposable {public ICentralLockedCallback? Callback;public void SetLockCallback(ICentralLockedCallback c)=>Callback=c;}
 public class SynchronizeWithCentralOptions:Disposable {public bool SaveLocalBefore,SaveLocalAfter;public string Comment="";public void SetRelinquishOptions(RelinquishOptions o){}}
 public class RelinquishOptions(bool all):Disposable {}
}
static class S {
 public static string Central="",HostCentral="",LocalCentral="",Fail="";
 public static Document Host=null!,Local=null!;public static RevitLinkType Link=null!;
 public static bool Cloud,Multiple,Loaded,NotCentral,OldVersion,HostIsCentral,Saved,Synced,Changed;
 public static int Logs;public static List<string> Events=new(),Messages=new();public static List<Document> OpenDocs=new();public static List<Element> Links=new();
 public static Queue<TaskDialogResult> Answers=new();
 public static void Hit(string step){Events.Add(step);if(Fail==step)throw new Exception("가상 실패: "+step);}
 public static void Reset(){
  Directory.CreateDirectory("fixtures");
  Central=Path.GetFullPath("fixtures/central.rvt");File.WriteAllText(Central,"테스트용 가짜 중앙 파일");
  HostCentral=Path.GetFullPath("fixtures/host-central.rvt");LocalCentral=Central;Fail="";
  Host=new(){PathName=Path.GetFullPath("fixtures/host.rvt")};Local=new();Link=new();
  Cloud=Multiple=NotCentral=OldVersion=HostIsCentral=Saved=Synced=Changed=false;Loaded=true;Logs=0;Events.Clear();Messages.Clear();Answers.Clear();
  OpenDocs=new(){Host};Links=new(){Link};WallSplitter.NamerWindow.Instance=null;
 }
}

