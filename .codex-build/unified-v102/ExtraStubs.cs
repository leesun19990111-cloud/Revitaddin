using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
namespace Autodesk.Revit.DB {
 public class ModelPath {public string Value="";} public class FilePath:ModelPath{}
 public static class ModelPathUtils {public static string ConvertModelPathToUserVisiblePath(ModelPath p)=>p.Value;}
 public class ExternalFileReference(string p) {public ModelPath GetAbsolutePath()=>new ModelPath{Value=p};}
 public class RevitLinkType:Element {public bool IsNestedLink;public string Path="";public ExternalFileReference GetExternalFileReference()=>new(Path);}
 public class RevitLinkInstance:Element {public ElementId Type=new(-1);public Document? Link;public override ElementId GetTypeId()=>Type;public Document? GetLinkDocument()=>Link;}
 public class BasicFileInfo:IDisposable {
  public bool IsCentral=true,IsWorkshared=true,IsSavedInCurrentVersion=true;
  public static BasicFileInfo Extract(string p)=>new();public void Dispose(){}
 }
 public class TransactionGroup(Document d,string title):IDisposable {
  Dictionary<Element,string> before=new();bool started;
  public TransactionStatus Start(){before=d.Elements.ToDictionary(e=>e,e=>e.Name);started=true;return TransactionStatus.Started;}
  public TransactionStatus Assimilate(){started=false;return TransactionStatus.Committed;}
  public void Dispose(){if(started)foreach(var p in before)p.Key.Name=p.Value;}
 }
}
namespace Autodesk.Revit.UI {
 public enum Result {Succeeded,Failed,Cancelled}
 public class UIApplication {public UIDocument ActiveUIDocument=new();public IntPtr MainWindowHandle=>IntPtr.Zero;}
 public class UIDocument {public Document Document=null!;}
 public static class TaskDialog {public static void Show(string a,string b){TestState.Report=b;}}
}
namespace WallSplitter {
 internal enum ChangeKind { ProjectInformation, ScheduleParameter }
 internal class ChangeLogEntry { public DateTime Timestamp;public string SourceDocumentTitle="",Key="",OldValue="",NewValue="";public ChangeKind Kind;public NamerWindow.NamerCategory Category; }
 internal static class ChangeLog {internal static void Append(IEnumerable<ChangeLogEntry> entries){TestState.LogCount+=entries.Count();}}
 internal class LinkedEditPlan {internal string Summary="";internal Func<Document,List<ChangeLogEntry>,Result> Apply=null!;}
 internal class LinkedEditReport {internal bool Synced,HasProblems,RetainChanges;internal List<string> Messages=new();}
 internal static class LinkedModelEditService {
  internal static Document? FindOpenTarget(UIApplication app,string path)=>TestState.OpenTargets.TryGetValue(path,out var doc)?doc:null;
  internal static bool IsFileBasedPath(ModelPath? p)=>p!=null&&p.Value.Length>0;
  internal static string CanonicalPath(string s)=>s.ToLowerInvariant();
  internal static bool SamePath(string a,string b)=>CanonicalPath(a)==CanonicalPath(b);
  internal static bool Confirm(string a,string b,string c){TestState.Confirmations++;return TestState.Approve;}
  internal static Result Run(UIApplication app,RevitLinkType link,string title,Func<Document,LinkedEditPlan?> prepare,LinkedEditReport report){
   TestState.LinkCalls++;
   if(TestState.FailLink==link.Path){report.Messages.Add("모의 동기화 실패");report.HasProblems=true;return Result.Failed;}
   var doc=((RevitLinkInstance)app.ActiveUIDocument.Document.Elements.First(e=>e is RevitLinkInstance i&&i.Type==link.Id)).Link!;
   var logs=new List<ChangeLogEntry>();var result=prepare(doc)!.Apply(doc,logs);
   report.Synced=result==Result.Succeeded;report.RetainChanges=report.Synced;report.Messages.Add("동기화 완료");return result;
  }
 }
 internal static class MaterialAssignCommand {
  internal static Result ExecuteAssign(Document doc,List<(ElementId TypeId,MaterialSlot Slot,ElementId NewMaterialId)> rows,List<MaterialInstanceAssignment> instances,List<ChangeLogEntry> logs){
   TestState.ApplyCalls++;
   foreach(var r in rows) {if(!MaterialSlotFinder.Apply(doc.GetElement(r.TypeId)!,r.Slot,r.NewMaterialId,out _))return Result.Failed;logs.Add(new());}
   foreach(var r in instances){if(!r.TryApply(doc,out _,out _))return Result.Failed;logs.Add(new());} return Result.Succeeded;
  }
 }
}
static class TestState {
 public static Dictionary<string,Document> OpenTargets=new();
 public static List<string> Events=new();public static string Report="",FailLink="";public static bool Approve=true;
 public static int Confirmations,LinkCalls,LogCount,ApplyCalls;
 public static void Reset(){Events.Clear();Report=FailLink="";Approve=true;Confirmations=LinkCalls=LogCount=ApplyCalls=0;}
}
