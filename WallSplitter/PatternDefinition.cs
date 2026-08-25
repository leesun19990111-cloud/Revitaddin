using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace WallSplitter
{
    internal sealed class PatternDefinition
    {
        public string Name { get; set; } = "새 패턴";
        public string Description { get; set; } = "";
        public FillPatternTarget Target { get; set; } = FillPatternTarget.Drafting;
        public FillPatternHostOrientation HostOrientation { get; set; } = FillPatternHostOrientation.ToView;
        public ElementId? SourceElementId { get; set; }
        public string SourceLabel { get; set; } = "";
        public string SourceUnitLabel { get; set; } = "Revit 내부 단위";
        public List<PatternGridDefinition> Grids { get; set; } = new List<PatternGridDefinition>();

        public PatternDefinition Clone()
        {
            return new PatternDefinition
            {
                Name = Name,
                Description = Description,
                Target = Target,
                HostOrientation = HostOrientation,
                SourceElementId = SourceElementId,
                SourceLabel = SourceLabel,
                SourceUnitLabel = SourceUnitLabel,
                Grids = Grids.Select(grid => grid.Clone()).ToList(),
            };
        }

        public override string ToString()
        {
            string type = Target == FillPatternTarget.Model ? "모델" : "제도";
            return $"{Name}  ·  {type}  ·  선군 {Grids.Count}개";
        }
    }

    internal sealed class PatternGridDefinition
    {
        public double AngleDegrees { get; set; }
        public double OriginX { get; set; }
        public double OriginY { get; set; }
        public double Shift { get; set; }
        public double Offset { get; set; }
        public List<double> Segments { get; set; } = new List<double>();

        public PatternGridDefinition Clone()
        {
            return new PatternGridDefinition
            {
                AngleDegrees = AngleDegrees,
                OriginX = OriginX,
                OriginY = OriginY,
                Shift = Shift,
                Offset = Offset,
                Segments = new List<double>(Segments),
            };
        }
    }

    // PAT와 편집기 내부에서는 양수=선, 음수=공백, 0=점으로 통일한다.
    // 반면 Revit FillGrid API와 PAT 사이에서는 홀수 인덱스의 부호가 반대다.
    // 따라서 읽기와 쓰기 모두 홀수 인덱스의 부호를 한 번 뒤집는 같은 변환을 사용한다.
    // 이 경계 변환이 없으면 HEX/원형 근사 패턴의 공백까지 선으로 그려져 무한 실선 격자가 된다.
    internal static class PatternSegmentCodec
    {
        public static List<double> FromRevit(IList<double> segments)
        {
            var result = new List<double>(segments.Count);
            for (int index = 0; index < segments.Count; index++)
            {
                double value = segments[index];
                result.Add(Math.Abs(value) < 1e-12 ? 0.0 : index % 2 == 0 ? value : -value);
            }
            return result;
        }

        public static List<double> ToRevit(IReadOnlyList<double> segments)
        {
            var result = new List<double>(segments.Count);
            for (int index = 0; index < segments.Count; index++)
            {
                double value = segments[index];
                result.Add(Math.Abs(value) < 1e-12 ? 0.0 : index % 2 == 0 ? value : -value);
            }
            return result;
        }
    }

    internal sealed class PatternGridEditState
    {
        public int Index { get; set; }
        public double AngleDeltaDegrees { get; set; }
        public double SizePercent { get; set; } = 100.0;
        public double SpacingPercent { get; set; } = 100.0;

        public string DisplayName => $"선군 {Index + 1}";
        public string Summary => $"회전 {AngleDeltaDegrees:0.##}°  ·  크기 {SizePercent:0.##}%  ·  간격 {SpacingPercent:0.##}%";

        public PatternGridEditState Clone()
        {
            return new PatternGridEditState
            {
                Index = Index,
                AngleDeltaDegrees = AngleDeltaDegrees,
                SizePercent = SizePercent,
                SpacingPercent = SpacingPercent,
            };
        }
    }

    internal sealed class PatternTransformSettings
    {
        public double RotationDegrees { get; set; }
        public double UniformScalePercent { get; set; } = 100.0;
        public double WidthPercent { get; set; } = 100.0;
        public double HeightPercent { get; set; } = 100.0;
        public List<PatternGridEditState> GridEdits { get; set; } = new List<PatternGridEditState>();

        public PatternTransformSettings Clone()
        {
            return new PatternTransformSettings
            {
                RotationDegrees = RotationDegrees,
                UniformScalePercent = UniformScalePercent,
                WidthPercent = WidthPercent,
                HeightPercent = HeightPercent,
                GridEdits = GridEdits.Select(edit => edit.Clone()).ToList(),
            };
        }
    }

    internal sealed class PatternStudioSaveRequest
    {
        public PatternDefinition Pattern { get; set; } = null!;
        public string Name { get; set; } = "";
        public bool OverwriteSource { get; set; }
        public ElementId? SourceElementId { get; set; }
    }
}
