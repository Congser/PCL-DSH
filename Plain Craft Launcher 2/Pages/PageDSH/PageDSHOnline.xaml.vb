Imports System.Windows.Media

''' <summary>
''' 启动页右栏 —— 模型接入方式（官方 API + 任意多个自定义网关）。
'''
''' 两块内容：
'''   1. <b>接入方式列表</b>：配置"可以用哪些 API"。官方固定在第一位、不可删；
'''      自定义网关可以加任意多个。每种接入方式有**自己的密钥**。
'''   2. <b>各实例的接入方式</b>：给每个实例挑一个 provider（下拉框直接选）。
'''
''' 配置落到哪里（关键，别搞混）：
'''   · provider 列表（不含密钥）→ <c>DSH_ROOT/providers.json</c>
'''   · 端点与协议 → 实例的 <c>settings.yaml</c> 的 <c>llm-deepseek</c> 分节
'''     （<see cref="DshSettings"/>，<c>watch: true</c> → 改完立刻生效，不用重启）
'''   · 密钥       → 实例的 <c>.credentials.yaml</c> 的 <c>refs</c> 段
'''     （<see cref="DshCredentials"/>，ref 名 = provider 的 KeyRef）
'''
''' ⚠️ **不要**改成写 <c>cordis.patch.yml</c>：patch 是"按 id 替换整个 config"，
''' 粒度更粗，而且早期版本写的文件名 <c>pcl-dsh.patch.yml</c> 根本不会被 dsh 读取。
''' </summary>
Public Class PageDSHOnline
    Implements IRefreshable

#Region "状态"

    Private IsLoad As Boolean = False

    ''' <summary>测试 / 保存期间为 True。</summary>
    Private IsBusy As Boolean = False

    ''' <summary>当前的接入方式列表（含官方）。</summary>
    Private Providers As List(Of DshApiConfig.DshProvider) = Nothing

    ''' <summary>程序化改写实例下拉框时置位，避免把回填当成用户选择。</summary>
    Private IsUpdatingInstanceCombo As Boolean = False

    ''' <summary>实例行 → 它的 provider 下拉框。</summary>
    Private ReadOnly InstanceCombos As New Dictionary(Of String, MyComboBox)(StringComparer.OrdinalIgnoreCase)

#End Region

#Region "初始化"

    Private Sub PageDSHOnline_Loaded(sender As Object, e As RoutedEventArgs) Handles Me.Loaded
        If Not IsLoad Then
            IsLoad = True
            AddHandler ModDSH.DshInstancesChanged, AddressOf OnInstancesChanged
            AddHandler ModDSH.DshInstanceStateChanged, AddressOf OnInstancesChanged
        End If
        Providers = DshApiConfig.LoadProviders()
        RefreshAll()
    End Sub

    Public Sub PageOnEnterHook() Handles Me.PageEnter
        Providers = DshApiConfig.LoadProviders()
        RefreshAll()
    End Sub

    Private Sub OnInstancesChanged()
        RefreshInstances()
    End Sub

#End Region

#Region "刷新"

    Private Sub RefreshAll()
        RefreshProviders()
        RefreshInstances()
    End Sub

    ''' <summary>重建接入方式列表。</summary>
    Private Sub RefreshProviders()
        Try
            PanProviders.Children.Clear()

            For Each provider In Providers
                PanProviders.Children.Add(BuildProviderRow(provider))
            Next
        Catch ex As Exception
            Logger.Error(ex, "DSH：构建接入方式列表失败")
        End Try
    End Sub

    ''' <summary>构建一个 provider 的卡片行（行内直接编辑，不用弹对话框）。</summary>
    Private Function BuildProviderRow(provider As DshApiConfig.DshProvider) As FrameworkElement
        Dim card As New Border With {
            .BorderThickness = New Thickness(1),
            .CornerRadius = New CornerRadius(4),
            .Padding = New Thickness(14, 12, 14, 12),
            .Margin = New Thickness(0, 0, 0, 10)
        }
        card.SetResourceReference(Border.BorderBrushProperty, "ColorBrushGray5")

        Dim root As New StackPanel()

        '── 标题行：名字 + 类型徽标 ──
        Dim titleRow As New Grid()
        titleRow.ColumnDefinitions.Add(New ColumnDefinition With {.Width = New GridLength(1, GridUnitType.Star)})
        titleRow.ColumnDefinitions.Add(New ColumnDefinition With {.Width = GridLength.Auto})

        Dim nameText As New TextBlock With {
            .FontSize = 14, .FontWeight = FontWeights.Bold, .Text = provider.DisplayLabel,
            .TextTrimming = TextTrimming.CharacterEllipsis, .VerticalAlignment = VerticalAlignment.Center
        }
        nameText.SetResourceReference(TextBlock.ForegroundProperty, "ColorBrush1")
        Grid.SetColumn(nameText, 0)
        titleRow.Children.Add(nameText)

        Dim badge As New TextBlock With {
            .FontSize = 11.5, .Text = If(provider.IsOfficial, "官方", "自定义"),
            .VerticalAlignment = VerticalAlignment.Center, .Margin = New Thickness(10, 0, 0, 0)
        }
        badge.SetResourceReference(TextBlock.ForegroundProperty, "ColorBrushGray4")
        Grid.SetColumn(badge, 1)
        titleRow.Children.Add(badge)

        root.Children.Add(titleRow)

        '── 自定义网关：可编辑字段 ──
        If Not provider.IsOfficial Then
            Dim captured As DshApiConfig.DshProvider = provider

            root.Children.Add(MakeFieldLabel("名称"))
            Dim nameBox As New MyTextBox With {.Height = 30, .Text = provider.Name}
            nameBox.HintText = "给这个网关起个名字"
            root.Children.Add(nameBox)

            root.Children.Add(MakeFieldLabel("API 地址"))
            Dim urlBox As New MyTextBox With {.Height = 30, .Text = provider.BaseUrl}
            urlBox.HintText = "https://your-gateway.example.com"
            root.Children.Add(urlBox)

            Dim pairGrid As New Grid With {.Margin = New Thickness(0, 10, 0, 0)}
            pairGrid.ColumnDefinitions.Add(New ColumnDefinition With {.Width = New GridLength(1, GridUnitType.Star)})
            pairGrid.ColumnDefinitions.Add(New ColumnDefinition With {.Width = New GridLength(12)})
            pairGrid.ColumnDefinitions.Add(New ColumnDefinition With {.Width = New GridLength(1, GridUnitType.Star)})

            Dim protoPanel As New StackPanel()
            protoPanel.Children.Add(MakeFieldLabel("协议"))
            Dim combo As New MyComboBox()
            combo.Items.Add(New MyComboBoxItem With {
                .Content = "chat-completions（OpenAI 风格）", .Tag = DshApiConfig.ProtocolChat
            })
            combo.Items.Add(New MyComboBoxItem With {
                .Content = "messages（Anthropic 风格）", .Tag = DshApiConfig.ProtocolMessages
            })
            SelectProtocolInCombo(combo, provider.Protocol)
            protoPanel.Children.Add(combo)
            Grid.SetColumn(protoPanel, 0)
            pairGrid.Children.Add(protoPanel)

            Dim modelPanel As New StackPanel()
            modelPanel.Children.Add(MakeFieldLabel("模型名（可留空）"))
            Dim modelBox As New MyTextBox With {.Height = 30, .Text = provider.Model}
            modelBox.HintText = "留空则沿用内置模型目录"
            modelPanel.Children.Add(modelBox)
            Grid.SetColumn(modelPanel, 2)
            pairGrid.Children.Add(modelPanel)

            root.Children.Add(pairGrid)

            Dim saveRow As New StackPanel With {.Orientation = Orientation.Horizontal, .Margin = New Thickness(0, 12, 0, 0)}
            saveRow.Children.Add(MakeButton("保存修改", True, Sub()
                                                              SaveProviderEdits(captured, nameBox.Text, urlBox.Text,
                                                                                GetProtocolFromCombo(combo), modelBox.Text)
                                                          End Sub))
            saveRow.Children.Add(MakeButton("测试连接", False, Sub()
                                                              TestProvider(captured, nameBox.Text, urlBox.Text,
                                                                           GetProtocolFromCombo(combo))
                                                          End Sub))
            saveRow.Children.Add(MakeButton("删除", False, Sub() DeleteProvider(captured)))
            root.Children.Add(saveRow)
        End If

        '── 密钥状态 + 密钥操作 ──
        Dim key As String = DshCredentials.GetAnyKey(provider.KeyRef)
        Dim keyText As New TextBlock With {
            .FontSize = 12.5, .Margin = New Thickness(0, 10, 0, 0), .TextWrapping = TextWrapping.Wrap
        }
        If String.IsNullOrWhiteSpace(key) Then
            keyText.Text = "密钥：未配置"
            keyText.Foreground = New SolidColorBrush(Color.FromRgb(&HDF, &H50, &H40))
        Else
            keyText.Text = $"密钥：已配置 {DshCredentials.MaskKey(key)}　（凭据名 {provider.KeyRef}）"
            keyText.Foreground = New SolidColorBrush(Color.FromRgb(&H2E, &HA0, &H55))
        End If
        root.Children.Add(keyText)

        Dim keyRow As New StackPanel With {.Orientation = Orientation.Horizontal, .Margin = New Thickness(0, 8, 0, 0)}
        keyRow.Children.Add(MakeButton("设置密钥", False, Sub() SetProviderKey(provider)))
        If Not String.IsNullOrWhiteSpace(key) Then
            keyRow.Children.Add(MakeButton("清除密钥", False, Sub() ClearProviderKey(provider)))
        End If
        root.Children.Add(keyRow)

        card.Child = root
        Return card
    End Function

    Private Function MakeFieldLabel(Text As String) As FrameworkElement
        Dim tb As New TextBlock With {
            .FontSize = 12, .Text = Text, .Margin = New Thickness(0, 10, 0, 5), .Opacity = 0.7
        }
        tb.SetResourceReference(TextBlock.ForegroundProperty, "ColorBrush1")
        Return tb
    End Function

    Private Function MakeButton(Text As String, Highlight As Boolean, Action As Action) As FrameworkElement
        Dim btn As New MyButton With {.Text = Text, .Height = 28, .Margin = New Thickness(0, 0, 8, 0)}
        btn.TextPadding = New Thickness(12, 0, 12, 0)
        If Highlight Then btn.ColorType = MyButton.ColorState.Highlight
        Dim captured As Action = Action
        AddHandler btn.Click, Sub(s, e) captured()
        Return btn
    End Function

    ''' <summary>重建实例列表（每个实例一个 provider 下拉框）。</summary>
    Private Sub RefreshInstances()
        Try
            PanInstances.Children.Clear()
            InstanceCombos.Clear()

            For Each inst In ModDSH.DshInstances
                PanInstances.Children.Add(BuildInstanceRow(inst))
            Next
        Catch ex As Exception
            Logger.Error(ex, "DSH：构建实例接入方式列表失败")
        End Try
    End Sub

    Private Function BuildInstanceRow(inst As DshInstance) As FrameworkElement
        Dim current As DshApiConfig.DshProvider = DshApiConfig.GetInstanceProvider(inst, Providers)

        Dim card As New Border With {
            .BorderThickness = New Thickness(1),
            .CornerRadius = New CornerRadius(4),
            .Padding = New Thickness(14, 12, 14, 12),
            .Margin = New Thickness(0, 0, 0, 10)
        }
        card.SetResourceReference(Border.BorderBrushProperty, "ColorBrushGray5")

        Dim root As New StackPanel()

        Dim nameText As New TextBlock With {
            .FontSize = 14, .FontWeight = FontWeights.Bold, .Text = inst.DisplayName,
            .TextTrimming = TextTrimming.CharacterEllipsis
        }
        nameText.SetResourceReference(TextBlock.ForegroundProperty, "ColorBrush1")
        root.Children.Add(nameText)

        root.Children.Add(MakeFieldLabel("接入方式"))

        Dim combo As New MyComboBox()
        For Each p In Providers
            combo.Items.Add(New MyComboBoxItem With {.Content = p.DisplayLabel, .Tag = p.Id})
        Next
        'settings.yaml 被手改过、认不出凭据名时，临时补一项如实显示
        If current IsNot Nothing AndAlso current.Id = "unknown" Then
            combo.Items.Add(New MyComboBoxItem With {.Content = current.DisplayLabel, .Tag = "unknown"})
        End If

        '先选好初始项，**再**挂事件 —— 顺序反了会把这次回填当成用户操作
        IsUpdatingInstanceCombo = True
        Try
            SelectProviderInCombo(combo, current)
        Finally
            IsUpdatingInstanceCombo = False
        End Try

        '⚠️ 必须挂 SelectionChanged，不能挂 TextChanged：
        '   MyComboBox 的 TextChanged 只在 IsEditable 为真时才从内部文本框转发出来，
        '   这里的下拉框是不可编辑的，挂 TextChanged 会永远不触发。
        AddHandler combo.SelectionChanged, Sub(s, e) OnInstanceComboChanged(inst)
        InstanceCombos(inst.Id) = combo
        root.Children.Add(combo)

        '密钥状态（当前 provider 的那一把）
        Dim keyRef As String = If(current Is Nothing, DshApiConfig.KeyRefOfficial, current.KeyRef)
        Dim key As String = DshCredentials.GetRef(keyRef, inst.HomeDir)
        Dim isShared As Boolean = DshCredentials.IsKeyShared(keyRef, inst)

        Dim keyText As New TextBlock With {
            .FontSize = 12.5, .Margin = New Thickness(0, 10, 0, 0), .TextWrapping = TextWrapping.Wrap
        }
        If String.IsNullOrWhiteSpace(key) Then
            keyText.Text = $"密钥：未配置（凭据名 {keyRef}）"
            keyText.Foreground = New SolidColorBrush(Color.FromRgb(&HDF, &H50, &H40))
        Else
            keyText.Text = $"密钥：{DshCredentials.MaskKey(key)}　（{(If(isShared, "共享", "单独设置"))}）"
            keyText.Foreground = If(isShared,
                                    TryFindResource("ColorBrushGray2"),
                                    New SolidColorBrush(Color.FromRgb(&H13, &H70, &HF3)))
        End If
        root.Children.Add(keyText)

        Dim pathText As New TextBlock With {
            .FontSize = 11, .Margin = New Thickness(0, 4, 0, 0),
            .Text = $"{keyRef} · {inst.CredentialsFile}", .TextTrimming = TextTrimming.CharacterEllipsis
        }
        pathText.SetResourceReference(TextBlock.ForegroundProperty, "ColorBrushGray4")
        root.Children.Add(pathText)

        Dim btnRow As New StackPanel With {.Orientation = Orientation.Horizontal, .Margin = New Thickness(0, 10, 0, 0)}
        btnRow.Children.Add(MakeButton("单独设置密钥", False, Sub() SetInstanceKey(inst)))
        If Not isShared AndAlso Not String.IsNullOrWhiteSpace(key) Then
            btnRow.Children.Add(MakeButton("恢复共享", False, Sub() RestoreSharedKey(inst)))
        End If
        If Not String.IsNullOrWhiteSpace(key) Then
            btnRow.Children.Add(MakeButton("清除密钥", False, Sub() ClearInstanceKey(inst)))
        End If
        root.Children.Add(btnRow)

        card.Child = root
        Return card
    End Function

    Public Sub Refresh() Implements IRefreshable.Refresh
        Providers = DshApiConfig.LoadProviders()
        RefreshAll()
    End Sub

#End Region

#Region "下拉框辅助"

    Private Sub SelectProtocolInCombo(Combo As MyComboBox, ProtocolName As String)
        Dim want As String =
            If(String.Equals(ProtocolName, DshApiConfig.ProtocolMessages, StringComparison.OrdinalIgnoreCase),
               DshApiConfig.ProtocolMessages, DshApiConfig.ProtocolChat)
        For Each obj In Combo.Items
            Dim ci = CType(obj, MyComboBoxItem)
            If String.Equals(CStr(ci.Tag), want, StringComparison.OrdinalIgnoreCase) Then
                Combo.SelectedItem = ci
                Return
            End If
        Next
        If Combo.Items.Count > 0 Then Combo.SelectedItem = Combo.Items(0)
    End Sub

    Private Function GetProtocolFromCombo(Combo As MyComboBox) As String
        Dim item = TryCast(Combo.SelectedItem, MyComboBoxItem)
        If item Is Nothing Then Return DshApiConfig.ProtocolChat
        Return CStr(item.Tag)
    End Function

    Private Sub SelectProviderInCombo(Combo As MyComboBox, Provider As DshApiConfig.DshProvider)
        Dim wantId As String = If(Provider Is Nothing, DshApiConfig.OfficialProviderId, Provider.Id)
        For Each obj In Combo.Items
            Dim ci = CType(obj, MyComboBoxItem)
            If String.Equals(CStr(ci.Tag), wantId, StringComparison.OrdinalIgnoreCase) Then
                Combo.SelectedItem = ci
                Return
            End If
        Next
        If Combo.Items.Count > 0 Then Combo.SelectedItem = Combo.Items(0)
    End Sub

    ''' <summary>用户在实例行里换了接入方式。</summary>
    Private Sub OnInstanceComboChanged(inst As DshInstance)
        If IsUpdatingInstanceCombo OrElse IsBusy Then Return

        Dim combo As MyComboBox = Nothing
        If Not InstanceCombos.TryGetValue(inst.Id, combo) Then Return

        Dim item = TryCast(combo.SelectedItem, MyComboBoxItem)
        If item Is Nothing Then Return
        Dim providerId As String = CStr(item.Tag)

        '认不出来的那一项是只读展示，选它不做事
        If String.Equals(providerId, "unknown", StringComparison.OrdinalIgnoreCase) Then Return

        Dim target As DshApiConfig.DshProvider = DshApiConfig.GetProvider(Providers, providerId)
        If target Is Nothing Then Return

        Dim current As DshApiConfig.DshProvider = DshApiConfig.GetInstanceProvider(inst, Providers)
        If current IsNot Nothing AndAlso String.Equals(current.Id, target.Id, StringComparison.OrdinalIgnoreCase) Then Return

        Dim reason As String = Nothing
        If DshApiConfig.ApplyToInstance(inst, target, reason) Then
            RefreshInstances()
            Hint($"「{inst.DisplayName}」的接入方式已改为 {target.DisplayLabel}。", HintType.Green)
        Else
            RefreshInstances()
            Hint($"切换失败：{reason}", HintType.Red)
        End If
    End Sub

#End Region

#Region "接入方式的增删改"

    Private Sub BtnAddProvider_Click(sender As Object, e As MouseButtonEventArgs) Handles BtnAddProvider.Click
        If IsBusy Then Return
        Try
            Dim newProvider As New DshApiConfig.DshProvider With {
                .Id = DshApiConfig.NewProviderId(),
                .Name = $"自定义网关 {Providers.Count}",
                .BaseUrl = "",
                .Protocol = DshApiConfig.ProtocolChat,
                .Model = "",
                .ContextWindow = 128000
            }
            Providers.Add(newProvider)
            DshApiConfig.SaveProviders(Providers)
            RefreshProviders()
            Hint("已添加一个自定义网关。填好地址与密钥后，在下面给实例选择它即可。", HintType.Green)
        Catch ex As Exception
            Logger.Error(ex, "DSH：添加接入方式失败")
            Hint($"添加失败：{ex.Message}", HintType.Red)
        End Try
    End Sub

    ''' <summary>保存某个 provider 的编辑（并同步到正在使用它的实例）。</summary>
    Private Sub SaveProviderEdits(provider As DshApiConfig.DshProvider,
                                  Name As String, Url As String, ProtocolName As String, Model As String)
        If IsBusy OrElse provider Is Nothing Then Return
        Try
            Dim trimmedUrl As String = If(Url, "").Trim()
            If trimmedUrl.Length > 0 AndAlso Not trimmedUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase) Then
                trimmedUrl = "https://" & trimmedUrl
            End If

            provider.Name = If(String.IsNullOrWhiteSpace(Name), "未命名网关", Name.Trim())
            provider.BaseUrl = trimmedUrl.TrimEnd("/"c)
            provider.Protocol = ProtocolName
            provider.Model = If(Model, "").Trim()

            DshApiConfig.SaveProviders(Providers)

            '凡是正在用这个 provider 的实例，都要把新的端点/协议推下去
            Dim updated As Integer = 0
            Dim failed As New List(Of String)
            For Each inst In ModDSH.DshInstances
                Dim cur As DshApiConfig.DshProvider = DshApiConfig.GetInstanceProvider(inst, Providers)
                If cur Is Nothing OrElse Not String.Equals(cur.Id, provider.Id, StringComparison.OrdinalIgnoreCase) Then Continue For
                Dim reason As String = Nothing
                If DshApiConfig.ApplyToInstance(inst, provider, reason) Then
                    updated += 1
                Else
                    failed.Add($"{inst.DisplayName}（{reason}）")
                End If
            Next

            RefreshProviders()
            RefreshInstances()

            If failed.Count > 0 Then
                Hint($"已保存，但以下实例应用失败：{String.Join("；", failed)}", HintType.Red)
            ElseIf updated > 0 Then
                Hint($"已保存，并更新了 {updated} 个正在使用它的实例。", HintType.Green)
            Else
                Hint("已保存。目前没有实例在使用它。", HintType.Green)
            End If
        Catch ex As Exception
            Logger.Error(ex, "DSH：保存接入方式失败")
            Hint($"保存失败：{ex.Message}", HintType.Red)
        End Try
    End Sub

    Private Sub DeleteProvider(provider As DshApiConfig.DshProvider)
        If IsBusy OrElse provider Is Nothing Then Return
        If provider.IsOfficial Then
            Hint("官方 API 不能删除。", HintType.Red)
            Return
        End If

        '统计有多少实例正在用它
        Dim users As New List(Of DshInstance)
        For Each inst In ModDSH.DshInstances
            Dim cur As DshApiConfig.DshProvider = DshApiConfig.GetInstanceProvider(inst, Providers)
            If cur IsNot Nothing AndAlso String.Equals(cur.Id, provider.Id, StringComparison.OrdinalIgnoreCase) Then
                users.Add(inst)
            End If
        Next

        Dim msg As New StringBuilder()
        msg.AppendLine($"删除接入方式「{provider.DisplayLabel}」？")
        msg.AppendLine()
        If users.Count > 0 Then
            msg.AppendLine($"有 {users.Count} 个实例正在使用它，删除后会自动切回官方 API：")
            For Each u In users
                msg.AppendLine($"  · {u.DisplayName}")
            Next
        Else
            msg.AppendLine("目前没有实例在使用它。")
        End If
        msg.AppendLine()
        msg.Append("密钥也会一并从所有实例的凭据文件里删除。")

        If MyMsgBox(msg.ToString(), "删除接入方式", "删除", "取消", IsWarn:=True) = 2 Then Return

        Try
            '先把使用者切回官方，避免实例指向一个不存在的 provider
            Dim official As DshApiConfig.DshProvider = DshApiConfig.GetProvider(Providers, DshApiConfig.OfficialProviderId)
            For Each u In users
                Dim reason As String = Nothing
                DshApiConfig.ApplyToInstance(u, official, reason)
            Next

            DshCredentials.ClearKeyFromAllInstances(provider.KeyRef)

            Providers.RemoveAll(Function(p) String.Equals(p.Id, provider.Id, StringComparison.OrdinalIgnoreCase))
            DshApiConfig.SaveProviders(Providers)

            RefreshProviders()
            RefreshInstances()
            Hint($"已删除「{provider.DisplayLabel}」。", HintType.Green)
        Catch ex As Exception
            Logger.Error(ex, "DSH：删除接入方式失败")
            Hint($"删除失败：{ex.Message}", HintType.Red)
        End Try
    End Sub

#End Region

#Region "密钥"

    ''' <summary>给某个接入方式设置密钥（写入所有实例）。</summary>
    Private Sub SetProviderKey(provider As DshApiConfig.DshProvider)
        If IsBusy OrElse provider Is Nothing Then Return

        Dim current As String = DshCredentials.GetAnyKey(provider.KeyRef)
        Dim input As String = MyMsgBoxInput(
            $"设置「{provider.DisplayLabel}」的密钥",
            "密钥会写入所有实例的凭据文件，这样任何实例都能用它。" & vbCrLf &
            $"凭据名：{provider.KeyRef}",
            If(String.IsNullOrWhiteSpace(current), "", current),
            HintText:="API Key")

        '取消返回 Nothing
        If input Is Nothing Then Return
        If String.IsNullOrWhiteSpace(input) Then
            Hint("密钥不能为空。要删除请点「清除密钥」。", HintType.Red)
            Return
        End If

        Dim problem As String = DshCredentials.ValidateApiKeyShape(input)
        If problem IsNot Nothing Then
            Hint(problem, HintType.Red)
            Return
        End If

        Try
            Dim failed As List(Of String) = Nothing
            Dim ok As Integer = DshCredentials.SyncKeyToAllInstances(provider.KeyRef, input.Trim(), failed)
            RefreshAll()

            If failed IsNot Nothing AndAlso failed.Count > 0 Then
                Hint($"已写入 {ok} 个实例，以下失败：{String.Join("、", failed)}", HintType.Red)
            Else
                Hint($"密钥已保存到 {ok} 个实例。", HintType.Green)
            End If
        Catch ex As Exception
            Logger.Error(ex, "DSH：保存接入方式密钥失败")
            Hint($"保存失败：{ex.Message}", HintType.Red)
        End Try
    End Sub

    Private Sub ClearProviderKey(provider As DshApiConfig.DshProvider)
        If IsBusy OrElse provider Is Nothing Then Return

        If MyMsgBox($"清除「{provider.DisplayLabel}」的密钥？" & vbCrLf & vbCrLf &
                    "会从所有实例的凭据文件里删掉它。使用这个接入方式的实例将无法调用模型。",
                    "清除密钥", "清除", "取消") = 2 Then
            Return
        End If

        Try
            Dim count As Integer = DshCredentials.ClearKeyFromAllInstances(provider.KeyRef)
            RefreshAll()
            Hint($"已从 {count} 个实例清除密钥。", HintType.Green)
        Catch ex As Exception
            Logger.Error(ex, "DSH：清除接入方式密钥失败")
            Hint($"清除失败：{ex.Message}", HintType.Red)
        End Try
    End Sub

    ''' <summary>给单个实例单独设置密钥（只影响这一个实例）。</summary>
    Private Sub SetInstanceKey(inst As DshInstance)
        If inst Is Nothing Then Return

        Dim provider As DshApiConfig.DshProvider = DshApiConfig.GetInstanceProvider(inst, Providers)
        Dim keyRef As String = If(provider Is Nothing, DshApiConfig.KeyRefOfficial, provider.KeyRef)
        Dim current As String = DshCredentials.GetRef(keyRef, inst.HomeDir)

        Dim input As String = MyMsgBoxInput(
            $"为「{inst.DisplayName}」单独设置密钥",
            $"这会只改写这个实例的凭据文件，其它实例不受影响。" & vbCrLf &
            $"接入方式：{If(provider Is Nothing, "官方 API", provider.DisplayLabel)}" & vbCrLf &
            $"凭据名：{keyRef}" & vbCrLf & vbCrLf &
            $"文件位置：{inst.CredentialsFile}",
            If(String.IsNullOrWhiteSpace(current), "", current),
            HintText:="API Key")

        If input Is Nothing Then Return
        If String.IsNullOrWhiteSpace(input) Then
            Hint("密钥不能为空。要删除请点「清除密钥」。", HintType.Red)
            Return
        End If

        Dim problem As String = DshCredentials.ValidateApiKeyShape(input)
        If problem IsNot Nothing Then
            Hint(problem, HintType.Red)
            Return
        End If

        Try
            DshCredentials.SetRef(keyRef, input.Trim(), inst.HomeDir)
            RefreshInstances()
            Hint($"已为「{inst.DisplayName}」单独设置密钥。", HintType.Green)
        Catch ex As Exception
            Logger.Error(ex, "DSH：为实例设置密钥失败")
            Hint($"设置失败：{ex.Message}", HintType.Red)
        End Try
    End Sub

    Private Sub RestoreSharedKey(inst As DshInstance)
        If inst Is Nothing Then Return

        Dim provider As DshApiConfig.DshProvider = DshApiConfig.GetInstanceProvider(inst, Providers)
        Dim keyRef As String = If(provider Is Nothing, DshApiConfig.KeyRefOfficial, provider.KeyRef)
        Dim sharedKey As String = DshCredentials.GetSharedKey(keyRef)

        If String.IsNullOrWhiteSpace(sharedKey) Then
            Hint("主实例上还没有这个接入方式的密钥，请先在上面设置一把。", HintType.Red)
            Return
        End If

        Try
            DshCredentials.SetRef(keyRef, sharedKey, inst.HomeDir)
            RefreshInstances()
            Hint($"「{inst.DisplayName}」已恢复为共享密钥。", HintType.Green)
        Catch ex As Exception
            Logger.Error(ex, "DSH：恢复共享密钥失败")
            Hint($"恢复失败：{ex.Message}", HintType.Red)
        End Try
    End Sub

    Private Sub ClearInstanceKey(inst As DshInstance)
        If inst Is Nothing Then Return

        Dim provider As DshApiConfig.DshProvider = DshApiConfig.GetInstanceProvider(inst, Providers)
        Dim keyRef As String = If(provider Is Nothing, DshApiConfig.KeyRefOfficial, provider.KeyRef)

        If MyMsgBox($"清除「{inst.DisplayName}」的密钥？" & vbCrLf & vbCrLf &
                    $"凭据名：{keyRef}" & vbCrLf &
                    "只影响这一个实例，其它实例不受影响。",
                    "清除密钥", "清除", "取消") = 2 Then
            Return
        End If

        Try
            DshCredentials.UnsetRef(keyRef, inst.HomeDir)
            RefreshInstances()
            Hint($"已清除「{inst.DisplayName}」的密钥。", HintType.Green)
        Catch ex As Exception
            Logger.Error(ex, "DSH：清除实例密钥失败")
            Hint($"清除失败：{ex.Message}", HintType.Red)
        End Try
    End Sub

#End Region

#Region "连接测试"

    ''' <summary>
    ''' 测试一个接入方式。
    ''' </summary>
    ''' <param name="Provider">要测的 provider（可能还没保存编辑，所以地址从参数来）。</param>
    ''' <param name="Name">界面上的名字（仅用于提示文案）。</param>
    ''' <param name="Url">界面上的地址。</param>
    ''' <param name="ProtocolName">界面上的协议。</param>
    Private Sub TestProvider(Provider As DshApiConfig.DshProvider,
                             Name As String, Url As String, ProtocolName As String)
        If IsBusy OrElse Provider Is Nothing Then Return

        Dim targetUrl As String = If(Url, "").Trim()
        If targetUrl.Length = 0 Then
            Hint("请先填写 API 地址。", HintType.Red)
            Return
        End If
        If Not targetUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase) Then
            targetUrl = "https://" & targetUrl
        End If

        Dim key As String = DshCredentials.GetAnyKey(Provider.KeyRef)
        If String.IsNullOrWhiteSpace(key) Then
            Hint("请先点「设置密钥」填一把密钥，否则测试没有意义。", HintType.Red)
            Return
        End If

        Dim label As String = If(String.IsNullOrWhiteSpace(Name), Provider.DisplayLabel, Name.Trim())
        SetBusy(True, $"正在测试 {label} 连接……")

        RunInNewThread(
            Sub()
                Dim r As DshApiConfig.ApiProbeResult = Nothing
                Dim err As String = Nothing
                Try
                    r = DshApiConfig.TestConnection(targetUrl, ProtocolName, key)
                Catch ex As Exception
                    err = ex.Message
                    Logger.Error(ex, "DSH：连接测试失败")
                End Try

                RunInUi(
                    Sub()
                        SetBusy(False)
                        If r Is Nothing Then
                            Hint($"测试失败：{If(err, "未知原因")}", HintType.Red)
                            Return
                        End If

                        If r.Ok Then
                            Hint($"{label} 连接成功（{r.LatencyMs} ms）。", HintType.Green)
                        Else
                            Hint($"{label} 连接失败：{r.Detail}", HintType.Red)
                            MyMsgBox(
                                $"测试「{label}」失败。{vbCrLf}{vbCrLf}{r.Detail}{vbCrLf}{vbCrLf}" &
                                $"探测地址：{r.Endpoint}",
                                "连接测试", "知道了", IsWarn:=True)
                        End If
                    End Sub)
            End Sub, "DSH 测试接入方式")
    End Sub

#End Region

#Region "界面辅助"

    Private Sub SetBusy(Busy As Boolean, Optional Hint As String = Nothing)
        IsBusy = Busy
        Try
            PanLoad.Visibility = Busy.ToVisibility
            If Busy Then
                LoadStart.State.LoadingState = MyLoading.MyLoadingState.Run
                If Hint IsNot Nothing Then LoadHintText.Text = Hint
            Else
                LoadStart.State.LoadingState = MyLoading.MyLoadingState.Stop
            End If

            BtnAddProvider.IsEnabled = Not Busy
            BtnRefresh.IsEnabled = Not Busy
        Catch ex As Exception
            Logger.Error(ex, "DSH：切换忙碌状态失败")
        End Try
    End Sub

#End Region

End Class
