using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using Autodesk.Revit.DB;
using WallSplitter;

static class Program
{
    static void Check(bool ok,string why){if(!ok)throw new Exception(why);}
    [STAThread] static void Main()
    {
        var id=new ElementId(1); var two=new ElementId(2); int confirmations=0;
        var doc=new Document();
        var cancelled=WarningPickDeleteService.Delete(doc,new[]{id,id},p=>{
            confirmations++; Check(p.TargetCount==1&&p.DependentCount==1&&p.TotalCount==2,"중복/의존 집계");
            Check(doc.Elements.Count==4&&!doc.IsModifiable,"확인 전 롤백");return false;
        });
        Check(confirmations==1&&doc.Elements.Count==4&&doc.Commits==0&&cancelled.Contains("취소"),"취소 보존");
        var done=WarningPickDeleteService.Delete(doc,new[]{id,two,id},p=>true);
        Check(doc.Elements.SetEquals(new[]{new ElementId(4)})&&doc.Commits==1&&done.Contains("삭제 완료"),"동의 후 일괄 삭제");
        foreach(var mode in new[]{"empty","missing","resolved","readonly","busy","scope","failure","commit"})
        {
            doc=new Document(); confirmations=0;var ids=new[]{id};
            if(mode=="empty")ids=Array.Empty<ElementId>();
            if(mode=="missing")doc.Elements.Remove(id);
            if(mode=="resolved")doc.Warning.Ids.Clear();
            if(mode=="readonly")doc.IsReadOnly=true;
            if(mode=="busy")doc.IsModifiable=true;
            if(mode=="scope")doc.ChangeScope=true;
            if(mode=="failure")doc.FailDelete=true;
            if(mode=="commit")doc.RejectCommit=true;
            var before=doc.Elements.ToHashSet();
            try { WarningPickDeleteService.Delete(doc,ids,p=>{confirmations++;return true;}); }
            catch when(mode=="failure"){}
            Check(doc.Elements.SetEquals(before)&&doc.Commits==0,"안전 중단 실패: "+mode);
            if(mode!="scope"&&mode!="commit")Check(confirmations==0,"확인 불필요 경로: "+mode);
        }
        Console.WriteLine("PASS: 취소/확인/중복/의존 요소/빈 목록/사라진 요소/해결된 경고/읽기전용/진행중 트랜잭션/범위 변경/실패/커밋 롤백");

        // 실제 XAML과 테마를 읽어 이벤트 연결만 제거한 독립 화면을 렌더링한다.
        var root=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../../"));
        var xml=XDocument.Load(Path.Combine(root,"WallSplitter/WarningPickWindow.xaml"));
        XNamespace x="http://schemas.microsoft.com/winfx/2006/xaml";
        xml.Root!.Attribute(x+"Class")!.Remove();
        foreach(var el in xml.Descendants())
        foreach(var a in el.Attributes().Where(a=>new[]{"Click","TextChanged","MouseMove","MouseLeftButtonUp","LostMouseCapture"}.Contains(a.Name.LocalName)).ToList())a.Remove();
        xml.Descendants().First(e=>e.Name.LocalName=="ResourceDictionary").SetAttributeValue("Source",new Uri(Path.Combine(root,"WallSplitter/Resources/Theme.xaml")).AbsoluteUri);
        var app=new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};
        var window=(Window)XamlReader.Parse(xml.ToString());
        var panel=(StackPanel)window.FindName("GroupsPanel");
        panel.Children.Add(new TextBlock{Text="경고 목록 — 삭제 대상은 체크박스로 선택합니다.",Margin=new Thickness(8)});
        ((TextBlock)window.FindName("DocumentText")).Text="문서: UI 검증용 (실제 모델 아님)";
        var output=Path.Combine(root,".codex-build/release-v92");
        var surface=(FrameworkElement)window.Content;
        ((System.Windows.Controls.Grid)surface).Background=(Brush)window.FindResource("WindowBackgroundBrush");
        foreach(var size in new[]{(820,700),(680,560)})
        {
            window.Width=size.Item1;window.Height=size.Item2;
            surface.Measure(new Size(size.Item1,size.Item2));surface.Arrange(new Rect(0,0,size.Item1,size.Item2));surface.UpdateLayout();
            var a=(Button)window.FindName("DeleteCheckedButton");var b=(Button)window.FindName("DeleteAllButton");
            Check(a.ActualWidth>100&&b.ActualWidth>50,"삭제 버튼 크기");
            Check(((SolidColorBrush)a.Background).Color==Color.FromRgb(166,89,93),"빨강 스타일");
            Check(a.TranslatePoint(new Point(),surface).Y>panel.TranslatePoint(new Point(),surface).Y,"하단 배치");
            var bmp=new RenderTargetBitmap(size.Item1,size.Item2,96,96,PixelFormats.Pbgra32);bmp.Render(surface);
            var enc=new PngBitmapEncoder();enc.Frames.Add(BitmapFrame.Create(bmp));
            using var stream=File.Create(Path.Combine(output,$"warning-pick-{size.Item1}.png"));enc.Save(stream);
        }
        Console.WriteLine("PASS: 기본/최소 크기 하단 빨간 삭제 버튼 렌더");
        app.Shutdown();
    }
}
