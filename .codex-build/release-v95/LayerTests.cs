using Autodesk.Revit.DB;
using WallSplitter;

static class LayerTests
{
 static void Check(bool ok,string name){if(!ok)throw new Exception(name);Console.WriteLine("PASS LAYER "+name);}
 public static void Run()
 {
  var a=new ElementId(300);var b=new ElementId(400);
  CompoundStructure Make()=>new(){Layers=new(){new(.03,2,a){LayerId=41,Priority=8,Wraps=false},new(.2,1,a){LayerId=97,Priority=3},new(.04,4,a){LayerId=106,Priority=6}}};
  var original=Make();var host=new HostObjAttributes{Id=new(1),Name="구조+가변 유형",Structure=original};
  var before=original.Layers.Select(l=>(l.Width,l.Function,l.Priority,l.LayerId,l.Wraps,l.Deck)).ToArray();
  var slot=MaterialSlotFinder.FindAll(host)[1];
  Check(MaterialSlotFinder.Apply(host,slot,b,out var previous),"구조 레이어 재료 변경 성공");
  Check(previous?.MaterialId==a && original.Layers[1].MaterialId==b,"이전 재료와 새 재료 확인");
  Check(original.StructuralMaterialIndex==1,"구조 재료 지정 유지");
  Check(original.VariableLayerIndex==2,"가변 레이어 지정 유지");
  Check(original.Layers.Select(l=>(l.Width,l.Function,l.Priority,l.LayerId,l.Wraps,l.Deck)).SequenceEqual(before),"두께·기능·우선순위·레이어 ID·감싸기·데크 유지");
  Check(original.ExteriorShell==1 && original.InteriorShell==1,"코어 경계 유지");
  Check(original.VerticallyCompound && original.Regions=="원래 수직 구역","수직 복합 구역 유지");
  Check(original.Layers[0].MaterialId==a && original.Layers[2].MaterialId==a,"다른 레이어 재료 유지");
  Check(original.Resets==0,"전체 레이어 재설정 미호출");
  Check(MaterialSlotFinder.Apply(host,MaterialSlotFinder.FindAll(host)[2],b,out _) && original.VariableLayerIndex==2 && original.StructuralMaterialIndex==1,"가변 레이어 재료 연속 변경도 설정 유지");
  var missing=new MaterialSlot(MaterialSlotKind.CompoundLayer,99,a,"없는 레이어");
  Check(!MaterialSlotFinder.Apply(host,missing,b,out _) && original.Resets==0,"잘못된 레이어 인덱스는 미변경");
  var legacy=Make();legacy.SetLayers(legacy.GetLayers());
  Check(legacy.StructuralMaterialIndex==-1 && legacy.VariableLayerIndex==-1,"구 방식의 초기화 부작용을 잡는 대조군");
  Console.WriteLine("ALL LAYER PRESERVATION TESTS PASSED");
 }
}
