using System.Windows.Controls;
using WallSplitter;
partial class Program {
 static void SortingRegression(){
  var host=new UnifiedSource{Key="host",Name="[현재] 호스트",IsHost=true};
  var link=new UnifiedSource{Key="link",Name="[링크] 건축"};
  UnifiedRow Add(UnifiedSource s,string id,string name,int category=0,string family=""){
   var r=new UnifiedRow{Source=s,UniqueId=id,Original=name,Working=name,Category=category,Family=family};s.Rows.Add(r);return r;
  }
  var h=Add(host,"1","Z 이름");var l=Add(link,"1","A 이름");
  var w=new UnifiedEditWindow(new(){link,host},false);Field<ComboBox>(w,"_category").SelectedIndex=0;
  List<UnifiedRow> Rows()=>Field<List<UnifiedRow>>(w,"_display");
  var sort=Field<ComboBox>(w,"_sort");
  Check(sort.SelectedIndex==0&&Rows().SequenceEqual(new[]{h,l}),"기본 모델 정렬: 현재 모델 먼저");
  h.Selected=true;h.Working="Y 누적 이름";Field<TextBox>(w,"_first").Text="누적";Field<TextBox>(w,"_second").Text="예시";
  typeof(UnifiedEditWindow).GetField("_limit",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.SetValue(w,600);
  sort.SelectedIndex=1;
  Check(Rows().SequenceEqual(new[]{l,h}),"이름 정렬: 소속 모델보다 이름 우선");
  Check(h.Selected&&h.Working=="Y 누적 이름"&&h.Changed&&Field<TextBox>(w,"_first").Text=="누적","정렬 전환은 체크·누적 편집·미리보기 입력 보존");
  Check(Field<int>(w,"_limit")==600,"정렬 변경 시 펼친 페이지 분량 유지");
  Check(Field<UnifiedRow?>(w,"_anchor")==null,"정렬 뒤 Shift 기준 초기화");
  sort.SelectedIndex=0;Check(Rows().SequenceEqual(new[]{h,l}),"모델 정렬 복귀");
  sort.SelectedIndex=1;Field<TextBox>(w,"_search").Text="이름";Check(Rows().SequenceEqual(new[]{l,h}),"검색 후에도 이름 정렬 유지");
  Field<ComboBox>(w,"_model").SelectedItem=host;Check(Rows().Count==1&&Rows()[0]==h,"정렬과 모델 필터 함께 사용");
  Field<ComboBox>(w,"_model").SelectedIndex=0;Field<TextBox>(w,"_search").Clear();
  var hp=Add(host,"p","Z 패밀리",2);hp.IsFamily=true;hp.Expanded=true;
  var lp=Add(link,"p","A 패밀리",2);lp.IsFamily=true;lp.Expanded=true;
  var hc=Add(host,"c","A 하위",3);hc.ParentId="p";hc.Selected=true;
  var lc=Add(link,"c","Z 하위",3);lc.ParentId="p";
  Field<ComboBox>(w,"_category").SelectedIndex=2;
  Check(Rows().SequenceEqual(new[]{lp,lc,hp,hc}),"이름 정렬에도 패밀리 자식은 각 모델의 부모 아래 유지");
  sort.SelectedIndex=0;Check(Rows().SequenceEqual(new[]{hp,hc,lp,lc})&&hp.Expanded&&hc.Selected,"모델 정렬 복귀에도 계층·펼침·하위 체크 유지");
  Capture(w,"sort-namer-model.png");sort.SelectedIndex=1;Capture(w,"sort-namer-name-min.png",1040,620);w.Close();
  host.Rows.Clear();link.Rows.Clear();host.Materials["m"]="철재";link.Materials["m"]="콘크리트";
  h=Add(host,"1","Z 유형",3,"가 패밀리");l=Add(link,"1","A 유형",3,"나 패밀리");
  foreach(var r in new[]{h,l}){r.IsMaterial=true;r.Original="기존 재료";r.MaterialNames.Add(r.Original);r.OriginalMaterials["instance"]="old";}
  w=new UnifiedEditWindow(new(){link,host},true);sort=Field<ComboBox>(w,"_sort");h.Selected=true;h.NewMaterial="m";
  Field<Dictionary<string,string>>(w,"_choices")[host.Key]="m";sort.SelectedIndex=1;
  Check(Rows().SequenceEqual(new[]{l,h}),"재료 지정 이름 기준은 재료명/패밀리보다 대상 유형명 우선");
  Check(h.Selected&&h.NewMaterial=="m"&&h.Value=="철재"&&Field<Dictionary<string,string>>(w,"_choices")[host.Key]=="m","재료 정렬 변경은 지정 재료와 모델별 선택 보존");
  sort.SelectedIndex=0;Check(Rows().SequenceEqual(new[]{h,l}),"재료 모델 기준 복귀");
  Capture(w,"sort-material-model.png");sort.SelectedIndex=1;Capture(w,"sort-material-name-min.png",1040,620);
  Field<ComboBox>(w,"_category").SelectedIndex=1;Field<TextBox>(w,"_search").Text="철재";
  Check(Rows().Count==1&&Rows()[0]==h,"재료 검색과 정렬 독립 유지");w.Close();
 }
}
