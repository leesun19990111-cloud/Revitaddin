using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using WallSplitter;

var options = new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } };
var read = typeof(QuickToggleSettings).GetMethod("ReadFrom", BindingFlags.NonPublic | BindingFlags.Static)!;
const string mixed = """
{"ToolbarVisible":false,"LayoutVersion":1,"UnlinkedCategories":["LevelSectionBox","Filter"],
 "Buttons":[
 {"Id":"keep-a","Name":"필터 A","Category":"Filter","FilterNames":["구조"],"SmallButton":true,"OnColorHex":"#A6595D","IconShape":"SectionBand"},
 {"Id":"remove","Category":"LevelSectionBox","LevelBottomName":"1F","LevelTopName":"2F","LevelBottomElevation":0,"LevelTopElevation":10},
 {"Id":"keep-b","Name":"저장","Category":"CommandLauncher","CommandKind":"NativeRevit","CommandId":"Save","SmallButton":true,"SecondRow":true},
 {"Id":"old-preset","Category":"Preset"},{"Id":"old-search","Category":"GraphicsDisplaySearch"}]}
""";
var fixture = Path.Combine(Path.GetTempPath(), "sunny-v91-" + Guid.NewGuid() + ".json");
void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
try
{
    File.WriteAllText(fixture, mixed);
    var result = (QuickToggleSettings?)read.Invoke(null, new object[] { fixture });
    Check(result != null, "기존 설정을 읽지 못함");
    Check(result!.Buttons.Select(b => b.Id).SequenceEqual(new[] { "keep-a", "keep-b" }), "다른 버튼/순서 손실");
    Check(!result.ToolbarVisible && result.LayoutVersion == 1, "전역 옵션 손실");
    Check(result.UnlinkedCategories.SequenceEqual(new[] { QuickToggleCategory.Filter }), "묶음 상태 손실");
    Check(result.Buttons[0].FilterNames.Single() == "구조" && result.Buttons[0].OnColorHex == "#A6595D"
        && result.Buttons[0].IconShape == QuickToggleIconShape.SectionBand, "대상/색/아이콘 손실");
    Check(result.Buttons[1].CommandId == "Save" && result.Buttons[1].SecondRow, "명령/배치 손실");
    // 버튼모음 가져오기와 같은 역직렬화 및 제외 경로.
    using var doc = JsonDocument.Parse(mixed);
    var imported = JsonSerializer.Deserialize<List<QuickToggleButtonConfig>>(doc.RootElement.GetProperty("Buttons").GetRawText(), options)!;
    imported.RemoveAll(b => QuickToggleSettings.IsRemovedCategory(b.Category));
    Check(imported.Select(b => b.Id).SequenceEqual(new[] { "keep-a", "keep-b" }), "가져오기 호환 실패");
    Check(!JsonSerializer.Serialize(result, options).Contains("LevelSectionBox"), "다시 저장할 데이터에 삭제된 종류 잔존");
    foreach (string category in new[] { "\"LevelSectionBox\"", "10" })
    {
        File.WriteAllText(fixture, "{\"Buttons\":[{\"Category\":" + category + "}]}");
        var empty = (QuickToggleSettings?)read.Invoke(null, new object[] { fixture });
        Check(empty != null && empty.Buttons.Count == 0, "삭제된 버튼만 있는 설정 실패");
    }
    Check((int)QuickToggleCategory.LevelSectionBox == 10, "기존 열거 값 순서 변경");
    Check(!QuickToggleButtonStyle.IsActionButton(QuickToggleCategory.LevelSectionBox), "삭제된 기능 실행형 노출");
    Console.WriteLine("PASS: 기존 설정/버튼모음, 문자열·숫자 호환, 타 버튼 값·순서·배치·아이콘 보존");
}
finally { File.Delete(fixture); }
