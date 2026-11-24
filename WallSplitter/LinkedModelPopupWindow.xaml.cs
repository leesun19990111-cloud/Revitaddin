using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Autodesk.Revit.DB;
// Autodesk.Revit.DB에도 View가 있고 WPF 쪽 타입과 이름이 겹치는 것들이 있어, DB 쪽은 필요한 것만
// 쓰고 겹치는 이름은 완전한 이름으로 적는다(이 프로젝트의 다른 창들과 같은 방침).
using RevitView = Autodesk.Revit.DB.View;
// Autodesk.Revit.DB.Grid(데이텀 그리드선)와 WPF의 Grid가 겹치는 이 프로젝트의 단골 충돌 - CLAUDE.md의
// 절대 규칙대로 WPF 쪽에 별칭을 준다(QuickToggleSettingsWindow.xaml.cs와 같은 방식).
using WpfGrid = System.Windows.Controls.Grid;

namespace WallSplitter
{
    // "링크된 모델" 버튼을 클릭하면 뜨는 링크 목록 패널 (2026-09-02 추가, 사용자 요청 - "링크된 모델도
    // 개별 링크로 끄고 켤 수 있게 해줘"). 색상 버튼 패널(ColorToolPopupWindow)과 같은 모드리스 팝업
    // 구조이며, 줄을 누를 때마다 ExternalEvent로 활성 뷰에 즉시 반영한다("확인" 버튼 없음).
    //
    // 링크된 CAD 도면 버튼과 달리 팝업을 쓰는 이유: Revit은 링크된 모델을 개별 카테고리로 나누지 않고
    // 전부 "Revit 링크" 카테고리 하나에 묶으므로 카테고리 숨기기로는 전부 함께 끄는 것밖에 안 된다.
    // 개별 제어는 요소 단위 숨기기라 "어느 링크인지" 고를 UI가 반드시 필요하다
    // (QuickToggleService.LinkedModelsInView/SetLinkedModelsVisible 참고).
    public partial class LinkedModelPopupWindow : Window
    {
        // 이 팝업이 지금 보여주고 있는 뷰 - 링크 표시 여부는 뷰마다 다르므로, 툴바가 뷰 전환을 감지하면
        // 이 값을 보고 목록을 새 뷰 기준으로 다시 그린다(매 Idling 틱마다 다시 그리지 않기 위한 판별용).
        public int CurrentViewId { get; private set; }

        // 팝업을 연 문서 경로 - 팝업을 열어둔 채 다른 프로젝트로 전환한 경우 잘못된 문서의 같은 정수
        // ElementId에 적용되는 사고를 막기 위해 요청에 같이 실어 보낸다(핸들러가 대조한다).
        private readonly string _sourceDocumentPath;

        public LinkedModelPopupWindow(RevitView view, QuickToggleButtonConfig cfg)
        {
            InitializeComponent();

            _sourceDocumentPath = view.Document.PathName ?? "";
            TitleText.Text = cfg.Name;
            HintText.Text = "이 뷰의 링크된 모델입니다. 줄을 누르면 그 링크만 켜고 끕니다.";
            HintText.Foreground = Theme.TextSecondary;

            Button showAllButton = new Button { Content = "전체 켜기", Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0, 0, 6, 0) };
            showAllButton.Click += (s, e) => SendAll(visible: true);
            Button hideAllButton = new Button { Content = "전체 끄기", Padding = new Thickness(10, 4, 10, 4) };
            hideAllButton.Click += (s, e) => SendAll(visible: false);
            AllButtonsPanel.Children.Add(showAllButton);
            AllButtonsPanel.Children.Add(hideAllButton);

            Refresh(view);
        }

        // 목록을 지금의 뷰 상태로 다시 그린다 - 팝업을 처음 열 때, 적용이 끝난 뒤(핸들러가 호출), 뷰가
        // 바뀌었을 때 호출된다. ExternalEvent가 비동기라 요청을 보낸 직후가 아니라 "실제로 반영된 뒤"에
        // 다시 읽어야 화면과 모델이 어긋나지 않는다.
        public void Refresh(RevitView view)
        {
            CurrentViewId = view.Id.ToInt();
            _links = QuickToggleService.LinkedModelsInView(view);

            LinkListPanel.Children.Clear();

            if (_links.Count == 0)
            {
                LinkListPanel.Children.Add(new TextBlock
                {
                    Text = "이 뷰에서 끄고 켤 수 있는 링크된 모델이 없습니다.",
                    Foreground = Theme.TextSecondary,
                    TextWrapping = TextWrapping.Wrap,
                });
                return;
            }

            foreach (QuickToggleService.LinkedModelInfo link in _links)
            {
                int instanceId = link.InstanceId;
                bool visible = link.Visible;

                // 켜짐/꺼짐을 색으로도 구분한다 - 툴바 버튼과 같은 시각 언어(켜진 것만 채워진 색).
                WpfGrid row = new WpfGrid();
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                TextBlock nameText = new TextBlock
                {
                    Text = link.Name,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Foreground = visible ? Theme.TextPrimary : Theme.TextSecondary,
                    Margin = new Thickness(0, 0, 8, 0),
                };
                WpfGrid.SetColumn(nameText, 0);
                row.Children.Add(nameText);

                Border stateChip = new Border
                {
                    Background = visible ? Theme.ToggleOn : Brushes.Transparent,
                    BorderBrush = visible ? Theme.ToggleOn : Theme.Border,
                    BorderThickness = new Thickness(1),
                    Padding = new Thickness(8, 2, 8, 2),
                    Child = new TextBlock
                    {
                        Text = visible ? "켜짐" : "꺼짐",
                        FontSize = 11,
                        Foreground = visible ? Theme.OnAccent : Theme.TextSecondary,
                    },
                };
                WpfGrid.SetColumn(stateChip, 1);
                row.Children.Add(stateChip);

                Button rowButton = new Button
                {
                    Content = row,
                    Padding = new Thickness(8, 5, 8, 5),
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    ToolTip = link.Name + (visible ? " - 클릭하면 이 뷰에서 끕니다" : " - 클릭하면 이 뷰에 다시 표시합니다"),
                };
                rowButton.Click += (s, e) => Send(new List<int> { instanceId }, !visible);
                LinkListPanel.Children.Add(rowButton);
            }
        }

        private List<QuickToggleService.LinkedModelInfo> _links = new List<QuickToggleService.LinkedModelInfo>();

        private void SendAll(bool visible) => Send(_links.Select(l => l.InstanceId).ToList(), visible);

        // 어느 뷰에 적용할지는 여기서 정하지 않는다 - 색상 버튼과 마찬가지로
        // QuickToggleExternalEventHandler가 실행되는 그 순간의 활성 뷰에 적용한다.
        private void Send(List<int> instanceIds, bool visible)
        {
            if (instanceIds.Count == 0) return;
            if (App.QuickToggleHandler == null || App.QuickToggleEvent == null) return;

            App.QuickToggleHandler.PendingLinkedModelApply = new LinkedModelApplyRequest
            {
                SourceDocumentPath = _sourceDocumentPath,
                InstanceIds = instanceIds,
                Visible = visible,
            };
            App.QuickToggleEvent.Raise();
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
    }
}
