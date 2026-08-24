using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;

namespace WallSplitter
{
    internal sealed class PatImportResult
    {
        public List<PatternDefinition> Patterns { get; } = new List<PatternDefinition>();
        public List<string> Warnings { get; } = new List<string>();
    }

    internal static class PatFileService
    {
        private enum PatUnit
        {
            Millimeter,
            Inch,
        }

        private const double MillimetersPerFoot = 304.8;

        public static PatImportResult Import(string path)
        {
            var result = new PatImportResult();
            string[] lines = File.ReadAllLines(path, DetectEncoding(path));
            PatUnit unit = PatUnit.Millimeter;
            bool unitWasDeclared = false;
            PatternDefinition? current = null;

            for (int index = 0; index < lines.Length; index++)
            {
                string line = lines[index].Trim();
                if (line.Length == 0) continue;

                if (TryReadDirective(line, "%UNITS", out string unitValue))
                {
                    if (unitValue.Equals("MM", StringComparison.OrdinalIgnoreCase) ||
                        unitValue.Equals("MILLIMETER", StringComparison.OrdinalIgnoreCase) ||
                        unitValue.Equals("MILLIMETERS", StringComparison.OrdinalIgnoreCase))
                    {
                        unit = PatUnit.Millimeter;
                        unitWasDeclared = true;
                    }
                    else if (unitValue.Equals("INCH", StringComparison.OrdinalIgnoreCase) ||
                             unitValue.Equals("INCHES", StringComparison.OrdinalIgnoreCase))
                    {
                        unit = PatUnit.Inch;
                        unitWasDeclared = true;
                    }
                    else
                    {
                        result.Warnings.Add($"{index + 1}행: 알 수 없는 단위 '{unitValue}'를 mm로 해석했습니다.");
                        unit = PatUnit.Millimeter;
                    }
                    if (current != null) current.SourceUnitLabel = unit == PatUnit.Inch ? "inch" : "mm";
                    continue;
                }

                if (line.StartsWith("*", StringComparison.Ordinal))
                {
                    string header = line.Substring(1);
                    int comma = header.IndexOf(',');
                    string name = (comma >= 0 ? header.Substring(0, comma) : header).Trim();
                    string description = comma >= 0 ? header.Substring(comma + 1).Trim() : "";
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        result.Warnings.Add($"{index + 1}행: 이름이 없는 패턴을 건너뛰었습니다.");
                        current = null;
                        continue;
                    }

                    current = new PatternDefinition
                    {
                        Name = name,
                        Description = description,
                        Target = FillPatternTarget.Drafting,
                        HostOrientation = FillPatternHostOrientation.ToView,
                        SourceLabel = Path.GetFileName(path),
                        SourceUnitLabel = unit == PatUnit.Inch ? "inch" : "mm",
                    };
                    result.Patterns.Add(current);
                    continue;
                }

                if (TryReadDirective(line, "%TYPE", out string typeValue))
                {
                    if (current == null) continue;
                    if (typeValue.Equals("MODEL", StringComparison.OrdinalIgnoreCase))
                    {
                        current.Target = FillPatternTarget.Model;
                        current.HostOrientation = FillPatternHostOrientation.ToHost;
                    }
                    else if (typeValue.Equals("DRAFTING", StringComparison.OrdinalIgnoreCase))
                    {
                        current.Target = FillPatternTarget.Drafting;
                        current.HostOrientation = FillPatternHostOrientation.ToView;
                    }
                    else
                    {
                        result.Warnings.Add($"{index + 1}행: 알 수 없는 패턴 유형 '{typeValue}'를 제도 패턴으로 해석했습니다.");
                    }
                    continue;
                }

                if (line.StartsWith(";", StringComparison.Ordinal)) continue;
                if (current == null)
                {
                    result.Warnings.Add($"{index + 1}행: 패턴 이름보다 앞에 있는 정의를 건너뛰었습니다.");
                    continue;
                }

                string[] tokens = line.Split(',').Select(token => token.Trim()).ToArray();
                if (tokens.Length < 5)
                {
                    result.Warnings.Add($"{index + 1}행: 선군 정의에는 최소 5개 값이 필요합니다.");
                    continue;
                }

                var values = new List<double>(tokens.Length);
                bool parsed = true;
                foreach (string token in tokens)
                {
                    if (TryParseNumber(token, out double value)) values.Add(value);
                    else
                    {
                        result.Warnings.Add($"{index + 1}행: '{token}'을 숫자로 읽을 수 없어 선군을 건너뛰었습니다.");
                        parsed = false;
                        break;
                    }
                }
                if (!parsed) continue;

                double lengthFactor = unit == PatUnit.Inch ? 1.0 / 12.0 : 1.0 / MillimetersPerFoot;
                double offset = values[4] * lengthFactor;
                if (Math.Abs(offset) < 1e-9)
                {
                    result.Warnings.Add($"{index + 1}행: 간격이 0인 선군을 건너뛰었습니다.");
                    continue;
                }

                current.Grids.Add(new PatternGridDefinition
                {
                    AngleDegrees = values[0],
                    OriginX = values[1] * lengthFactor,
                    OriginY = values[2] * lengthFactor,
                    Shift = values[3] * lengthFactor,
                    Offset = offset,
                    Segments = values.Skip(5).Select(value => value * lengthFactor).ToList(),
                });
            }

            result.Patterns.RemoveAll(pattern =>
            {
                if (pattern.Grids.Count > 0) return false;
                result.Warnings.Add($"'{pattern.Name}'에는 사용할 수 있는 선군이 없어 목록에서 제외했습니다.");
                return true;
            });

            if (!unitWasDeclared && result.Patterns.Count > 0)
                result.Warnings.Insert(0, "단위 선언이 없어 길이 값을 mm로 해석했습니다.");
            return result;
        }

        public static void Export(string path, PatternDefinition pattern)
        {
            var lines = new List<string>
            {
                "; Sunny Tools 패턴 스튜디오에서 내보냄",
                ";%UNITS=MM",
                $"*{SanitizeHeader(pattern.Name)},{SanitizeHeader(pattern.Description)}",
            };
            if (pattern.Target == FillPatternTarget.Model) lines.Add(";%TYPE=MODEL");

            foreach (PatternGridDefinition grid in pattern.Grids)
            {
                var values = new List<string>
                {
                    Format(grid.AngleDegrees),
                    Format(grid.OriginX * MillimetersPerFoot),
                    Format(grid.OriginY * MillimetersPerFoot),
                    Format(grid.Shift * MillimetersPerFoot),
                    Format(grid.Offset * MillimetersPerFoot),
                };
                values.AddRange(grid.Segments.Select(segment => Format(segment * MillimetersPerFoot)));
                lines.Add(string.Join(", ", values));
            }

            File.WriteAllLines(path, lines, new UTF8Encoding(false));
        }

        private static bool TryReadDirective(string line, string name, out string value)
        {
            string normalized = line.TrimStart(';').Trim();
            string prefix = name + "=";
            if (normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                value = normalized.Substring(prefix.Length).Trim();
                return true;
            }
            value = "";
            return false;
        }

        private static bool TryParseNumber(string text, out double value)
        {
            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)) return true;

            int slash = text.IndexOf('/');
            if (slash > 0 && slash < text.Length - 1 &&
                double.TryParse(text.Substring(0, slash), NumberStyles.Float, CultureInfo.InvariantCulture, out double numerator) &&
                double.TryParse(text.Substring(slash + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out double denominator) &&
                Math.Abs(denominator) > double.Epsilon)
            {
                value = numerator / denominator;
                return true;
            }
            value = 0.0;
            return false;
        }

        private static string Format(double value) => value.ToString("0.##########", CultureInfo.InvariantCulture);
        private static string SanitizeHeader(string value) => (value ?? "").Replace("\r", " ").Replace("\n", " ").Replace(",", " ").Trim();

        private static Encoding DetectEncoding(string path)
        {
            using (var stream = File.OpenRead(path))
            {
                if (stream.Length >= 3)
                {
                    int first = stream.ReadByte();
                    int second = stream.ReadByte();
                    int third = stream.ReadByte();
                    if (first == 0xEF && second == 0xBB && third == 0xBF) return Encoding.UTF8;
                }
                if (stream.Length >= 2)
                {
                    stream.Position = 0;
                    int first = stream.ReadByte();
                    int second = stream.ReadByte();
                    if (first == 0xFF && second == 0xFE) return Encoding.Unicode;
                    if (first == 0xFE && second == 0xFF) return Encoding.BigEndianUnicode;
                }
            }

            byte[] bytes = File.ReadAllBytes(path);
            try
            {
                _ = new UTF8Encoding(false, true).GetString(bytes);
                return new UTF8Encoding(false);
            }
            catch (DecoderFallbackException)
            {
                // 기존 PAT 파일은 ANSI인 경우가 많다. .NET 8/10에서도 현재 Windows ANSI 코드 페이지를
                // 쓸 수 있도록 공급자를 리플렉션으로 등록한다(net48과 한 소스에서 빌드하기 위함).
                try
                {
                    Type? providerType = Type.GetType("System.Text.CodePagesEncodingProvider, System.Text.Encoding.CodePages");
                    if (providerType?.GetProperty("Instance")?.GetValue(null) is EncodingProvider provider)
                        Encoding.RegisterProvider(provider);
                    return Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.ANSICodePage);
                }
                catch
                {
                    return Encoding.Default;
                }
            }
        }
    }
}
