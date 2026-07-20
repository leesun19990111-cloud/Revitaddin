using System.Diagnostics;
using System.Text;

Console.OutputEncoding = Encoding.UTF8;

Console.WriteLine("=== Sunny Tools Revit 애드인 설치 프로그램 (Revit 2023~2027 지원) ===");
Console.WriteLine();

// WallSplitter.csproj가 net48/net8.0-windows/net10.0-windows용으로 각각 미리 빌드해 둔 결과물.
string[] supportedYears = { "2023", "2024", "2025", "2026", "2027" };
// 기존 매니페스트와 동일한 AddInId. 재설치/업데이트 시 Revit이 동일한 애드인으로 인식하도록 값을 고정한다.
const string addInId = "E3FC07B6-D2EC-412F-9F78-453F51266352";

string payloadRoot = Path.Combine(AppContext.BaseDirectory, "Payload");
string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

if (Process.GetProcessesByName("Revit").Length > 0)
{
    Console.WriteLine("Revit이 실행 중입니다. 이미 설치된 애드인을 갱신하는 경우 파일이 잠겨 있어 실패할 수 있습니다.");
    Console.WriteLine("Revit을 종료한 뒤 Enter를 눌러 계속하세요. (그냥 진행하려면 Enter)");
    Console.ReadLine();
}

// 이 컴퓨터에 실제로 설치되어 있는 Revit 버전을 감지 (기본 설치 경로 기준).
List<string> detectedYears = supportedYears
    .Where(year => File.Exists(Path.Combine(@"C:\Program Files\Autodesk\Revit " + year, "Revit.exe")))
    .ToList();

// 감지된 버전이 하나도 없으면(비표준 설치 경로 등) 안전하게 지원하는 모든 연도에 설치를 시도한다.
List<string> targetYears = detectedYears.Count > 0 ? detectedYears : supportedYears.ToList();

if (detectedYears.Count == 0)
{
    Console.WriteLine("이 컴퓨터에서 기본 경로(C:\\Program Files\\Autodesk\\Revit <연도>)로 설치된 Revit을 찾지 못했습니다.");
    Console.WriteLine("혹시 몰라 지원하는 모든 버전(2023~2027)에 대해 설치를 시도합니다.");
}
else
{
    Console.WriteLine("감지된 Revit 버전: " + string.Join(", ", detectedYears));
}
Console.WriteLine();

var installed = new List<string>();
var failed = new List<string>();

foreach (string year in targetYears)
{
    string sourceDir = Path.Combine(payloadRoot, year);
    string sourceDll = Path.Combine(sourceDir, "WallSplitter.dll");
    if (!File.Exists(sourceDll))
    {
        Console.WriteLine($"[{year}] 건너뜀: 설치 프로그램 안에 이 버전용 파일이 없습니다.");
        continue;
    }

    string addinsRoot = Path.Combine(appData, "Autodesk", "Revit", "Addins", year);
    string installDir = Path.Combine(addinsRoot, "WallSplitter");

    try
    {
        Directory.CreateDirectory(installDir);

        // WallSplitter.dll뿐 아니라 같은 폴더의 모든 DLL을 복사한다.
        // net48(2023/2024) 빌드는 System.Text.Json 등 프레임워크에 내장되지 않은 의존 DLL이
        // 함께 필요하므로, WallSplitter.dll 하나만 복사하면 Revit에서 로드에 실패한다.
        foreach (string file in Directory.GetFiles(sourceDir, "*.dll"))
            File.Copy(file, Path.Combine(installDir, Path.GetFileName(file)), overwrite: true);

        string targetDll = Path.Combine(installDir, "WallSplitter.dll");

        string addinXml =
$@"<?xml version=""1.0"" encoding=""utf-8""?>
<RevitAddIns>
  <AddIn Type=""Application"">
    <Name>WallSplitter</Name>
    <Assembly>{targetDll}</Assembly>
    <AddInId>{addInId}</AddInId>
    <FullClassName>WallSplitter.App</FullClassName>
    <VendorId>Sunny</VendorId>
    <VendorDescription>Sunny Automation, WallSplitter</VendorDescription>
  </AddIn>
</RevitAddIns>
";
        string targetAddin = Path.Combine(addinsRoot, "WallSplitter.addin");
        File.WriteAllText(targetAddin, addinXml, Encoding.UTF8);

        Console.WriteLine($"[{year}] 설치 완료 -> {targetDll}");
        installed.Add(year);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[{year}] 설치 실패: {ex.Message}");
        failed.Add(year);
    }
}

Console.WriteLine();
if (installed.Count > 0)
{
    Console.WriteLine($"완료: {string.Join(", ", installed)} 버전에 설치되었습니다.");
    Console.WriteLine("해당 Revit을 (재)시작하면 상단 \"Sunny Tools\" 탭에 벽체 분리 / 일람표 뷰어 버튼이 나타납니다.");
}
else
{
    Console.WriteLine("설치된 버전이 없습니다.");
}
if (failed.Count > 0)
{
    Console.WriteLine($"실패: {string.Join(", ", failed)} (Revit이 실행 중이라면 종료 후 다시 시도해 주세요.)");
}

Pause();
return installed.Count > 0 ? 0 : 1;

static void Pause()
{
    Console.WriteLine();
    Console.WriteLine("아무 키나 누르면 종료합니다...");
    Console.ReadKey();
}
