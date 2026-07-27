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
            _ => "",
        };

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
                default:
                    canvas.Children.Add(new Ellipse { Width = 12, Height = 12, Fill = brush });
                    Canvas.SetLeft(canvas.Children[0], 4);
                    Canvas.SetTop(canvas.Children[0], 2);
                    break;
            }

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
        // 도형 종류(Rectangle/Polygon/Line/Ellipse)에 맞춰 Fill 또는 Stroke를 갱신한다.
        public static void SetBrush(Canvas canvas, Brush brush)
        {
            foreach (UIElement child in canvas.Children)
            {
                switch (child)
                {
                    case Rectangle rect: rect.Fill = brush; break;
                    case Polygon poly: poly.Fill = brush; break;
                    case Line line: line.Stroke = brush; break;
                    case Ellipse ellipse: ellipse.Fill = brush; break;
                }
            }
        }
    }
}
