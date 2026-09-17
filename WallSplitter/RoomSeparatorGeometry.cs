using System;
using System.Collections.Generic;

namespace WallSplitter
{
    // 룸 구분선으로 만들 선분들을 "벽처럼" 정리하는 순수 계산. **일부러 Revit 타입을 쓰지 않는다** -
    // 이 프로젝트의 관례대로(RejectOutliersAndUnion, CenterShiftFromFaces) 하네스에서 리플렉션으로 직접
    // 돌려 검증하기 위해서다. 좌표는 전부 한 레벨의 평면(XY)이므로 2D로 충분하다.
    //
    // 2026-09-17 사용자 요청: *"그 벽과 연결되어 있는 벽들에 그릴 때에는 룸구분선도 벽처럼 서로 다른 두
    // 방향의 선이 연결되어 있어야 해(trim). 그리고 같은 선상에 있는 쪼개진 여러 룸 구분선은 딱 한 줄로
    // 이어져서 그려졌으면 좋겠어."*
    //
    // 벽 중심선을 그대로 쓰면 모서리에서 두 선이 만나지 않는다 - 서로 상대 벽의 두께 절반만큼 못 미치거나
    // 지나쳐 버리기 때문이다. 그래서 두 단계로 정리한다:
    //   1) JoinAtCorners  - 방향이 다른 두 선이 만나야 할 자리(무한직선 교점)로 끝점을 옮긴다(연장/잘라내기)
    //   2) MergeCollinear - 같은 직선 위에서 닿거나 겹치는 선분들을 한 줄로 합친다
    // 1을 먼저 하는 것이 중요하다: 가로지르는 벽 때문에 쪼개져 있던 같은 선상의 조각들이 1에서 같은 점까지
    // 연장되어 서로 닿게 되고, 그래야 2가 그것들을 한 줄로 합칠 수 있다.
    internal static class RoomSeparatorGeometry
    {
        internal struct Seg
        {
            public double X0, Y0, X1, Y1;

            public Seg(double x0, double y0, double x1, double y1)
            {
                X0 = x0; Y0 = y0; X1 = x1; Y1 = y1;
            }

            public double Dx => X1 - X0;
            public double Dy => Y1 - Y0;
            public double Length => Math.Sqrt(Dx * Dx + Dy * Dy);
        }

        // 두 선분이 "평행"인지. 단위벡터 외적의 절대값으로 재므로 값이 곧 사이각의 sin이다.
        private const double ParallelSin = 0.01;        // 약 0.57도

        // 같은 직선으로 볼 수직 거리(피트). 중심선끼리는 정확히 겹치거나 확실히 떨어져 있어 넉넉하지 않아도 된다.
        private const double SameLineOffset = 0.02;     // 약 6mm

        // 합칠 때 허용하는 틈(피트). 1단계에서 모서리를 맞추고 나면 같은 선상 조각들은 서로 닿으므로,
        // 여기서는 부동소수 오차만 흡수하면 된다. **문을 사이에 둔 별개의 벽까지 이어 붙이지 않으려면
        // 이 값을 크게 키우지 말 것.**
        private const double MergeGap = 0.05;           // 약 15mm

        // ===== 1단계: 모서리에서 만나게 하기 =====
        //
        // reach = 끝점을 옮겨도 되는 최대 거리(피트). 벽 두께의 절반 정도면 충분하지만 두꺼운 벽까지
        // 감안해 호출부가 정한다. 이 거리를 넘는 교점은 "우연히 멀리서 만나는 남의 선"이므로 무시한다.
        //
        // **조정량은 전부 원본 기준으로 계산한 뒤 한꺼번에 적용한다** - 하나씩 옮기면서 그 결과를 다음
        // 계산에 쓰면 처리 순서에 따라 결과가 달라진다(같은 모델에서 실행할 때마다 선이 달라지는 건 최악이다).
        public static List<Seg> JoinAtCorners(List<Seg> segments, double reach)
        {
            int n = segments.Count;
            double[] nx0 = new double[n], ny0 = new double[n], nx1 = new double[n], ny1 = new double[n];
            for (int i = 0; i < n; i++)
            {
                nx0[i] = segments[i].X0; ny0[i] = segments[i].Y0;
                nx1[i] = segments[i].X1; ny1[i] = segments[i].Y1;
            }

            for (int i = 0; i < n; i++)
            {
                if (segments[i].Length < 1e-9) continue;

                for (int end = 0; end < 2; end++)
                {
                    double px = end == 0 ? segments[i].X0 : segments[i].X1;
                    double py = end == 0 ? segments[i].Y0 : segments[i].Y1;

                    double bestDist = reach;
                    double bx = 0, by = 0;
                    bool found = false;

                    for (int j = 0; j < n; j++)
                    {
                        if (j == i || segments[j].Length < 1e-9) continue;
                        if (!TryIntersect(segments[i], segments[j], out double ix, out double iy)) continue;

                        double d = Distance(px, py, ix, iy);
                        if (d > bestDist) continue;

                        // 교점이 상대 선분의 범위(양끝을 reach만큼 넉넉히 본 것) 안에 있어야 한다.
                        // 그러지 않으면 멀리 떨어진 선의 "연장선"과 만나는 엉뚱한 점에 붙는다.
                        if (!WithinExtent(segments[j], ix, iy, reach)) continue;

                        bestDist = d;
                        bx = ix; by = iy;
                        found = true;
                    }

                    if (!found) continue;
                    if (end == 0) { nx0[i] = bx; ny0[i] = by; }
                    else { nx1[i] = bx; ny1[i] = by; }
                }
            }

            List<Seg> result = new List<Seg>(n);
            for (int i = 0; i < n; i++)
            {
                Seg s = new Seg(nx0[i], ny0[i], nx1[i], ny1[i]);
                if (s.Length > 1e-6) result.Add(s);   // 양끝이 같은 점으로 무너진 것은 버린다
            }
            return result;
        }

        // 두 선분이 놓인 **무한직선**의 교점. 평행이면 false.
        private static bool TryIntersect(Seg a, Seg b, out double x, out double y)
        {
            x = 0; y = 0;
            double la = a.Length, lb = b.Length;
            if (la < 1e-9 || lb < 1e-9) return false;

            double ax = a.Dx / la, ay = a.Dy / la;
            double bx = b.Dx / lb, by = b.Dy / lb;

            double cross = ax * by - ay * bx;
            if (Math.Abs(cross) < ParallelSin) return false;

            // a.P0 + t*(ax,ay) = b.P0 + u*(bx,by)
            double t = ((b.X0 - a.X0) * by - (b.Y0 - a.Y0) * bx) / cross;
            x = a.X0 + t * ax;
            y = a.Y0 + t * ay;
            return true;
        }

        // 점이 선분 범위 안(양끝을 slack만큼 늘린 것)에 있는가 - 선 위에 있다는 전제로 투영값만 본다.
        private static bool WithinExtent(Seg s, double x, double y, double slack)
        {
            double len = s.Length;
            if (len < 1e-9) return false;
            double ux = s.Dx / len, uy = s.Dy / len;
            double t = (x - s.X0) * ux + (y - s.Y0) * uy;
            return t >= -slack && t <= len + slack;
        }

        private static double Distance(double x0, double y0, double x1, double y1)
        {
            double dx = x1 - x0, dy = y1 - y0;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        // ===== 2단계: 같은 직선 위의 조각들을 한 줄로 =====
        public static List<Seg> MergeCollinear(List<Seg> segments)
        {
            List<Seg> pending = new List<Seg>();
            foreach (Seg s in segments) if (s.Length > 1e-9) pending.Add(s);

            List<Seg> result = new List<Seg>();
            bool[] used = new bool[pending.Count];

            for (int i = 0; i < pending.Count; i++)
            {
                if (used[i]) continue;
                used[i] = true;

                Seg baseSeg = pending[i];
                double len = baseSeg.Length;
                double ux = baseSeg.Dx / len, uy = baseSeg.Dy / len;

                // 이 직선 위에 있는 조각들을 모아 방향축 위의 구간으로 바꾼다.
                List<(double Start, double End)> spans = new List<(double, double)>
                {
                    (0, len),
                };

                for (int j = i + 1; j < pending.Count; j++)
                {
                    if (used[j]) continue;
                    if (!SameInfiniteLine(baseSeg, pending[j], ux, uy)) continue;

                    used[j] = true;
                    double t0 = (pending[j].X0 - baseSeg.X0) * ux + (pending[j].Y0 - baseSeg.Y0) * uy;
                    double t1 = (pending[j].X1 - baseSeg.X0) * ux + (pending[j].Y1 - baseSeg.Y0) * uy;
                    spans.Add((Math.Min(t0, t1), Math.Max(t0, t1)));
                }

                // 구간들을 정렬해 훑으며 닿거나 겹치는 것을 잇는다.
                spans.Sort((a, b) => a.Start.CompareTo(b.Start));
                double curStart = spans[0].Start, curEnd = spans[0].End;
                for (int k = 1; k < spans.Count; k++)
                {
                    if (spans[k].Start <= curEnd + MergeGap)
                    {
                        if (spans[k].End > curEnd) curEnd = spans[k].End;
                    }
                    else
                    {
                        result.Add(FromSpan(baseSeg, ux, uy, curStart, curEnd));
                        curStart = spans[k].Start;
                        curEnd = spans[k].End;
                    }
                }
                result.Add(FromSpan(baseSeg, ux, uy, curStart, curEnd));
            }

            return result;
        }

        // 두 선분이 같은 무한직선 위에 있는가 - 방향이 나란하고(부호는 반대여도 됨) 수직 거리가 거의 0.
        private static bool SameInfiniteLine(Seg baseSeg, Seg other, double ux, double uy)
        {
            double len = other.Length;
            if (len < 1e-9) return false;
            double vx = other.Dx / len, vy = other.Dy / len;
            if (Math.Abs(ux * vy - uy * vx) > ParallelSin) return false;   // 평행하지 않음

            // baseSeg의 직선에서 other의 양 끝점까지의 수직 거리
            double d0 = Math.Abs((other.X0 - baseSeg.X0) * (-uy) + (other.Y0 - baseSeg.Y0) * ux);
            double d1 = Math.Abs((other.X1 - baseSeg.X0) * (-uy) + (other.Y1 - baseSeg.Y0) * ux);
            return d0 <= SameLineOffset && d1 <= SameLineOffset;
        }

        private static Seg FromSpan(Seg baseSeg, double ux, double uy, double start, double end) =>
            new Seg(
                baseSeg.X0 + ux * start, baseSeg.Y0 + uy * start,
                baseSeg.X0 + ux * end, baseSeg.Y0 + uy * end);

        // 두 단계를 순서대로 - 호출부는 이것만 쓰면 된다.
        public static List<Seg> Cleanup(List<Seg> segments, double reach) =>
            MergeCollinear(JoinAtCorners(segments, reach));
    }
}
