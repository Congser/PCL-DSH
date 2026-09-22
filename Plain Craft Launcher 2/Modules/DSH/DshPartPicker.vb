''' <summary>
''' PCL_DSH：「选择要处理的项」勾选对话框（**导入导出共用**）。
''' </summary>
''' <remarks>
''' ═══════════════════════════════════════════════════════════════════════
''' 为什么需要它
''' ═══════════════════════════════════════════════════════════════════════
''' 「导入环境」和「导出环境」都需要让用户**勾选要处理哪几类内容**。
''' PCL 自带的 <c>MyMsgBox</c> 只有按钮、<c>MyMsgBoxSelect</c> 是单选，
''' 都不支持多选，所以这里自己做一个。
'''
''' ⚠️ **必须两边共用同一个对话框** —— 用户明确要求
''' 「导入与导出在可选项、交互方式和格式上保持一致」。
''' 如果各写一个，迟早会漂移成"导入能选、导出不能选"。
'''
''' ═══════════════════════════════════════════════════════════════════════
''' 与 PCL 观感的一致性
''' ═══════════════════════════════════════════════════════════════════════
''' 纯代码构造（不新建 XAML），配色跟随 PCL 的浅色主题 ——
''' 与 <c>PageSetupLaunch</c> 里的 <c>DshMaintenanceProgress</c> 同一套做法。
''' </remarks>
Public Class DshPartPicker
    Inherits Window

#Region "数据模型"

    ''' <summary>一个可勾选项。</summary>
    Public Class DshPickItem
        ''' <summary>稳定标识（调用方用它判断选了什么）。</summary>
        Public Property Key As String = ""
        ''' <summary>显示名。</summary>
        Public Property Title As String = ""
        ''' <summary>副标题（说明这一项包含什么）。</summary>
        Public Property Description As String = ""
        ''' <summary>右侧的统计文字（"3 个插件" / "12.5 MB"）。</summary>
        Public Property Detail As String = ""
        ''' <summary>默认是否勾选。</summary>
        Public Property DefaultChecked As Boolean = True
        ''' <summary>是否可勾（没有内容时为 False，显示为灰色）。</summary>
        Public Property Enabled As Boolean = True
        ''' <summary>勾选它是否需要额外警告（比如含密钥）。</summary>
        Public Property WarnOnCheck As String = ""
    End Class

#End Region

#Region "字段"

    Private ReadOnly CheckBoxes As New List(Of (Item As DshPickItem, Box As CheckBox))()
    Private ReadOnly NoteText As TextBlock

#End Region

#Region "构造"

    ''' <summary>
    ''' 构造对话框。
    ''' </summary>
    ''' <param name="Title">窗口标题。</param>
    ''' <param name="Intro">顶部说明文字。</param>
    ''' <param name="Items">可勾选项。</param>
    ''' <param name="ConfirmText">确认按钮文字。</param>
    ''' <param name="Warning">底部警告文字（可选，红色显示）。</param>
    Public Sub New(Title As String, Intro As String, Items As List(Of DshPickItem),
                   Optional ConfirmText As String = "确定",
                   Optional Warning As String = Nothing)
        Me.Title = Title
        Me.Width = 560
        Me.SizeToContent = SizeToContent.Height
        Me.MaxHeight = 720
        Me.WindowStartupLocation = WindowStartupLocation.CenterScreen
        Me.ResizeMode = ResizeMode.NoResize
        Me.WindowStyle = WindowStyle.ToolWindow
        Me.ShowInTaskbar = True
        Me.Background = New SolidColorBrush(Color.FromRgb(&HF2, &HF2, &HF2))

        Dim root As New StackPanel With {.Margin = New Thickness(0)}

        ' ── 顶部说明 ──
        If Not String.IsNullOrWhiteSpace(Intro) Then
            Dim introText As New TextBlock With {
                .Text = Intro,
                .FontSize = 12.5,
                .TextWrapping = TextWrapping.Wrap,
                .LineHeight = 20,
                .Margin = New Thickness(20, 18, 20, 4),
                .Foreground = New SolidColorBrush(Color.FromRgb(&H55, &H55, &H55))
            }
            root.Children.Add(introText)
        End If

        ' ── 勾选项 ──
        Dim listPanel As New StackPanel With {.Margin = New Thickness(20, 8, 20, 0)}
        For Each item In Items
            Dim box As New CheckBox With {
                .IsChecked = item.DefaultChecked AndAlso item.Enabled,
                .IsEnabled = item.Enabled,
                .Margin = New Thickness(0, 6, 0, 6),
                .VerticalContentAlignment = VerticalAlignment.Top
            }

            Dim row As New StackPanel With {.Margin = New Thickness(6, 0, 0, 0)}
            Dim titleRow As New StackPanel With {.Orientation = Orientation.Horizontal}
            Dim titleText As New TextBlock With {
                .Text = item.Title,
                .FontSize = 13,
                .FontWeight = FontWeights.SemiBold,
                .Foreground = New SolidColorBrush(
                    If(item.Enabled, Color.FromRgb(&H22, &H22, &H22), Color.FromRgb(&HAA, &HAA, &HAA)))
            }
            titleRow.Children.Add(titleText)

            If Not String.IsNullOrWhiteSpace(item.Detail) Then
                Dim detailText As New TextBlock With {
                    .Text = item.Detail,
                    .FontSize = 11.5,
                    .Margin = New Thickness(10, 1, 0, 0),
                    .Foreground = New SolidColorBrush(Color.FromRgb(&H88, &H88, &H88))
                }
                titleRow.Children.Add(detailText)
            End If
            row.Children.Add(titleRow)

            If Not String.IsNullOrWhiteSpace(item.Description) Then
                Dim descText As New TextBlock With {
                    .Text = item.Description,
                    .FontSize = 11.5,
                    .TextWrapping = TextWrapping.Wrap,
                    .LineHeight = 18,
                    .Margin = New Thickness(0, 3, 0, 0),
                    .Foreground = New SolidColorBrush(Color.FromRgb(&H88, &H88, &H88))
                }
                row.Children.Add(descText)
            End If

            box.Content = row
            CheckBoxes.Add((item, box))
            listPanel.Children.Add(box)
        Next
        root.Children.Add(listPanel)

        ' ── 警告文字 ──
        NoteText = New TextBlock With {
            .FontSize = 12,
            .TextWrapping = TextWrapping.Wrap,
            .LineHeight = 19,
            .Margin = New Thickness(20, 12, 20, 0),
            .Foreground = New SolidColorBrush(Color.FromRgb(&HC0, &H30, &H30))
        }
        root.Children.Add(NoteText)

        ' ── 按钮 ──
        Dim btnRow As New StackPanel With {
            .Orientation = Orientation.Horizontal,
            .HorizontalAlignment = HorizontalAlignment.Right,
            .Margin = New Thickness(20, 18, 20, 18)
        }
        Dim btnOk As New Button With {
            .Content = ConfirmText,
            .MinWidth = 100,
            .Height = 30,
            .Margin = New Thickness(0, 0, 10, 0),
            .IsDefault = True
        }
        Dim btnCancel As New Button With {
            .Content = "取消",
            .MinWidth = 80,
            .Height = 30,
            .IsCancel = True
        }
        AddHandler btnOk.Click, Sub() Me.DialogResult = True
        AddHandler btnCancel.Click, Sub() Me.DialogResult = False
        btnRow.Children.Add(btnOk)
        btnRow.Children.Add(btnCancel)
        root.Children.Add(btnRow)

        Me.Content = root

        ' ── 勾选敏感项时实时提示 ──
        For Each pair In CheckBoxes
            Dim captured As DshPickItem = pair.Item
            AddHandler pair.Box.Checked, Sub() UpdateNote(Warning)
            AddHandler pair.Box.Unchecked, Sub() UpdateNote(Warning)
        Next
        UpdateNote(Warning)
    End Sub

#End Region

#Region "交互"

    ''' <summary>刷新底部提示（勾了敏感项就显示警告）。</summary>
    Private Sub UpdateNote(StaticWarning As String)
        Try
            Dim warns As New List(Of String)
            For Each pair In CheckBoxes
                If pair.Box.IsChecked = True AndAlso Not String.IsNullOrWhiteSpace(pair.Item.WarnOnCheck) Then
                    warns.Add(pair.Item.WarnOnCheck)
                End If
            Next
            If warns.Count > 0 Then
                NoteText.Text = "⚠ " & String.Join(vbCrLf & "⚠ ", warns)
                NoteText.Visibility = Visibility.Visible
            ElseIf Not String.IsNullOrWhiteSpace(StaticWarning) Then
                NoteText.Text = StaticWarning
                NoteText.Visibility = Visibility.Visible
            Else
                NoteText.Text = ""
                NoteText.Visibility = Visibility.Collapsed
            End If
        Catch ex As Exception
            Logger.Warn(ex, "DSH：刷新勾选提示失败（可忽略）")
        End Try
    End Sub

    ''' <summary>取回用户勾选的项（按原顺序）。</summary>
    Public Function PickedKeys() As List(Of String)
        Dim result As New List(Of String)
        For Each pair In CheckBoxes
            If pair.Box.IsChecked = True AndAlso pair.Item.Enabled Then
                result.Add(pair.Item.Key)
            End If
        Next
        Return result
    End Function

#End Region

End Class
