using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WallSplitter
{
    // 단일 벽 유형 이름을 구성하는 매개변수 종류.
    // ProjectName/BuildingName/Custom: 설정 창에서 사용자가 직접 입력하는 고정 텍스트.
    // Material/Thickness: 분리 대상 복합벽의 레이어에서 자동으로 읽어오며, 설정 창에서 값 편집은 불가능(순서/구분자만 조정 가능).
    public enum TokenKind
    {
        ProjectName,
        BuildingName,
        Material,
        Thickness,
        Custom,
    }

    public class NamingToken
    {
        public TokenKind Kind { get; set; }
        public string Value { get; set; } = "";
        // 이 매개변수 "다음"에 들어갈 구분자: "", "-", "_" 중 하나.
        public string SeparatorAfter { get; set; } = "";
    }

    // 단일 벽 유형을 어떻게 정할지: Template=토큰 조합으로 이름을 만들어 유형을 자동 생성,
    // DirectType=레이어마다 이미 문서에 있는 유형을 검색해서 그대로 지정(자동 생성 없음).
    public enum NamingMode
    {
        Template,
        DirectType,
    }

    // DirectType 모드에서 지정한 유형을 다음 벽에도 이어서 쓸지: Single=벽마다 매번 새로 지정,
    // Multiple=레이어 구성(개수+재료+두께)이 직전 지정과 같으면 자동 재적용.
    public enum TypeAssignmentPersistence
    {
        Single,
        Multiple,
    }

    public class NamingSettings
    {
        public List<NamingToken> Tokens { get; set; } = new List<NamingToken>();
        public NamingMode Mode { get; set; } = NamingMode.Template;
        public TypeAssignmentPersistence TypeAssignmentPersistence { get; set; } = TypeAssignmentPersistence.Single;

        public static List<NamingToken> DefaultTokens() => new List<NamingToken>
        {
            new NamingToken { Kind = TokenKind.Custom, Value = "단일", SeparatorAfter = "_" },
            new NamingToken { Kind = TokenKind.Material, SeparatorAfter = "_" },
            new NamingToken { Kind = TokenKind.Thickness, SeparatorAfter = "" },
        };

        // Revit 요소/유형 이름에 사용할 수 없는 문자들 (Revit이 이름 지정 시 거부하는 집합)
        private static readonly char[] InvalidNameChars =
            { '\\', ':', '{', '}', '[', ']', '|', ';', '<', '>', '?', '`', '~', '\r', '\n', '\t' };

        // materialName/thicknessMm은 분리 중인 실제 레이어에서 읽은 값을 그대로 전달받는다.
        // 결과에서 Revit 금지 문자를 제거하며, 그러고도 빈 이름이 되면 기본 형식으로 대체한다
        // (빈 이름으로 WallType.Duplicate를 호출하면 예외가 나므로).
        public string BuildName(string materialName, double thicknessMm)
        {
            string name = Sanitize(Render(Tokens, materialName, thicknessMm));
            if (string.IsNullOrWhiteSpace(name))
                name = Sanitize(Render(DefaultTokens(), materialName, thicknessMm));
            return name;
        }

        private static string Render(List<NamingToken> tokens, string materialName, double thicknessMm)
        {
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < tokens.Count; i++)
            {
                NamingToken token = tokens[i];
                string part = token.Kind switch
                {
                    TokenKind.Material => materialName,
                    TokenKind.Thickness => $"{thicknessMm:F0}mm",
                    _ => token.Value ?? "",
                };
                sb.Append(part);
                if (i < tokens.Count - 1)
                    sb.Append(token.SeparatorAfter);
            }
            return sb.ToString();
        }

        private static string Sanitize(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "";
            StringBuilder sb = new StringBuilder(raw.Length);
            foreach (char c in raw)
            {
                if (Array.IndexOf(InvalidNameChars, c) < 0)
                    sb.Append(c);
            }
            return sb.ToString().Trim();
        }

        private static string ConfigPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "WallSplitter", "naming-settings.json");

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() },
        };

        // 사용자 PC(%APPDATA%)에 저장되는 전역 설정. 프로젝트/문서와 무관하게 한 번 저장하면 계속 적용된다.
        public static NamingSettings Load()
        {
            try
            {
                if (File.Exists(ConfigPath))
                {
                    string json = File.ReadAllText(ConfigPath, Encoding.UTF8);
                    NamingSettings? loaded = JsonSerializer.Deserialize<NamingSettings>(json, JsonOptions);
                    // Tokens가 비어 있어도(DirectType 모드에서는 애초에 안 쓰이므로 정상적으로 비어 있을 수 있다)
                    // 파일 자체는 유효하게 취급한다 - BuildName은 Tokens가 비어 있으면 알아서 기본 템플릿으로
                    // 대체하므로, 여기서 통째로 버리면 DirectType/TypeAssignmentPersistence까지 같이 사라진다.
                    if (loaded != null)
                    {
                        loaded.Tokens ??= new List<NamingToken>();
                        return loaded;
                    }
                }
            }
            catch
            {
                // 설정 파일이 손상된 경우 기본값으로 대체
            }
            return new NamingSettings { Tokens = DefaultTokens() };
        }

        public void Save()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
            string json = JsonSerializer.Serialize(this, JsonOptions);
            File.WriteAllText(ConfigPath, json, Encoding.UTF8);
        }
    }
}
