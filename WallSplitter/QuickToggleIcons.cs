using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace WallSplitter
{
    // 빠른 토글 버튼 하나가 화면에 그리는 아이콘 모양. 기존엔 카테고리(뷰템플릿/필터/작업세트)에 따라
    // 자동으로 하나씩만 정해졌는데(Layers/Funnel/Lines), 사용자가 버튼마다 직접 아이콘/색을 고를 수 있게
    // 해달라는 요청(2026-07-27)으로 몇 가지를 더 추가하고 선택 가능하게 뺐다.
    public enum QuickToggleIconShape
    {
        Layers,
        Funnel,
        Lines,
        Star,
        Flag,
        Dot,
        // 2026-07-27, "아이콘 갯수를 늘려달라(눈/꽃 모양 등)"는 요청으로 추가.
        Eye,
        Flower,
        Heart,
        Bolt,
        // 2026-09-02, 링크된 도면/링크된 모델 버튼(QuickToggleCategory.LinkedCad/LinkedModel)의 기본
        // 아이콘으로 추가 - 기존 도형만으로는 "도면"과 "모델"을 형태로 구분할 수가 없었다.
        Sheet,
        Cube,
    }

    // QuickToggleToolbar(실제 툴바 렌더링)와 QuickToggleSettingsWindow(설정 창의 아이콘 선택 미리보기)가
    // 똑같은 그리기 로직을 공유해야 해서(하나만 고치고 다른 하나를 깜빡하면 미리보기와 실제 모습이 어긋남)
    // 이 프로젝트의 "작은 UI 패턴은 각 창에 복제" 관례와 달리 여기는 별도 공유 클래스로 뺐다.
    internal static class QuickToggleIcons
    {
        // 사용자가 아이콘을 직접 고르지 않은(IconShape==null) 기존 버튼을 위한 카테고리별 기본값 -
        // 이 매핑이 곧 이 기능이 추가되기 전의 기존 동작이었다.
        public static QuickToggleIconShape DefaultFor(QuickToggleCategory category) => category switch
        {
            QuickToggleCategory.ViewTemplate => QuickToggleIconShape.Layers,
            QuickToggleCategory.Filter => QuickToggleIconShape.Funnel,
            QuickToggleCategory.Workset => QuickToggleIconShape.Lines,
            QuickToggleCategory.ColorTool => QuickToggleIconShape.Dot,
            QuickToggleCategory.CommandLauncher => QuickToggleIconShape.Bolt,
            QuickToggleCategory.LinkedCad => QuickToggleIconShape.Sheet,
            QuickToggleCategory.LinkedModel => QuickToggleIconShape.Cube,
            _ => QuickToggleIconShape.Dot,
        };

        public static string LabelFor(QuickToggleIconShape shape) => shape switch
        {
            QuickToggleIconShape.Layers => "레이어",
            QuickToggleIconShape.Funnel => "깔때기",
            QuickToggleIconShape.Lines => "리스트",
            QuickToggleIconShape.Star => "별",
            QuickToggleIconShape.Flag => "깃발",
            QuickToggleIconShape.Dot => "점",
            QuickToggleIconShape.Eye => "눈",
            QuickToggleIconShape.Flower => "꽃",
            QuickToggleIconShape.Heart => "하트",
            QuickToggleIconShape.Bolt => "번개",
            QuickToggleIconShape.Sheet => "도면",
            QuickToggleIconShape.Cube => "상자",
            _ => "",
        };

        // 2026-07-27, "버튼 색상을 설정하면 배경 자체가 바뀌고, 아이콘/텍스트는 그 배경에 대비되어 잘
        // 보이는 색으로 자동 전환"되어야 한다는 요청으로 추가 - QuickToggleToolbar가 On 상태 버튼의
        // 배경을 사용자 지정(또는 기본) 색으로 채울 때, 그 위에 얹을 아이콘/라벨 색을 이걸로 고른다.
        // 상대 휘도(perceived luminance) 근사식으로 밝은 배경엔 어두운 글자를, 어두운 배경엔 밝은
        // 글자를 선택한다 - WCAG 권장 공식만큼 정밀하진 않지만 이 정도 팔레트(8가지 프리셋)에서는
        // 육안으로 항상 뚜렷하게 갈렸다.
        public static Brush ContrastingForeground(Color background)
        {
            double luminance = 0.299 * background.R + 0.587 * background.G + 0.114 * background.B;
            return luminance > 150 ? Theme.TextPrimary : Theme.OnAccent;
        }

        public static Canvas Create(QuickToggleIconShape shape, Brush brush)
        {
            Canvas canvas = new Canvas { Width = 20, Height = 16, HorizontalAlignment = HorizontalAlignment.Center };

            switch (shape)
            {
                case QuickToggleIconShape.Layers:
                    // 겹쳐진 3개의 가로 막대 - 레이어(뷰템플릿)를 은유
                    for (int i = 0; i < 3; i++)
                    {
                        canvas.Children.Add(new Rectangle
                        {
                            Width = 20 - i * 4,
                            Height = 3,
                            Fill = brush,
                            RadiusX = 1,
                            RadiusY = 1,
                        });
                        Canvas.SetLeft(canvas.Children[i], i * 2);
                        Canvas.SetTop(canvas.Children[i], i * 5.5);
                    }
                    break;

                case QuickToggleIconShape.Funnel:
                    // 깔때기(필터) 모양
                    canvas.Children.Add(new Polygon
                    {
                        Points = new PointCollection
                        {
                            new Point(0, 0), new Point(20, 0), new Point(12, 9), new Point(12, 16),
                            new Point(8, 16), new Point(8, 9),
                        },
                        Fill = brush,
                    });
                    break;

                case QuickToggleIconShape.Lines:
                    // 3줄 리스트 - 작업세트 묶음을 은유
                    for (int i = 0; i < 3; i++)
                    {
                        canvas.Children.Add(new Line
                        {
                            X1 = 0, Y1 = i * 6 + 2, X2 = 20, Y2 = i * 6 + 2,
                            Stroke = brush,
                            StrokeThickness = 2.5,
                        });
                    }
                    break;

                case QuickToggleIconShape.Star:
                    canvas.Children.Add(new Polygon { Points = StarPoints(centerX: 10, centerY: 8, outerR: 8, innerR: 3.4), Fill = brush });
                    break;

                case QuickToggleIconShape.Flag:
                    canvas.Children.Add(new Line { X1 = 2, Y1 = 0, X2 = 2, Y2 = 16, Stroke = brush, StrokeThickness = 2 });
                    canvas.Children.Add(new Polygon
                    {
                        Points = new PointCollection { new Point(2, 1), new Point(18, 4), new Point(2, 8) },
                        Fill = brush,
                    });
                    break;

                case QuickToggleIconShape.Dot:
                    canvas.Children.Add(new Ellipse { Width = 12, Height = 12, Fill = brush });
                    Canvas.SetLeft(canvas.Children[0], 4);
                    Canvas.SetTop(canvas.Children[0], 2);
                    break;

                case QuickToggleIconShape.Eye:
                {
                    // 아몬드형 눈 바깥선(위/아래 베지어 곡선)에서 동공(원)을 오려낸 모양 -
                    // GeometryGroup(EvenOdd)로 큰 도형에서 작은 원을 뺀다.
                    PathFigure outline = new PathFigure { StartPoint = new Point(0, 8), IsClosed = true };
                    outline.Segments.Add(new QuadraticBezierSegment(new Point(10, -2), new Point(20, 8), true));
                    outline.Segments.Add(new QuadraticBezierSegment(new Point(10, 18), new Point(0, 8), true));
                    PathGeometry almond = new PathGeometry();
                    almond.Figures.Add(outline);
                    EllipseGeometry pupil = new EllipseGeometry(new Point(10, 8), 3, 3);
                    GeometryGroup eyeGroup = new GeometryGroup { FillRule = FillRule.EvenOdd };
                    eyeGroup.Children.Add(almond);
                    eyeGroup.Children.Add(pupil);
                    canvas.Children.Add(new Path { Data = eyeGroup, Fill = brush });
                    break;
                }

                case QuickToggleIconShape.Flower:
                    // 중심에서 6방향으로 뻗는 꽃잎(타원) - RenderTransformOrigin으로 각 타원의
                    // 아래쪽 끝을 중심점에 고정해두고 회전시켜 방사형으로 배치한다.
                    for (int i = 0; i < 6; i++)
                    {
                        Ellipse petal = new Ellipse
                        {
                            Width = 6,
                            Height = 8,
                            Fill = brush,
                            RenderTransformOrigin = new Point(0.5, 1.0),
                            RenderTransform = new RotateTransform(i * 60),
                        };
                        Canvas.SetLeft(petal, 10 - 3);
                        Canvas.SetTop(petal, 8 - 8);
                        canvas.Children.Add(petal);
                    }
                    break;

                case QuickToggleIconShape.Heart:
                {
                    // 원 2개(윗부분 두 봉우리) + 삼각형(아랫부분 뾰족한 끝)을 합쳐서 하트 모양을 만든다.
                    EllipseGeometry lobeLeft = new EllipseGeometry(new Point(6, 5), 4.5, 4.5);
                    EllipseGeometry lobeRight = new EllipseGeometry(new Point(14, 5), 4.5, 4.5);
                    PathFigure tip = new PathFigure { StartPoint = new Point(1.5, 6.5), IsClosed = true };
                    tip.Segments.Add(new LineSegment(new Point(10, 16), true));
                    tip.Segments.Add(new LineSegment(new Point(18.5, 6.5), true));
                    PathGeometry tipGeometry = new PathGeometry();
                    tipGeometry.Figures.Add(tip);
                    GeometryGroup heartGroup = new GeometryGroup { FillRule = FillRule.Nonzero };
                    heartGroup.Children.Add(lobeLeft);
                    heartGroup.Children.Add(lobeRight);
                    heartGroup.Children.Add(tipGeometry);
                    canvas.Children.Add(new Path { Data = heartGroup, Fill = brush });
                    break;
                }

                case QuickToggleIconShape.Bolt:
                    canvas.Children.Add(new Polygon
                    {
                        Points = new PointCollection
                        {
                            new Point(11, 0), new Point(3, 9), new Point(9, 9),
                            new Point(7, 16), new Point(17, 6), new Point(11, 6),
                        },
                        Fill = brush,
                    });
                    break;

                case QuickToggleIconShape.Sheet:
                {
                    // 도면 한 장 - 바깥 테두리 + 오른쪽 아래 표제란. 채우지 않고 선으로만 그린다
                    // (채우면 안쪽 표제란 선이 같은 색에 묻혀 안 보인다).
                    Rectangle frame = new Rectangle { Width = 14, Height = 16, Stroke = brush, StrokeThickness = 1.8 };
                    Canvas.SetLeft(frame, 3);
                    Canvas.SetTop(frame, 0);
                    canvas.Children.Add(frame);
                    canvas.Children.Add(new Line { X1 = 3, Y1 = 11, X2 = 17, Y2 = 11, Stroke = brush, StrokeThickness = 1.6 });
                    canvas.Children.Add(new Line { X1 = 10, Y1 = 11, X2 = 10, Y2 = 16, Stroke = brush, StrokeThickness = 1.6 });
                    break;
                }

                case QuickToggleIconShape.Cube:
                {
                    // 아이소메트릭 정육면체(6각형 윤곽 + 가운데에서 세 방향으로 뻗는 모서리) - 링크된
                    // "모델"을 은유. 도면 아이콘과 마찬가지로 선으로만 그린다.
                    canvas.Children.Add(new Polygon
                    {
                        Points = new PointCollection
                        {
                            new Point(10, 0.6), new Point(18.4, 4.8), new Point(18.4, 11.2),
                            new Point(10, 15.4), new Point(1.6, 11.2), new Point(1.6, 4.8),
                        },
                        Stroke = brush,
                        StrokeThickness = 1.8,
                        StrokeLineJoin = PenLineJoin.Round,
                    });
                    canvas.Children.Add(new Line { X1 = 10, Y1 = 8, X2 = 1.6, Y2 = 4.8, Stroke = brush, StrokeThickness = 1.6 });
                    canvas.Children.Add(new Line { X1 = 10, Y1 = 8, X2 = 18.4, Y2 = 4.8, Stroke = brush, StrokeThickness = 1.6 });
                    canvas.Children.Add(new Line { X1 = 10, Y1 = 8, X2 = 10, Y2 = 15.4, Stroke = brush, StrokeThickness = 1.6 });
                    break;
                }

                default:
                    goto case QuickToggleIconShape.Dot;
            }

            return canvas;
        }

        // "뷰 저장"/"되돌리기" 고정 버튼(2026-07-27 추가)이 쓰는 아이콘 - 사용자가 고르는 QuickToggleIconShape
        // 목록에는 넣지 않는다(이 두 버튼은 등록형 버튼이 아니라 항상 고정으로 붙는 전용 기능이라서).
        public static Canvas CreateBookmarkIcon(Brush brush)
        {
            // 책갈피 리본 모양 - 위는 사각형, 아래는 V자로 파인 삼각 노치.
            Canvas canvas = new Canvas { Width = 20, Height = 16, HorizontalAlignment = HorizontalAlignment.Center };
            canvas.Children.Add(new Polygon
            {
                Points = new PointCollection
                {
                    new Point(4, 0), new Point(16, 0), new Point(16, 16), new Point(10, 11), new Point(4, 16),
                },
                Fill = brush,
            });
            return canvas;
        }

        public static Canvas CreateUndoIcon(Brush brush)
        {
            // 반시계 방향으로 크게 도는 호 + 왼쪽 위를 향하는 화살촉 - 표준적인 "되돌리기" 아이콘 모양.
            Canvas canvas = new Canvas { Width = 20, Height = 16, HorizontalAlignment = HorizontalAlignment.Center };

            PathFigure arcFigure = new PathFigure { StartPoint = new Point(17, 11), IsClosed = false };
            arcFigure.Segments.Add(new ArcSegment(new Point(6, 3), new Size(6.5, 6.5), 0, true, SweepDirection.Clockwise, true));
            PathGeometry arcGeometry = new PathGeometry();
            arcGeometry.Figures.Add(arcFigure);
            canvas.Children.Add(new Path
            {
                Data = arcGeometry,
                Stroke = brush,
                StrokeThickness = 2.2,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
            });

            canvas.Children.Add(new Polygon
            {
                Points = new PointCollection { new Point(2, 0), new Point(10, 2), new Point(3, 8) },
                Fill = brush,
            });

            return canvas;
        }

        private static PointCollection StarPoints(double centerX, double centerY, double outerR, double innerR)
        {
            PointCollection points = new PointCollection();
            for (int i = 0; i < 10; i++)
            {
                double angle = Math.PI / 5 * i - Math.PI / 2;
                double r = i % 2 == 0 ? outerR : innerR;
                points.Add(new Point(centerX + r * Math.Cos(angle), centerY + r * Math.Sin(angle)));
            }
            return points;
        }

        // UpdateButtonStates가 상태만 바뀌었을 때 아이콘 색을 다시 칠하기 위한 헬퍼 - Create가 만드는
        // 도형 종류(Rectangle/Polygon/Line/Ellipse/Path)에 맞춰 Fill 또는 Stroke를 갱신한다.
        // 2026-09-02: 도면/상자 아이콘처럼 채우지 않고 선으로만 그리는 도형이 생겨서, 원래 칠해져 있던
        // 쪽(Fill 또는 Stroke)만 갱신하도록 바꿨다 - 무조건 Fill을 대입하면 선 그림 아이콘이 통째로
        // 칠해진 덩어리가 되어 버린다.
        public static void SetBrush(Canvas canvas, Brush brush)
        {
            foreach (UIElement child in canvas.Children)
            {
                if (child is not Shape shape) continue;
                if (shape.Fill != null) shape.Fill = brush;
                if (shape.Stroke != null) shape.Stroke = brush;
            }
        }
    }
}
