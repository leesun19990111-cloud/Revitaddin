using System;
using System.Collections.Generic;
using System.Linq;

namespace WallSplitter
{
    internal static class PatternTransformService
    {
        private const double MinimumScale = 0.0001;
        private const double MinimumOffset = 1e-9;

        public static PatternDefinition Transform(PatternDefinition source, PatternTransformSettings settings)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (settings == null) throw new ArgumentNullException(nameof(settings));

            double uniform = PositiveScale(settings.UniformScalePercent, "전체 스케일");
            double width = PositiveScale(settings.WidthPercent, "폭");
            double height = PositiveScale(settings.HeightPercent, "높이");
            double sx = uniform * width;
            double sy = uniform * height;
            double referenceAxis = source.Grids.Count > 0 ? DegreesToRadians(source.Grids[0].AngleDegrees) : 0.0;
            double globalRotation = DegreesToRadians(RequireFinite(settings.RotationDegrees, "전체 회전"));

            // 같은 Index가 두 번 들어오면 ToDictionary가 ArgumentException을 던져 미리보기 전체가 멈춘다.
            // 편집 상태는 선군마다 하나만 의미가 있으므로 마지막 값을 쓴다.
            var editByIndex = new Dictionary<int, PatternGridEditState>();
            foreach (PatternGridEditState edit in settings.GridEdits) editByIndex[edit.Index] = edit;
            PatternDefinition result = source.Clone();
            result.SourceElementId = source.SourceElementId;
            result.Grids.Clear();

            for (int index = 0; index < source.Grids.Count; index++)
            {
                PatternGridDefinition grid = source.Grids[index];
                PatternGridEditState edit = editByIndex.TryGetValue(index, out PatternGridEditState? found)
                    ? found
                    : new PatternGridEditState { Index = index };

                double groupScale = PositiveScale(edit.SizePercent, $"선군 {index + 1} 크기");
                double spacingScale = PositiveScale(edit.SpacingPercent, $"선군 {index + 1} 간격");
                double groupRotation = DegreesToRadians(RequireFinite(edit.AngleDeltaDegrees, $"선군 {index + 1} 회전"));

                double angle = DegreesToRadians(grid.AngleDegrees);
                Vector2 direction = new Vector2(Math.Cos(angle), Math.Sin(angle));
                Vector2 normal = new Vector2(-direction.Y, direction.X);
                Vector2 origin = new Vector2(grid.OriginX, grid.OriginY);
                Vector2 lattice = direction * grid.Shift + normal * grid.Offset;

                Vector2 transformedDirection = Rotate(ScaleAlongAxes(direction, referenceAxis, sx, sy), globalRotation);
                double directionLength = transformedDirection.Length;
                if (directionLength < MinimumOffset)
                    throw new InvalidOperationException($"선군 {index + 1}의 방향을 계산할 수 없습니다.");
                transformedDirection /= directionLength;

                Vector2 transformedOrigin = Rotate(ScaleAlongAxes(origin, referenceAxis, sx, sy), globalRotation);
                Vector2 transformedLattice = Rotate(ScaleAlongAxes(lattice, referenceAxis, sx, sy), globalRotation);

                // 선군 회전은 그 선군의 기준점은 고정한 채 방향과 반복 격자를 함께 돌린다.
                transformedDirection = Rotate(transformedDirection, groupRotation);
                transformedLattice = Rotate(transformedLattice, groupRotation);

                transformedLattice *= groupScale;
                Vector2 transformedNormal = new Vector2(-transformedDirection.Y, transformedDirection.X);
                double newShift = Dot(transformedLattice, transformedDirection);
                double newOffset = Dot(transformedLattice, transformedNormal) * spacingScale;
                if (Math.Abs(newOffset) < MinimumOffset)
                    throw new InvalidOperationException($"선군 {index + 1}의 간격이 0에 너무 가까워 저장할 수 없습니다.");

                result.Grids.Add(new PatternGridDefinition
                {
                    AngleDegrees = NormalizeDegrees(RadiansToDegrees(Math.Atan2(transformedDirection.Y, transformedDirection.X))),
                    OriginX = transformedOrigin.X,
                    OriginY = transformedOrigin.Y,
                    Shift = newShift,
                    Offset = newOffset,
                    Segments = grid.Segments.Select(segment => segment * directionLength * groupScale).ToList(),
                });
            }

            return result;
        }

        public static List<string> Validate(PatternDefinition definition)
        {
            var errors = new List<string>();
            if (string.IsNullOrWhiteSpace(definition.Name)) errors.Add("패턴 이름이 비어 있습니다.");
            if (definition.Grids.Count == 0) errors.Add("저장할 선군이 없습니다.");

            for (int index = 0; index < definition.Grids.Count; index++)
            {
                PatternGridDefinition grid = definition.Grids[index];
                if (!IsFinite(grid.AngleDegrees) || !IsFinite(grid.OriginX) || !IsFinite(grid.OriginY) ||
                    !IsFinite(grid.Shift) || !IsFinite(grid.Offset) || grid.Segments.Any(segment => !IsFinite(segment)))
                    errors.Add($"선군 {index + 1}에 올바르지 않은 숫자가 있습니다.");
                if (Math.Abs(grid.Offset) < MinimumOffset)
                    errors.Add($"선군 {index + 1}의 간격은 0일 수 없습니다.");
            }

            return errors;
        }

        private static double PositiveScale(double percent, string label)
        {
            percent = RequireFinite(percent, label);
            double scale = percent / 100.0;
            if (scale < MinimumScale) throw new InvalidOperationException($"{label} 값은 0보다 커야 합니다.");
            return scale;
        }

        private static double RequireFinite(double value, string label)
        {
            if (!IsFinite(value)) throw new InvalidOperationException($"{label} 값이 올바르지 않습니다.");
            return value;
        }

        private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
        private static double DegreesToRadians(double degrees) => degrees * Math.PI / 180.0;
        private static double RadiansToDegrees(double radians) => radians * 180.0 / Math.PI;
        private static double Dot(Vector2 a, Vector2 b) => a.X * b.X + a.Y * b.Y;

        private static double NormalizeDegrees(double value)
        {
            value %= 360.0;
            if (value < 0.0) value += 360.0;
            return value;
        }

        private static Vector2 ScaleAlongAxes(Vector2 vector, double axis, double sx, double sy)
        {
            Vector2 local = Rotate(vector, -axis);
            return Rotate(new Vector2(local.X * sx, local.Y * sy), axis);
        }

        private static Vector2 Rotate(Vector2 vector, double radians)
        {
            double cos = Math.Cos(radians);
            double sin = Math.Sin(radians);
            return new Vector2(vector.X * cos - vector.Y * sin, vector.X * sin + vector.Y * cos);
        }

        private readonly struct Vector2
        {
            public Vector2(double x, double y)
            {
                X = x;
                Y = y;
            }

            public double X { get; }
            public double Y { get; }
            public double Length => Math.Sqrt(X * X + Y * Y);

            public static Vector2 operator +(Vector2 a, Vector2 b) => new Vector2(a.X + b.X, a.Y + b.Y);
            public static Vector2 operator *(Vector2 value, double scale) => new Vector2(value.X * scale, value.Y * scale);
            public static Vector2 operator *(double scale, Vector2 value) => value * scale;
            public static Vector2 operator /(Vector2 value, double scale) => new Vector2(value.X / scale, value.Y / scale);
        }
    }
}
