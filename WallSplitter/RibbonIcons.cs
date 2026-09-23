using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WallSplitter
{
    // 리본 버튼 아이콘을 전부 여기서 코드로 그린다.
    //
    // 예전에는 리본 아이콘이 PNG 리소스(Resources/icon*.png - 강조색 한 가지로만 칠한 실루엣)였고,
    // 나중에 만든 기능(패턴/경고Pick/룸 구분선/룸경계/매개변수 조합/일괄결합)만 코드로 그렸다. 그래서
    // 한 리본 위에 "단색 실루엣"과 "Industry 도면 스타일"이 섞여 있었다 - 2026-09-23 사용자 지적
    // ("전에 만들어졌던 버튼 디자인을 최근 버튼들과 결이 맞게 바꿔달라")으로 전부 이 파일로 옮겨
    // 같은 언어로 다시 그렸다. PNG 리소스는 이때 전부 제거했다(더 이상 참조하는 곳이 없다).
    //
    // 공통 규칙(Industry 테마 = 설계도면 스타일, docs/design-system/CLAUDE.md 참고):
    //  - 면은 밝은 회색(Fill)으로 채우고 얇은 먹선(Outline)으로 두른다. 모서리는 절대 둥글리지 않는다.
    //  - 강조색(Accent)은 "그 버튼이 실제로 하는 일"에만 쓴다(자르는 선, 합쳐진 결과, 켜진 상태 등).
    //    아이콘 전체를 강조색으로 칠하지 않는다 - 그러면 예전 단색 실루엣으로 되돌아가는 셈이다.
    //  - 16px에서도 읽혀야 한다. 요소 개수를 늘리는 대신 형태 대비(세로/가로, 채움/빈칸)로 구분한다.
    //  - 크기는 항상 인자로 받아 좌표를 비율로 계산한다(16/32 두 벌을 따로 그리지 않는다).
    internal static class RibbonIcons
    {
        // Theme.xaml / Theme.cs의 같은 이름 토큰과 값을 손으로 맞춰 유지할 것 - 자동으로 맞춰주는 장치는 없다.
        private static readonly SolidColorBrush Accent = Frozen(0x59, 0x80, 0xA6);   // AccentBrush
        private static readonly SolidColorBrush Outline = Frozen(0x1D, 0x1F, 0x20);  // TextPrimaryBrush
        private static readonly SolidColorBrush Fill = Frozen(0xE9, 0xE9, 0xEA);     // 카드보다 살짝 어두운 면
        private static readonly SolidColorBrush Paper = Frozen(0xF2, 0xF2, 0xF3);    // WindowBackgroundBrush
        private static readonly SolidColorBrush Muted = Frozen(0xB7, 0xB7, 0xBA);    // 꺼짐/비활성 표현용
        private static readonly SolidColorBrush Warning = Frozen(0xA6, 0x7B, 0x3D);  // WarningBrush

        private static SolidColorBrush Frozen(byte r, byte g, byte b)
        {
            var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
            brush.Freeze();
            return brush;
        }

        // 아이콘 바깥 여백. 리본이 아이콘을 다시 확대/축소하지 않도록 16/32 실제 크기에 맞춰 그린다.
        private static double Margin(int size) => Math.Max(1.5, size * 0.09);

        // 먹선(윤곽) 펜 - 16px에서 1px, 32px에서 2px.
        private static Pen OutlinePen(int size)
        {
            var pen = new Pen(Outline, Math.Max(1.0, size / 16.0));
            pen.Freeze();
            return pen;
        }

        private static Pen Stroke(SolidColorBrush brush, double thickness, bool round = true)
        {
            var pen = new Pen(brush, thickness);
            if (round)
            {
                pen.StartLineCap = PenLineCap.Round;
                pen.EndLineCap = PenLineCap.Round;
                pen.LineJoin = PenLineJoin.Round;
            }
            pen.Freeze();
            return pen;
        }

        private static BitmapSource Draw(int size, Action<DrawingContext> body)
        {
            var visual = new DrawingVisual();
            using (DrawingContext drawing = visual.RenderOpen())
                body(drawing);

            var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            bitmap.Freeze();
            return bitmap;
        }

        // 화살촉 - tip에서 dirDeg(진행 방향) 뒤쪽으로 벌어진 선 두 개.
        private static void ArrowHead(DrawingContext drawing, Pen pen, Point tip, double dirDeg, double length)
        {
            for (int side = -1; side <= 1; side += 2)
            {
                double angle = (dirDeg + 180 + side * 34) * Math.PI / 180.0;
                drawing.DrawLine(pen, tip, new Point(tip.X + Math.Cos(angle) * length, tip.Y + Math.Sin(angle) * length));
            }
        }

        // 화면 좌표(y가 아래로 증가) 기준 각도 → 점. 0°=오른쪽, 90°=아래, 270°=위.
        private static Point Polar(double cx, double cy, double radius, double degrees)
        {
            double a = degrees * Math.PI / 180.0;
            return new Point(cx + Math.Cos(a) * radius, cy + Math.Sin(a) * radius);
        }

        private static StreamGeometry Arc(double cx, double cy, double radius, double fromDeg, double toDeg)
        {
            var geometry = new StreamGeometry();
            using (StreamGeometryContext ctx = geometry.Open())
            {
                ctx.BeginFigure(Polar(cx, cy, radius, fromDeg), false, false);
                ctx.ArcTo(Polar(cx, cy, radius, toDeg), new Size(radius, radius), 0,
                    Math.Abs(toDeg - fromDeg) > 180, SweepDirection.Clockwise, true, false);
            }
            geometry.Freeze();
            return geometry;
        }

        // ── 벽체 분리 / 바닥 분리 ────────────────────────────────────────────────
        // 복합 부재 한 덩어리를 강조색 "자르는 선"이 켜켜이 나누는 그림. 예전 PNG가 벽=세로 막대,
        // 바닥=가로 막대로 구분됐던 것을 그대로 이어받아 방향만으로 둘을 구분한다(16px에서 가장 확실한 대비).
        // 자르는 선은 일부러 사각형 밖으로 조금 넘겨 그린다 - 도면의 절단선처럼 "여기서 끊는다"로 읽히게.
        public static BitmapSource SplitWall(int size) => SplitMember(size, vertical: true);

        public static BitmapSource SplitFloor(int size) => SplitMember(size, vertical: false);

        private static BitmapSource SplitMember(int size, bool vertical) => Draw(size, drawing =>
        {
            double margin = Margin(size);
            var box = new Rect(margin, margin, size - margin * 2, size - margin * 2);
            drawing.DrawRectangle(Fill, OutlinePen(size), box);

            Pen cut = Stroke(Accent, Math.Max(1.2, size / 13.0), round: false);
            double over = size * 0.08;
            for (int i = 1; i <= 2; i++)
            {
                double t = i / 3.0;
                if (vertical)
                {
                    double x = box.Left + box.Width * t;
                    drawing.DrawLine(cut, new Point(x, box.Top - over), new Point(x, box.Bottom + over));
                }
                else
                {
                    double y = box.Top + box.Height * t;
                    drawing.DrawLine(cut, new Point(box.Left - over, y), new Point(box.Right + over, y));
                }
            }
        });

        // ── 설정 ────────────────────────────────────────────────────────────────
        // 톱니바퀴 대신 "슬라이더 세 줄 + 강조색 손잡이". 16px에서 톱니는 뭉개지지만 이 형태는 또렷하고,
        // 곡선 없이 선과 사각형만 쓰는 이 테마의 문법에도 맞는다.
        public static BitmapSource Settings(int size) => Draw(size, drawing =>
        {
            double margin = Margin(size);
            Pen rail = Stroke(Outline, Math.Max(1.0, size / 16.0));
            Pen edge = OutlinePen(size);
            double knob = Math.Max(3.0, size * 0.22);
            double[] rows = { 0.27, 0.5, 0.73 };
            double[] handles = { 0.34, 0.68, 0.46 };

            for (int i = 0; i < rows.Length; i++)
            {
                double y = size * rows[i];
                drawing.DrawLine(rail, new Point(margin, y), new Point(size - margin, y));
                double cx = margin + (size - margin * 2) * handles[i];
                drawing.DrawRectangle(Accent, edge, new Rect(cx - knob / 2, y - knob / 2, knob, knob));
            }
        });

        // ── 켜짐/꺼짐 토글 ──────────────────────────────────────────────────────
        // 예전 PNG(알약형 스위치)를 모서리 반지름 0으로 다시 그린 것. 켜짐은 트랙을 강조색으로 채우고
        // 손잡이를 오른쪽에, 꺼짐은 빈 트랙에 회색 손잡이를 왼쪽에 둔다 - 색과 위치 두 가지로 구분된다.
        public static BitmapSource Toggle(int size, bool on) => Draw(size, drawing =>
        {
            double margin = Math.Max(1.0, size * 0.09);
            double height = Math.Max(6.0, size * 0.46);
            var track = new Rect(margin, (size - height) / 2.0, size - margin * 2, height);
            Pen edge = OutlinePen(size);
            drawing.DrawRectangle(on ? Accent : Fill, edge, track);

            double inset = Math.Max(1.0, size * 0.07);
            double side = track.Height - inset * 2;
            double x = on ? track.Right - inset - side : track.Left + inset;
            drawing.DrawRectangle(on ? Paper : Muted, edge, new Rect(x, track.Top + inset, side, side));
        });

        // ── NAMER ───────────────────────────────────────────────────────────────
        // 이름표(카드) 안의 강조색 "A"와, 그 옆에서 바뀌는 글줄 두 개. 예전 PNG의 "네모 안의 A" 실루엣을
        // 그대로 이어받되 글줄을 더해 "이름을 고친다"가 드러나게 했다.
        public static BitmapSource Namer(int size) => Draw(size, drawing =>
        {
            double margin = Margin(size);
            var box = new Rect(margin, margin, size - margin * 2, size - margin * 2);
            drawing.DrawRectangle(Fill, OutlinePen(size), box);

            Pen glyph = Stroke(Accent, Math.Max(1.3, size / 12.0));
            double left = box.Left + box.Width * 0.08;
            double width = box.Width * 0.42;
            double top = box.Top + box.Height * 0.2;
            double height = box.Height * 0.6;
            var apex = new Point(left + width / 2.0, top);
            drawing.DrawLine(glyph, apex, new Point(left, top + height));
            drawing.DrawLine(glyph, apex, new Point(left + width, top + height));
            drawing.DrawLine(glyph,
                new Point(left + width * 0.18, top + height * 0.66),
                new Point(left + width * 0.82, top + height * 0.66));

            Pen text = Stroke(Outline, Math.Max(1.0, size / 16.0));
            double tx = box.Left + box.Width * 0.6;
            double tw = box.Width * 0.32;
            drawing.DrawLine(text, new Point(tx, box.Top + box.Height * 0.35), new Point(tx + tw, box.Top + box.Height * 0.35));
            drawing.DrawLine(text, new Point(tx, box.Top + box.Height * 0.63), new Point(tx + tw * 0.62, box.Top + box.Height * 0.63));
        });

        // ── 재료 지정 ───────────────────────────────────────────────────────────
        // 겹친 재료 견본 두 장. 예전 PNG(겹친 사각형 둘)의 실루엣 그대로, 앞장만 강조색으로 채워
        // "이 재료를 지정한다"를 드러낸다.
        public static BitmapSource MaterialAssign(int size) => Draw(size, drawing =>
        {
            double margin = Margin(size);
            double span = size - margin * 2;
            double side = span * 0.68;
            Pen edge = OutlinePen(size);
            drawing.DrawRectangle(Fill, edge, new Rect(margin, margin, side, side));
            drawing.DrawRectangle(Accent, edge, new Rect(margin + span - side, margin + span - side, side, side));
        });

        // ── 모델간 변경 반영 ────────────────────────────────────────────────────
        // 모델 두 개(카드)와 그 사이를 건너가는 강조색 화살표 - "이 모델의 변경을 저 모델에 반영한다".
        // 예전 PNG는 순환 화살표라 '전체 갱신'과 구분이 안 됐는데, 이제 순환 화살표는 Refresh에만 쓴다.
        public static BitmapSource ModelSync(int size) => Draw(size, drawing =>
        {
            double margin = Math.Max(0.8, size * 0.05);
            double cardWidth = size * 0.22;
            double cardHeight = size * 0.62;
            double top = (size - cardHeight) / 2.0;
            Pen edge = OutlinePen(size);
            drawing.DrawRectangle(Fill, edge, new Rect(margin, top, cardWidth, cardHeight));
            drawing.DrawRectangle(Fill, edge, new Rect(size - margin - cardWidth, top, cardWidth, cardHeight));

            Pen arrow = Stroke(Accent, Math.Max(1.3, size / 12.0));
            double middle = size / 2.0;
            double from = margin + cardWidth + size * 0.03;
            double to = size - margin - cardWidth - size * 0.03;
            drawing.DrawLine(arrow, new Point(from, middle), new Point(to, middle));
            ArrowHead(drawing, arrow, new Point(to, middle), 0, size * 0.18);
        });

        // ── 전체 갱신 ───────────────────────────────────────────────────────────
        // 순환 화살표 두 개. 어느 창도 열지 않고 "지금 다시 한 번 훑는다"는 뜻이라 익숙한 새로고침 기호를 쓴다.
        public static BitmapSource Refresh(int size) => Draw(size, drawing =>
        {
            double margin = Margin(size);
            double center = size / 2.0;
            double radius = (size - margin * 2) / 2.0 * 0.86;
            Pen pen = Stroke(Accent, Math.Max(1.4, size / 10.0), round: false);

            drawing.DrawGeometry(null, pen, Arc(center, center, radius, 190, 348));
            ArrowHead(drawing, pen, Polar(center, center, radius, 348), 78, size * 0.2);

            drawing.DrawGeometry(null, pen, Arc(center, center, radius, 10, 168));
            ArrowHead(drawing, pen, Polar(center, center, radius, 168), -102, size * 0.2);
        });

        // ── 커스텀 버튼 ─────────────────────────────────────────────────────────
        // 예전 PNG의 번개 실루엣 그대로, 강조색으로 채우고 먹선을 둘렀다("원클릭"의 상징이라 형태는 유지).
        public static BitmapSource QuickToggle(int size) => Draw(size, drawing =>
        {
            double margin = Margin(size);
            double span = size - margin * 2;
            Point P(double fx, double fy) => new Point(margin + span * fx, margin + span * fy);

            var bolt = new StreamGeometry();
            using (StreamGeometryContext ctx = bolt.Open())
            {
                ctx.BeginFigure(P(0.62, 0.0), true, true);
                ctx.LineTo(P(0.14, 0.57), true, true);
                ctx.LineTo(P(0.44, 0.57), true, true);
                ctx.LineTo(P(0.34, 1.0), true, true);
                ctx.LineTo(P(0.86, 0.41), true, true);
                ctx.LineTo(P(0.54, 0.41), true, true);
            }
            bolt.Freeze();
            drawing.DrawGeometry(Accent, OutlinePen(size), bolt);
        });

        // ── 패턴 스튜디오 ───────────────────────────────────────────────────────
        // 반복 해치로 채운 도면 조각. 원래도 코드로 그렸지만 바탕이 비어 있어 다른 아이콘보다 가벼워
        // 보였으므로, 다른 아이콘과 같은 밝은 면을 깔고 그 위에 해치를 올린다.
        public static BitmapSource PatternStudio(int size) => Draw(size, drawing =>
        {
            double margin = Margin(size);
            var bounds = new Rect(margin, margin, size - margin * 2, size - margin * 2);
            Pen edge = OutlinePen(size);
            Pen hatch = Stroke(Accent, Math.Max(1.0, size / 16.0), round: false);
            Pen thin = Stroke(Outline, Math.Max(0.7, size / 32.0), round: false);

            drawing.DrawRectangle(Fill, null, bounds);
            drawing.PushClip(new RectangleGeometry(bounds));
            double interval = Math.Max(4.5, size / 3.6);
            for (double offset = -size; offset <= size * 2.0; offset += interval)
                drawing.DrawLine(hatch, new Point(offset, size), new Point(offset + size, 0));
            for (double offset = -size; offset <= size * 2.0; offset += interval * 2.0)
                drawing.DrawLine(thin, new Point(offset, 0), new Point(offset + size, size));
            drawing.Pop();
            drawing.DrawRectangle(null, edge, bounds);
        });

        // ── 모델선 캡처 ─────────────────────────────────────────────────────────
        // 강조색 ㄱ자 브래킷 두 개가 점선 범위를 집는 그림 - 이 명령이 실제로 하는 일(첫 모서리→첫 변 끝→
        // 둘째 변 끝을 ㄱ자 순서로 지정해 직사각형 범위를 만든다)을 그대로 옮긴 것이다.
        public static BitmapSource ModelLineCapture(int size) => Draw(size, drawing =>
        {
            double inner = size * 0.27;
            var box = new Rect(inner, inner, size - inner * 2, size - inner * 2);
            var dashed = new Pen(Outline, Math.Max(1.0, size / 16.0))
            {
                DashStyle = new DashStyle(new double[] { 1.8, 1.4 }, 0),
            };
            dashed.Freeze();
            drawing.DrawRectangle(Fill, dashed, box);

            double edge = size * 0.06;
            double arm = size * 0.26;
            Pen bracket = Stroke(Accent, Math.Max(1.4, size / 10.0), round: false);
            drawing.DrawLine(bracket, new Point(edge, edge + arm), new Point(edge, edge));
            drawing.DrawLine(bracket, new Point(edge, edge), new Point(edge + arm, edge));
            drawing.DrawLine(bracket, new Point(size - edge, size - edge - arm), new Point(size - edge, size - edge));
            drawing.DrawLine(bracket, new Point(size - edge, size - edge), new Point(size - edge - arm, size - edge));
        });

        // ── 패턴 타공 / 타공 복원 ───────────────────────────────────────────────
        // 같은 그림의 두 상태로 그린다(일괄결합 금지/허용과 같은 방식) - 타공은 구멍이 강조색으로 뚫려 있고,
        // 복원은 같은 자리가 도로 메워져 윤곽만 남는다. 나란히 놓인 두 작은 버튼이라 한눈에 짝으로 읽힌다.
        public static BitmapSource PatternPunch(int size, bool punched) => Draw(size, drawing =>
        {
            double margin = Margin(size);
            var box = new Rect(margin, margin, size - margin * 2, size - margin * 2);
            drawing.DrawRectangle(Fill, OutlinePen(size), box);

            double pad = box.Width * 0.16;
            double cell = (box.Width - pad * 3) / 2.0;
            Pen hole = Stroke(Accent, Math.Max(1.0, size / 16.0), round: false);
            for (int row = 0; row < 2; row++)
            {
                for (int column = 0; column < 2; column++)
                {
                    var rect = new Rect(
                        box.Left + pad + (cell + pad) * column,
                        box.Top + pad + (cell + pad) * row,
                        cell, cell);
                    if (punched) drawing.DrawRectangle(Accent, null, rect);
                    else drawing.DrawRectangle(Paper, hole, rect);
                }
            }
        });

        // ── 경고Pick ────────────────────────────────────────────────────────────
        // 경고 표지판. 이 아이콘만 강조색(스틸블루)이 아니라 테마의 Warning 색을 쓰는데, "경고"라는 뜻
        // 자체가 색에 실려 있어 다른 색으로 바꾸면 못 알아본다 - DangerButtonStyle과 같은 의도적 예외다.
        public static BitmapSource WarningPick(int size) => Draw(size, drawing =>
        {
            double margin = Math.Max(1.5, size * 0.08);
            var top = new Point(size / 2.0, margin);
            var right = new Point(size - margin, size - margin);
            var left = new Point(margin, size - margin);

            var triangle = new StreamGeometry();
            using (StreamGeometryContext ctx = triangle.Open())
            {
                ctx.BeginFigure(top, true, true);
                ctx.LineTo(right, true, true);
                ctx.LineTo(left, true, true);
            }
            triangle.Freeze();
            drawing.DrawGeometry(Warning, OutlinePen(size), triangle);

            double barWidth = Math.Max(1.2, size / 10.0);
            Pen bar = Stroke(Paper, barWidth);
            drawing.DrawLine(bar, new Point(size / 2.0, size * 0.42), new Point(size / 2.0, size * 0.66));
            drawing.DrawEllipse(Paper, null, new Point(size / 2.0, size * 0.78), barWidth / 2.0, barWidth / 2.0);
        });

        // ── 룸 구분선 ───────────────────────────────────────────────────────────
        // 벽 두 겹 사이를 지나는 강조색 중심선 - 이 기능이 하는 일(벽의 중심선에 구분선을 넣는다) 그대로.
        public static BitmapSource RoomSeparator(int size) => Draw(size, drawing =>
        {
            double margin = Margin(size);
            double bandHeight = Math.Max(2.0, size * 0.22);
            double centerY = size / 2.0;
            Pen edge = OutlinePen(size);

            drawing.DrawRectangle(Muted, edge, new Rect(
                margin, centerY - bandHeight - size * 0.06, size - margin * 2, bandHeight));
            drawing.DrawRectangle(Muted, edge, new Rect(
                margin, centerY + size * 0.06, size - margin * 2, bandHeight));

            var center = new Pen(Accent, Math.Max(1.2, size / 9.0))
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round,
                DashStyle = new DashStyle(new double[] { 2.2, 1.4 }, 0),
            };
            center.Freeze();
            drawing.DrawLine(center,
                new Point(margin * 0.6, centerY),
                new Point(size - margin * 0.6, centerY));
        });

        // ── 룸경계 ON/OFF ───────────────────────────────────────────────────────
        // 방을 둘러싼 테두리 중 한 변만 점선(=경계 꺼짐)으로 끊어 둔 모양. 룸 구분선 아이콘과 한눈에
        // 구분되도록 그쪽은 가로 띠 두 개, 이쪽은 닫힌 테두리로 대비를 줬다.
        public static BitmapSource RoomBounding(int size) => Draw(size, drawing =>
        {
            double margin = Math.Max(2.0, size * 0.14);
            double thickness = Math.Max(1.4, size / 10.0);
            Pen solid = Stroke(Outline, thickness, round: false);

            drawing.DrawRectangle(Fill, null, new Rect(margin, margin, size - margin * 2, size - margin * 2));

            double left = margin, right = size - margin, top = margin, bottom = size - margin;
            drawing.DrawLine(solid, new Point(left, top), new Point(right, top));
            drawing.DrawLine(solid, new Point(right, top), new Point(right, bottom));
            drawing.DrawLine(solid, new Point(right, bottom), new Point(left, bottom));

            var dashed = new Pen(Accent, thickness)
            {
                DashStyle = new DashStyle(new double[] { 1.6, 1.4 }, 0),
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round,
            };
            dashed.Freeze();
            drawing.DrawLine(dashed, new Point(left, bottom), new Point(left, top));
        });

        // ── 매개변수 조합 ───────────────────────────────────────────────────────
        // 왼쪽의 짧은 조각 여러 개가 화살표를 지나 오른쪽의 긴 막대 하나로 합쳐지는 그림
        // - "여러 매개변수 → 하나의 값"이라는 이 기능의 전부를 16px에서도 읽히게 단순화한 것이다.
        public static BitmapSource ParamCombine(int size) => Draw(size, drawing =>
        {
            double margin = Math.Max(1.5, size * 0.10);
            double pieceWidth = size * 0.26;
            double gap = Math.Max(1.0, size * 0.075);
            double pieceHeight = (size - margin * 2 - gap * 2) / 3.0;
            double stroke = Math.Max(1.0, size / 16.0);
            Pen piece = Stroke(Outline, stroke, round: false);

            for (int i = 0; i < 3; i++)
            {
                double top = margin + i * (pieceHeight + gap);
                drawing.DrawRectangle(Fill, piece, new Rect(margin, top, pieceWidth, pieceHeight));
            }

            double arrowLeft = margin + pieceWidth + gap;
            double arrowRight = arrowLeft + size * 0.18;
            double middle = size / 2.0;
            Pen arrow = Stroke(Accent, stroke * 1.3);
            drawing.DrawLine(arrow, new Point(arrowLeft, middle), new Point(arrowRight, middle));
            ArrowHead(drawing, arrow, new Point(arrowRight, middle), 0, size * 0.12);

            double resultLeft = arrowRight + gap;
            double resultWidth = size - margin - resultLeft;
            double resultHeight = pieceHeight * 3 + gap * 2;
            drawing.DrawRectangle(Accent, piece, new Rect(resultLeft, margin, Math.Max(2.0, resultWidth), resultHeight));
        });

        // ── 일괄결합 ────────────────────────────────────────────────────────────
        // T자로 만나는 두 부재. disallow면 세로 부재가 가로 부재에 닿지 않고 틈을 두고 끊긴 채 끝면(강조색)이
        // 막혀 있고, 허용이면 맞닿은 지점이 강조색으로 이어진다 - 리본에서 "금지/허용" 두 버튼을 아이콘만
        // 보고도 구분할 수 있게 같은 그림의 두 상태로 그린다.
        public static BitmapSource BatchJoin(int size, bool disallow) => Draw(size, drawing =>
        {
            double margin = Margin(size);
            double thickness = Math.Max(2.0, size * 0.2);
            Pen edge = OutlinePen(size);

            double horizontalTop = size - margin - thickness;
            drawing.DrawRectangle(Fill, edge, new Rect(margin, horizontalTop, size - margin * 2, thickness));

            double gap = disallow ? Math.Max(1.5, size * 0.14) : 0;
            double left = size * 0.5 - thickness / 2.0;
            double bottom = horizontalTop - gap;
            drawing.DrawRectangle(Fill, edge, new Rect(left, margin, thickness, Math.Max(2.0, bottom - margin)));

            Pen mark = Stroke(Accent, Math.Max(1.4, size / 9.0));
            double y = disallow ? bottom : horizontalTop;
            drawing.DrawLine(mark,
                new Point(left - size * 0.06, y),
                new Point(left + thickness + size * 0.06, y));
        });
    }
}
