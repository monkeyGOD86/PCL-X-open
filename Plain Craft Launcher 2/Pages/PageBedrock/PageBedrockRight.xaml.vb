Imports PCLCS.Bedrock

Public Class PageBedrockRight
    Private ReadOnly Service As New BedrockService(New WindowsBedrockPackageCatalog(), New WindowsBedrockActivator())
    Private HasLoaded As Boolean
    Private IsBusy As Boolean

    Private Async Sub PageLoaded(sender As Object, e As RoutedEventArgs) Handles Me.Loaded
        If HasLoaded Then Return
        HasLoaded = True
        Await RefreshInstallations()
    End Sub

    Private Async Sub RefreshClicked(sender As Object, e As EventArgs) Handles BtnRefresh.Click
        Await RefreshInstallations()
    End Sub

    Private Async Function RefreshInstallations() As Task
        If IsBusy Then Return
        SetBusy(True)
        LabStatus.Text = "正在检查当前 Windows 用户的安装…"
        Try
            Dim Result = Await Task.Run(Function() Service.Detect())
            ComboInstall.Items.Clear()
            LabDetails.Text = ""
            If Result.Error IsNot Nothing Then
                LabStatus.Text = "无法读取游戏安装状态：" & Result.Error
            ElseIf Result.Installations.Count = 0 Then
                LabStatus.Text = "未发现受支持的基岩版安装。请通过 Microsoft Store 或 Xbox 应用安装游戏后刷新。"
            Else
                For Each Installation In Result.Installations
                    ComboInstall.Items.Add(New MyComboBoxItem With {
                        .Content = Installation.Channel & " · " & Installation.Version,
                        .Tag = Installation
                    })
                Next
                ComboInstall.SelectedIndex = 0
                LabStatus.Text = "已发现 " & Result.Installations.Count & " 个已注册的游戏安装。"
            End If
        Catch ex As Exception
            LabStatus.Text = "检查安装状态失败：" & ex.Message
            Logger.Warn(ex, "检测基岩版安装失败")
        Finally
            SetBusy(False)
        End Try
    End Function

    Private Sub InstallSelectionChanged(sender As Object, e As SelectionChangedEventArgs) Handles ComboInstall.SelectionChanged
        Dim Item = TryCast(ComboInstall.SelectedItem, MyComboBoxItem)
        Dim Installation = TryCast(Item?.Tag, BedrockInstallation)
        If Installation Is Nothing Then
            LabDetails.Text = ""
        Else
            LabDetails.Text = $"包版本：{Installation.Version}　架构：{Installation.Architecture}" & vbCrLf &
                              $"安装类型：{Installation.RuntimeEvidence}　应用：{If(Installation.CanLaunch, Installation.AppUserModelId, "未知")}" & vbCrLf &
                              $"Gaming Services：{Installation.GamingServices}（不代表服务运行正常）" & vbCrLf &
                              "游戏授权由 Windows 和游戏处理。"
        End If
        BtnStart.IsEnabled = Not IsBusy AndAlso Installation IsNot Nothing AndAlso Installation.CanLaunch
    End Sub

    Private Async Sub StartClicked(sender As Object, e As EventArgs) Handles BtnStart.Click
        If IsBusy Then Return
        Dim Item = TryCast(ComboInstall.SelectedItem, MyComboBoxItem)
        Dim Installation = TryCast(Item?.Tag, BedrockInstallation)
        If Installation Is Nothing Then Return
        SetBusy(True)
        LabStatus.Text = "正在向 Windows 提交启动请求…"
        Try
            Dim Result = Await Task.Run(Function() Service.Launch(Installation))
            If Result.Accepted Then
                LabStatus.Text = "Windows 已接受启动请求。游戏可能仍需完成登录或授权。"
            Else
                LabStatus.Text = "启动请求失败：" & Result.Error
            End If
        Catch ex As Exception
            LabStatus.Text = "启动请求失败：" & ex.Message
            Logger.Warn(ex, "启动基岩版失败")
        Finally
            SetBusy(False)
        End Try
    End Sub

    Private Sub AddonClicked(sender As Object, e As EventArgs) Handles BtnAddons.Click
        Dim Dialog As New Microsoft.Win32.OpenFileDialog With {
            .Filter = "Minecraft 附加包 (*.mcpack;*.mcaddon)|*.mcpack;*.mcaddon",
            .CheckFileExists = True,
            .Multiselect = False
        }
        If Dialog.ShowDialog() = True Then PreviewPackage(Dialog.FileName)
    End Sub

    Public Async Sub PreviewPackage(FilePath As String)
        If IsBusy Then Return
        SetBusy(True)
        LabPackInfo.Text = "正在检查附加包…"
        Try
            Dim Packs = Await Task.Run(Function() BedrockPackInspector.Inspect(FilePath))
            Dim Lines As New List(Of String)
            For Each Pack In Packs
                Lines.Add($"{Pack.Name}　{Pack.Version}" & vbCrLf &
                          $"UUID：{Pack.Uuid}　类型：{String.Join(", ", Pack.Modules)}" & vbCrLf &
                          If(Pack.Dependencies.Count > 0, "依赖：" & String.Join(", ", Pack.Dependencies) & vbCrLf, "") &
                          If(Pack.MinEngineVersion IsNot Nothing, "最低游戏版本：" & Pack.MinEngineVersion & vbCrLf, ""))
            Next
            LabPackInfo.Text = String.Join(vbCrLf, Lines) & vbCrLf & "已完成只读预览；导入功能尚待验证。"
        Catch ex As Exception
            LabPackInfo.Text = "无法预览附加包：" & ex.Message
            Logger.Warn(ex, "基岩版附加包预览失败")
        Finally
            SetBusy(False)
        End Try
    End Sub

    Private Sub SetBusy(Value As Boolean)
        IsBusy = Value
        BtnRefresh.IsEnabled = Not Value
        BtnAddons.IsEnabled = Not Value
        ComboInstall.IsEnabled = Not Value AndAlso ComboInstall.Items.Count > 0
        Dim Item = TryCast(ComboInstall.SelectedItem, MyComboBoxItem)
        Dim Installation = TryCast(Item?.Tag, BedrockInstallation)
        BtnStart.IsEnabled = Not Value AndAlso Installation IsNot Nothing AndAlso Installation.CanLaunch
    End Sub
End Class
