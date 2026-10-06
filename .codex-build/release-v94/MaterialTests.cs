using System.Collections;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Autodesk.Revit.DB;
using WallSplitter;

static class MaterialTests
{
 static readonly BindingFlags F=BindingFlags.Instance|BindingFlags.Static|BindingFlags.NonPublic|BindingFlags.Public;
 static object Get(object o,string n)=>o.GetType().GetField(n,F)!.GetValue(o)!;
 static T Get<T>(object o,string n)=>(T)Get(o,n);
 static object? Call(object o,string n,params object?[] args)=>o.GetType().GetMethods(F).Single(m=>m.Name==n&&m.GetParameters().Length==args.Length).Invoke(o,args);
 static void Check(bool ok,string name){if(!ok)throw new Exception(name);Console.WriteLine("PASS MATERIAL "+name);}
 static Parameter P(int id,ElementId value,string name="마감 재료")=>new(){Id=new(id),Value=value,Definition=new(){Name=name}};
 static List<object> Candidates(object w,string field="_allCandidates")=>Get<IList>(w,field).Cast<object>().ToList();
 static void Click(object w,string method)=>Call(w,method,w,new RoutedEventArgs());
 static string Current(object w,object c)=>(string)Call(w,"CurrentMaterialName",c)!;
 static void Filter(object w,string term,int mode=0,int target=1){
  Get<ComboBox>(w,"FilterTargetCombo").SelectedIndex=target;
  Get<ComboBox>(w,"FilterModeCombo").SelectedIndex=mode;Get<TextBox>(w,"FilterBox").Text=term;
 }
 static void Choose(object w,ElementId id){
  var combo=Get<ComboBox>(w,"MaterialCombo");
  combo.SelectedItem=combo.Items.Cast<object>().Single(o=>((Material)o.GetType().GetProperty("Material")!.GetValue(o)!).Id==id);
 }
 public static void Run(){
  var d=new Document();var a=new Material{Id=new(300),Name="목재"};var b=new Material{Id=new(400),Name="금속"};var c=new Material{Id=new(500),Name="유리"};
  var t=new FamilySymbol{Id=new(1),Name="900",FamilyName="수납장"};t.Parameters.Add(P(101,a.Id));
  var other=new FamilySymbol{Id=new(2),Name="900",FamilyName="문"};
  var unplaced=new FamilySymbol{Id=new(3),Name="미배치"};
  var host=new HostObjAttributes{Id=new(4),Name="벽",Structure=new(){Layers=new(){new(.2,0,a.Id),new(0,0,b.Id)}}};
  var i1=new FamilyInstance{Id=new(10),Symbol=t};i1.Parameters.AddRange(new[]{P(101,a.Id),P(102,b.Id,"손잡이"),P(103,ElementId.InvalidElementId,"미지정")});
  var i2=new FamilyInstance{Id=new(11),Symbol=t};i2.Parameters.AddRange(new[]{P(101,b.Id),P(102,b.Id,"손잡이"),P(103,ElementId.InvalidElementId,"미지정")});
  var i3=new FamilyInstance{Id=new(12),Symbol=other};i3.Parameters.Add(P(101,c.Id));
  i1.Parameters.Add(new Parameter{Id=new(104),Value=a.Id,Definition=new(){DataType=new ForgeTypeId("not-material")}});
  d.Elements.AddRange(new Element[]{a,b,c,t,other,unplaced,host,i1,i2,i3});
  var w=new MaterialAssignWindow(d,new());
  var all=Candidates(w);
  var mixed=all.Single(x=>Get<ElementType>(x,"Type").Id==t.Id && Get<MaterialSlot>(x,"Slot").Kind==MaterialSlotKind.InstanceParameter && Get<MaterialSlot>(x,"Slot").ParameterId==new ElementId(101));
  var uniform=all.Single(x=>Get<MaterialSlot>(x,"Slot").ParameterId==new ElementId(102));
  Check(all.Count==6,"유형 슬롯·인스턴스 그룹·복합 레이어 수집");
  Check(Current(w,mixed)=="<다양함>" && Current(w,uniform)=="금속","다양함과 공통 재료 표시");
  Capture(w,"material-initial.png",780,620);
  Check(Get<Dictionary<ElementId,ElementId>>(mixed,"InstanceMaterials").Count==2,"유형 이름이 같아도 다른 유형 ID는 분리");
  Check(MaterialSlotFinder.FindAll(t).Single().Kind==MaterialSlotKind.Parameter && MaterialSlotFinder.FindAll(i1).Count==3,"유형/인스턴스 슬롯 분리 및 비재료 제외");
  Check(MaterialSlotFinder.FindEligibleTypes(d).Count==2,"기존 모델간 유형 후보 계약 유지");
  Check(!((string)Call(w,"MaterialDisplayName",c)!).Contains("미사용"),"인스턴스 전용 재료도 사용 중으로 표시");
  Filter(w,"금속");
  Check(Candidates(w,"_filteredCandidates").Count==2 && Candidates(w,"_filteredCandidates").Contains(mixed),"다양함 내부 재료로 검색");
  foreach(int mode in new[]{1,3}){Filter(w,"금속",mode);Check(!Candidates(w,"_filteredCandidates").Contains(mixed),"부정 검색은 일치 재료가 없는 그룹만 "+mode);}
  foreach(int mode in new[]{0,2,4,5}){Filter(w,"금속",mode);Check(Candidates(w,"_filteredCandidates").Contains(mixed),"재료 필터 방식 "+mode);}
  Filter(w,"",0,2);Check(Candidates(w,"_filteredCandidates").Count==1,"재료 없음 그룹 검색");
  Filter(w,"수납장",0,0);Check(Candidates(w,"_filteredCandidates").Count==4,"패밀리 이름 검색");
  Filter(w,"금속");Click(w,"SelectAllButton_Click");Choose(w,c.Id);Click(w,"ApplyButton_Click");
  Check(Current(w,mixed)=="유리" && Current(w,uniform)=="유리","적용 미리보기는 그룹 전체 단일 재료");
  Check(i1.Parameters[0].Value==a.Id && i2.Parameters[0].Value==b.Id,"창 안 적용은 모델 미변경");
  Check(Get<IDictionary>(w,"_workingMaterialIds").Count==2,"매개변수별 편집값 유지");
  Filter(w,"금속");Check(Candidates(w,"_filteredCandidates").Count==0,"작업 중 재료 기준 재검색");
  Filter(w,"유리");Check(Candidates(w,"_filteredCandidates").Count==3,"유형 이름이 같은 다른 그룹과 작업값 분리");
  var mixedKey=Call(w,"KeyOf",mixed);
  var row=Get<IList>(w,"_rows").Cast<object>().Single(r=>Get(r,"Key").Equals(mixedKey));
  Get<CheckBox>(row,"CheckBox").IsChecked=true;Choose(w,a.Id);Click(w,"ApplyButton_Click");
  Check(Current(w,mixed)=="목재" && Get<TextBlock>(w,"PendingChangesText").Text.Contains("2개"),"다양함의 첫 재료로 바꿔도 변경 사항 유지");
  object?[] output={null,null};Call(w,"BuildAssignmentResults",output);
  Check(((IList)output[0]!).Count==0 && ((List<MaterialInstanceAssignment>)output[1]!).Count==2,"최종 결과에서 유형과 인스턴스 대상을 분리");
  Check(((List<MaterialInstanceAssignment>)output[1]!).All(x=>x.InstanceIds.Count==2),"재료 검색과 관계없이 유형 전체 ID 전달");
  Filter(w,"수납장",0,0);
  var typeRow=Get<IList>(w,"_rows").Cast<object>().Single(r=>Get(r,"Key").Equals(Call(w,"KeyOf",all.Single(x=>Get<ElementType>(x,"Type").Id==t.Id && Get<MaterialSlot>(x,"Slot").Kind==MaterialSlotKind.Parameter))));
  Get<CheckBox>(typeRow,"CheckBox").IsChecked=true;Choose(w,c.Id);Click(w,"ApplyButton_Click");
  Call(w,"BuildAssignmentResults",output);
  Check(((IList)output[0]!).Count==1 && ((List<MaterialInstanceAssignment>)output[1]!).Count==2,"유형·인스턴스 동시 편집 결과 보존");
  Filter(w,"유리");
  var uniformRow=Get<IList>(w,"_rows").Cast<object>().Single(r=>Get(r,"Key").Equals(Call(w,"KeyOf",uniform)));
  Get<CheckBox>(uniformRow,"CheckBox").IsChecked=true;Choose(w,b.Id);Click(w,"ApplyButton_Click");
  Call(w,"BuildAssignmentResults",output);
  Check(((List<MaterialInstanceAssignment>)output[1]!).Count==1,"원래 공통 재료로 되돌린 그룹은 최종 변경에서 제외");

  var content=(System.Windows.Controls.Grid)w.Content;content.Background=(Brush)w.FindResource("WindowBackgroundBrush");
  Filter(w,"",0,0);content.Measure(new Size(950,720));content.Arrange(new Rect(0,0,950,720));content.UpdateLayout();Call(w,"MaterialAssignWindow_Loaded",w,new RoutedEventArgs());content.UpdateLayout();
  var bitmap=new RenderTargetBitmap(950,720,96,96,PixelFormats.Pbgra32);bitmap.Render(content);
  var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bitmap));using(var file=System.IO.File.Create("material-groups.png"))png.Save(file);

  var slot=MaterialSlotFinder.FindAll(i1).First();
  var assignment=new MaterialInstanceAssignment(t.Id,slot,new(){i1.Id,i2.Id},c.Id);
  Check(assignment.TryApply(d,out var old,out var reason) && i1.Parameters[0].Value==c.Id && i2.Parameters[0].Value==c.Id,"인스턴스 그룹 전체 적용·재확인");
  Check(t.Parameters[0].Value==a.Id && i3.Parameters[0].Value==c.Id && i1.Parameters[1].Value==b.Id,"다른 유형·유형 슬롯·다른 매개변수는 불변");
  i1.Parameters[0].Value=a.Id;i2.Parameters[0].Value=b.Id;i2.Parameters[0].IsReadOnly=true;
  Check(!assignment.TryApply(d,out _,out reason) && i1.Parameters[0].Value==a.Id && i2.Parameters[0].Value==b.Id,"읽기 전용이면 묶음 전체 미변경");
  i2.Parameters[0].IsReadOnly=false;i2.Parameters[0].FailSet=true;
  Check(!assignment.TryApply(d,out _,out reason) && i1.Parameters[0].Value==a.Id,"두 번째 쓰기 실패 시 첫 번째도 롤백");
  i2.Parameters[0].FailSet=false;i2.Parameters[0].IgnoreSet=true;
  Check(!assignment.TryApply(d,out _,out reason) && i1.Parameters[0].Value==a.Id,"조용한 쓰기 무시도 재조회 후 전체 롤백");
  i2.Parameters[0].IgnoreSet=false;i2.Symbol=other;
  Check(!assignment.TryApply(d,out _,out reason) && i1.Parameters[0].Value==a.Id,"유형 변경 감지");
  i2.Symbol=t;d.Elements.Remove(i2);
  Check(!assignment.TryApply(d,out _,out reason),"삭제된 인스턴스 감지");
  d.Elements.Add(i2);
  var wrongSlot=new MaterialInstanceAssignment(t.Id,MaterialSlotFinder.FindAll(t).First(),new(){i1.Id},c.Id);
  Check(!wrongSlot.TryApply(d,out _,out reason),"유형 슬롯을 인스턴스로 오인하지 않음");
  w.Close();Console.WriteLine("ALL MATERIAL GROUP TESTS PASSED");
 }
 static void Capture(MaterialAssignWindow w,string name,int width,int height){
  var content=(System.Windows.Controls.Grid)w.Content;content.Background=(Brush)w.FindResource("WindowBackgroundBrush");
  content.Measure(new Size(width,height));content.Arrange(new Rect(0,0,width,height));content.UpdateLayout();
  Call(w,"MaterialAssignWindow_Loaded",w,new RoutedEventArgs());content.UpdateLayout();
  var bitmap=new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32);bitmap.Render(content);
  var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bitmap));using(var file=System.IO.File.Create(name))png.Save(file);
 }
}

