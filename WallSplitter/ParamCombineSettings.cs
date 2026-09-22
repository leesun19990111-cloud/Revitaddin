using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WallSplitter
{
    // 자리수를 맞출 때 채움 문자를 어느 쪽에 넣을지.
    // Left = 값 앞에 채움(숫자 앞의 0 - 기본), Right = 값 뒤에 채움(문자 코드를 왼쪽 정렬할 때).
    public enum PadSide
    {
        Left,
        Right,
    }

    // 결합에 참여하는 소스 매개변수 하나. "매개변수 값을 읽어 → 자리수를 맞추고 → 뒤에 구분자를 붙인다"가 전부다.
    public class CombineSourceField
    {
        // Revit에 실제로 적혀 있는 매개변수 이름과 한 글자까지 같아야 한다(내장/공유/프로젝트 매개변수 모두 가능).
        public string ParameterName { get; set; } = "";

        // 0이면 자리수를 맞추지 않고 값을 그대로 쓴다.
        public int Width { get; set; }

        // 자리수를 채울 문자. 빈 문자열이면 채우지 않는다(Width만 길이 검사에 쓰임).
        public string PadChar { get; set; } = "0";

        public PadSide Pad { get; set; } = PadSide.Left;

        // 값이 Width보다 길 때 잘라낼지. 기본은 자르지 않는다 - 실번호가 조용히 뭉개지는 것보다
        // 길어진 채로 눈에 띄는 편이 낫기 때문(예: 번호 1234를 3자리로 자르면 어느 쪽을 버려도 틀린 값이 된다).
        public bool Truncate { get; set; }

        // 이 값 "다음"에 들어갈 구분자(마지막 항목의 것은 쓰이지 않는다 - NamingSettings.Render와 같은 규칙).
        public string SeparatorAfter { get; set; } = "";

        public CombineSourceField Clone() => new CombineSourceField
        {
            ParameterName = ParameterName,
            Width = Width,
            PadChar = PadChar,
            Pad = Pad,
            Truncate = Truncate,
            SeparatorAfter = SeparatorAfter,
        };
    }

    // "이 카테고리의 요소에서, 이 매개변수들을 이 순서로 합쳐, 이 매개변수에 써넣는다" 한 줄짜리 규칙.
    public class CombineRule
    {
        // 사용자가 알아보기 위한 이름일 뿐 식별자가 아니다(식별은 Id).
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string Name { get; set; } = "실번호";
        public bool Enabled { get; set; } = true;

        // 내장 카테고리의 ElementId 정수값(음수). 문서가 달라져도 같은 값이라 설정 파일에 그대로 저장할 수 있다
        // (QuickToggleSettings가 ElementId를 int로 저장하는 것과 같은 이유 - ElementIdCompat 주석 참고).
        public int CategoryId { get; set; } = (int)BuiltInCategoryIds.Rooms;

        public List<CombineSourceField> Sources { get; set; } = new List<CombineSourceField>();

        // 결과를 써넣을 인스턴스 매개변수 이름(문자 형식이어야 한다).
        public string TargetParameterName { get; set; } = "실번호";

        // 소스 중 하나라도 값이 비어 있으면 아예 쓰지 않는다 - 반쯤 완성된 실번호가 들어가는 것을 막는 용도.
        public bool SkipWhenAnySourceEmpty { get; set; }

        public CombineRule Clone() => new CombineRule
        {
            Id = Id,
            Name = Name,
            Enabled = Enabled,
            CategoryId = CategoryId,
            Sources = Sources.Select(s => s.Clone()).ToList(),
            TargetParameterName = TargetParameterName,
            SkipWhenAnySourceEmpty = SkipWhenAnySourceEmpty,
        };

        // 설정 창을 한 번도 안 연 사용자에게 곧바로 쓸 만한 기본 규칙.
        // 자리수는 용도 1 / 지상지하 1 / 층 2 / 번호 3 - 예: 용도 X, 지상지하 X, 층 1, 번호 3 -> "XX01003".
        public static CombineRule DefaultRoomNumberRule() => new CombineRule
        {
            Name = "실번호",
            CategoryId = (int)BuiltInCategoryIds.Rooms,
            TargetParameterName = "실번호",
            Sources = new List<CombineSourceField>
            {
                new CombineSourceField { ParameterName = "용도", Width = 1, PadChar = "0", Pad = PadSide.Left },
                new CombineSourceField { ParameterName = "지상지하", Width = 1, PadChar = "0", Pad = PadSide.Left },
                new CombineSourceField { ParameterName = "층", Width = 2, PadChar = "0", Pad = PadSide.Left },
                new CombineSourceField { ParameterName = "번호", Width = 3, PadChar = "0", Pad = PadSide.Left },
            },
        };
    }

    // Autodesk.Revit.DB를 참조하지 않고도 기본값을 적을 수 있도록 실제로 쓰는 카테고리 id만 상수로 둔다
    // (설정 클래스는 순수 데이터로 두어 설정 창/엔진 어느 쪽에서도 부담 없이 쓰게 하기 위함).
    internal static class BuiltInCategoryIds
    {
        public const int Rooms = -2000160; // OST_Rooms
    }

    public class ParamCombineSettings
    {
        // 리본의 "실시간" 토글. 꺼두면 규칙은 그대로 있고 자동 반영만 멈춘다(전체 갱신 버튼은 계속 동작).
        public bool AutoUpdate { get; set; } = true;

        public List<CombineRule> Rules { get; set; } = new List<CombineRule>();

        public ParamCombineSettings Clone() => new ParamCombineSettings
        {
            AutoUpdate = AutoUpdate,
            Rules = Rules.Select(r => r.Clone()).ToList(),
        };

        public IEnumerable<CombineRule> ActiveRules => Rules.Where(r =>
            r.Enabled &&
            r.Sources.Count > 0 &&
            !string.IsNullOrWhiteSpace(r.TargetParameterName));

        private static string ConfigPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "WallSplitter", "param-combine-settings.json");

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() },
        };

        // IUpdater.Execute는 사용자가 값을 하나 바꿀 때마다 불린다 - 거기서 매번 디스크를 읽으면 편집이
        // 눈에 띄게 굼떠지므로 메모리에 캐시하고, 설정을 저장할 때만 갈아끼운다.
        private static ParamCombineSettings? _cached;

        public static ParamCombineSettings Current => _cached ??= Load();

        public static ParamCombineSettings Load()
        {
            try
            {
                if (File.Exists(ConfigPath))
                {
                    string json = File.ReadAllText(ConfigPath, Encoding.UTF8);
                    ParamCombineSettings? loaded = JsonSerializer.Deserialize<ParamCombineSettings>(json, JsonOptions);
                    if (loaded != null)
                    {
                        loaded.Rules ??= new List<CombineRule>();
                        foreach (CombineRule rule in loaded.Rules)
                            rule.Sources ??= new List<CombineSourceField>();
                        return loaded;
                    }
                }
            }
            catch
            {
                // 설정 파일이 손상된 경우 기본값으로 대체
            }

            return new ParamCombineSettings
            {
                // 기본값은 "켜짐"이 아니다 - 규칙이 이 사용자의 매개변수 이름과 맞는지 확인하기 전에
                // 모델을 자동으로 고치기 시작하면 곤란하다. 설정 창에서 확인한 뒤 직접 켜게 한다.
                AutoUpdate = false,
                Rules = new List<CombineRule> { CombineRule.DefaultRoomNumberRule() },
            };
        }

        public void Save()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
            string json = JsonSerializer.Serialize(this, JsonOptions);
            File.WriteAllText(ConfigPath, json, Encoding.UTF8);
            _cached = this;
        }
    }
}
