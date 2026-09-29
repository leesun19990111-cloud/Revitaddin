// 삭제 서비스의 순서·롤백을 확인하기 위한 대역. 실제 Revit 모델 검증을 대신하지 않는다.
namespace Autodesk.Revit.DB;
public sealed record ElementId(int Value) { public override string ToString()=>Value.ToString(); }
public class Category { public string Name=>"테스트"; }
public class Element { public string Name=>"요소"; public Category Category=>new(); }
public class FailureMessage {
 public List<ElementId> Ids=new(); public IEnumerable<ElementId> GetFailingElements()=>Ids;
 public IEnumerable<ElementId> GetAdditionalElements()=>Ids;
}
public class Document {
 public bool IsReadOnly {get;set;} public bool IsModifiable {get;set;}
 public HashSet<ElementId> Elements=new(){new(1),new(2),new(3),new(4)};
 public FailureMessage Warning=new(){Ids=new(){new(1),new(2)}};
 public int Calls, Commits; public bool FailDelete, ChangeScope, RejectCommit;
 public IEnumerable<FailureMessage> GetWarnings()=>new[]{Warning};
 public Element? GetElement(ElementId id)=>Elements.Contains(id)?new():null;
 public ICollection<ElementId> Delete(IEnumerable<ElementId> ids){
   if(!IsModifiable)throw new Exception("트랜잭션 없음");
   Calls++; var deleted=ids.ToHashSet();
   if(deleted.Contains(new(1)))deleted.Add(new(3));
   if(ChangeScope&&Calls>1)deleted.Add(new(4));
   Elements.ExceptWith(deleted);
   if(FailDelete)throw new Exception("삭제 실패 모사");
   return deleted;
 }
}
public enum TransactionStatus{Uninitialized,Started,Committed,RolledBack}
public class FailureHandlingOptions { public FailureHandlingOptions SetForcedModalHandling(bool value)=>this; }
public class Transaction:IDisposable{
 readonly Document doc; HashSet<ElementId>? saved; TransactionStatus state;
 public Transaction(Document d,string name){doc=d;}
 public TransactionStatus Start(){saved=doc.Elements.ToHashSet();doc.IsModifiable=true;return state=TransactionStatus.Started;}
 public TransactionStatus GetStatus()=>state;
 public TransactionStatus RollBack(){doc.Elements=saved!;doc.IsModifiable=false;return state=TransactionStatus.RolledBack;}
 public TransactionStatus Commit(){if(doc.RejectCommit)return RollBack();doc.Commits++;doc.IsModifiable=false;return state=TransactionStatus.Committed;}
 public FailureHandlingOptions GetFailureHandlingOptions()=>new();
 public void SetFailureHandlingOptions(FailureHandlingOptions options){}
 public void Dispose(){if(state==TransactionStatus.Started)RollBack();}
}
