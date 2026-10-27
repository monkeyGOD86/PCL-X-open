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
        BtnWorlds.IsEnabled = Not IsBusy AndAlso Installation IsNot Nothing
        If CardWorlds IsNot Nothing Then CardWorlds.Visibility = Visibility.Collapsed
    End Sub

    Private Async Sub WorldsClicked(sender As Object, e As EventArgs) Handles BtnWorlds.Click
        Dim Item = TryCast(ComboInstall.SelectedItem, MyComboBoxItem)
        Dim Installation = TryCast(Item?.Tag, BedrockInstallation)
        If IsBusy OrElse Installation Is Nothing Then Return
        CardWorlds.Visibility = Visibility.Visible
        Dim ScrollOperation = Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, New Action(Sub() PanBack.ScrollToBottom()))
        ComboRoots.Items.Clear()
        ComboWorlds.Items.Clear()
        LabWorldStatus.Text = ""
        LabWorldDetails.Text = ""
        LabRootsStatus.Text = "正在查找候选数据目录…"
        SetBusy(True)
        Try
            Dim Roots = Await Task.Run(Function() (New BedrockDataRootFinder()).Find(Installation))
            For Each Root In Roots
                ComboRoots.Items.Add(New MyComboBoxItem With {.Content = Root.Source & "：" & Root.Path, .Tag = Root})
            Next
            LabRootsStatus.Text = If(Roots.Count = 0,
                "未发现可访问的数据目录。",
                "请确认并选择属于目标游戏账户的数据目录；启动器无法自动确认游戏账户。")
        Catch ex As Exception
            LabRootsStatus.Text = "查找数据目录失败：" & ex.Message
            Logger.Warn(ex, "查找基岩版数据目录失败")
        Finally
            SetBusy(False)
        End Try
    End Sub

    Private Sub RootSelectionChanged(sender As Object, e As SelectionChangedEventArgs) Handles ComboRoots.SelectionChanged
        Dim Item = TryCast(ComboRoots.SelectedItem, MyComboBoxItem)
        Dim Root = TryCast(Item?.Tag, BedrockDataRoot)
        BtnReadWorlds.IsEnabled = Not IsBusy AndAlso Root IsNot Nothing AndAlso Root.SupportsWorlds
        BtnRestore.IsEnabled = Not IsBusy AndAlso Root IsNot Nothing AndAlso Root.SupportsWorlds
        ComboWorlds.Items.Clear()
        LabWorldStatus.Text = If(Root IsNot Nothing AndAlso Not Root.SupportsWorlds, "共享内容目录不用于管理账户世界。", "")
        LabWorldDetails.Text = ""
    End Sub

    Private Async Sub ReadWorldsClicked(sender As Object, e As EventArgs) Handles BtnReadWorlds.Click
        Dim Item = TryCast(ComboRoots.SelectedItem, MyComboBoxItem)
        Dim Root = TryCast(Item?.Tag, BedrockDataRoot)
        If IsBusy OrElse Root Is Nothing Then Return
        ComboWorlds.Items.Clear()
        LabWorldStatus.Text = "正在读取世界列表…"
        LabWorldDetails.Text = ""
        SetBusy(True)
        Try
            Dim Worlds = Await Task.Run(Function() BedrockWorldReader.Read(Root))
            For Each World In Worlds
                ComboWorlds.Items.Add(New MyComboBoxItem With {.Content = World.Name, .Tag = World})
            Next
            LabWorldStatus.Text = If(Worlds.Count = 0, "此目录中没有发现本地世界。", $"发现 {Worlds.Count} 个世界，请选择以查看附加包。")
        Catch ex As Exception
            LabWorldStatus.Text = "读取世界失败：" & ex.Message
            Logger.Warn(ex, "读取基岩版世界失败")
        Finally
            SetBusy(False)
        End Try
    End Sub

    Private Sub WorldSelectionChanged(sender As Object, e As SelectionChangedEventArgs) Handles ComboWorlds.SelectionChanged
        Dim Item = TryCast(ComboWorlds.SelectedItem, MyComboBoxItem)
        Dim World = TryCast(Item?.Tag, BedrockWorldInfo)
        BtnBackup.IsEnabled = Not IsBusy AndAlso World IsNot Nothing AndAlso World.HasLevelData
        If World Is Nothing Then
            LabWorldDetails.Text = ""
            Return
        End If
        If World.Warning IsNot Nothing Then
            LabWorldDetails.Text = World.Warning
            Return
        End If
        Dim ResourceLines = If(World.ResourcePacks.Count = 0, "无", String.Join(vbCrLf, World.ResourcePacks))
        Dim BehaviorLines = If(World.BehaviorPacks.Count = 0, "无", String.Join(vbCrLf, World.BehaviorPacks))
        LabWorldDetails.Text = $"资源包：{ResourceLines}" & vbCrLf & $"行为包：{BehaviorLines}" & vbCrLf &
                               If(World.HasLevelData, "", "未发现 level.dat。" & vbCrLf) & If(World.Warning, "")
    End Sub

    Private Async Sub BackupClicked(sender As Object, e As EventArgs) Handles BtnBackup.Click
        Dim RootItem = TryCast(ComboRoots.SelectedItem, MyComboBoxItem)
        Dim WorldItem = TryCast(ComboWorlds.SelectedItem, MyComboBoxItem)
        Dim Root = TryCast(RootItem?.Tag, BedrockDataRoot)
        Dim World = TryCast(WorldItem?.Tag, BedrockWorldInfo)
        If IsBusy OrElse Root Is Nothing OrElse World Is Nothing Then Return
        If IsKnownBedrockProcessRunning() Then
            MyMsgBox("请先关闭 Minecraft 基岩版，再备份世界，以避免备份进行时游戏修改文件。", "世界备份")
            Return
        End If
        If MyMsgBox("请确认 Minecraft 已关闭且存档同步已完成。" & vbCrLf & vbCrLf &
                    "数据目录：" & Root.Path & vbCrLf & "世界：" & World.Name & vbCrLf &
                    "世界目录：" & World.Path, "确认备份目标", "继续", "取消") <> 1 Then Return
        Dim Dialog As New Microsoft.Win32.SaveFileDialog With {
            .Filter = "ZIP 备份 (*.zip)|*.zip",
            .FileName = "MinecraftWorldBackup-" & Date.Now.ToString("yyyyMMdd-HHmmss") & ".zip",
            .OverwritePrompt = False,
            .CheckPathExists = True
        }
        If Dialog.ShowDialog() <> True Then Return
        Dim BackupPath = Dialog.FileName
        SetBusy(True)
        LabWorldStatus.Text = "正在备份并校验世界…"
        Try
            Dim Info = Await Task.Run(Function()
                                          If IsKnownBedrockProcessRunning() Then Throw New InvalidOperationException("Minecraft 正在运行，请关闭后重试。")
                                          Return BedrockWorldBackup.Create(Root, World, BackupPath)
                                      End Function)
            LabWorldStatus.Text = $"备份已创建并校验：{Info.Files} 个文件。"
        Catch ex As Exception
            LabWorldStatus.Text = "备份失败：" & ex.Message
            Logger.Warn(ex, "基岩版世界备份失败")
        Finally
            SetBusy(False)
        End Try
    End Sub

    Private Async Sub RestoreClicked(sender As Object, e As EventArgs) Handles BtnRestore.Click
        Dim RootItem = TryCast(ComboRoots.SelectedItem, MyComboBoxItem)
        Dim Root = TryCast(RootItem?.Tag, BedrockDataRoot)
        If IsBusy OrElse Root Is Nothing Then Return
        If IsKnownBedrockProcessRunning() Then
            MyMsgBox("请先关闭 Minecraft 基岩版，再恢复世界。", "恢复世界")
            Return
        End If
        Dim Dialog As New Microsoft.Win32.OpenFileDialog With {
            .Filter = "PCL 世界备份 (*.zip)|*.zip", .CheckFileExists = True, .Multiselect = False
        }
        If Dialog.ShowDialog() <> True Then Return
        Dim BackupPath = Dialog.FileName
        SetBusy(True)
        LabWorldStatus.Text = "正在校验备份…"
        Try
            Dim Info = Await Task.Run(Function() BedrockWorldBackup.Verify(BackupPath))
            If MyMsgBox("请确认 Minecraft 已关闭且存档同步已完成。" & vbCrLf & vbCrLf &
                        "目标数据目录：" & Root.Path & vbCrLf & $"备份世界：{Info.WorldName}（{Info.Files} 个文件）" & vbCrLf &
                        "将创建新的世界目录，不覆盖现有目录。游戏及云同步对重复世界标识的处理尚未验证，请保留原备份。", "确认恢复目标", "恢复", "取消") <> 1 Then
                LabWorldStatus.Text = "已取消恢复。"
                Return
            End If
            LabWorldStatus.Text = "正在恢复并校验世界…"
            Dim Restored = Await Task.Run(Function()
                                              If IsKnownBedrockProcessRunning() Then Throw New InvalidOperationException("Minecraft 正在运行，请关闭后重试。")
                                              Return BedrockWorldBackup.RestoreAsNew(Root, BackupPath)
                                          End Function)
            LabWorldStatus.Text = "已恢复为新世界。请重新读取世界列表，并在游戏中确认。目录：" & Restored
        Catch ex As Exception
            LabWorldStatus.Text = "恢复失败：" & ex.Message
            Logger.Warn(ex, "基岩版世界恢复失败")
        Finally
            SetBusy(False)
        End Try
    End Sub

    Private Shared Function IsKnownBedrockProcessRunning() As Boolean
        For Each ProcessName In {"Minecraft.Windows", "GameLaunchHelper"}
            Dim Processes = Process.GetProcessesByName(ProcessName)
            Try
                If Processes.Length > 0 Then Return True
            Finally
                For Each GameProcess In Processes
                    GameProcess.Dispose()
                Next
            End Try
        Next
        Return False
    End Function

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
        BtnWorlds.IsEnabled = Not Value AndAlso Installation IsNot Nothing
        ComboRoots.IsEnabled = Not Value AndAlso ComboRoots.Items.Count > 0
        Dim RootItem = TryCast(ComboRoots.SelectedItem, MyComboBoxItem)
        Dim Root = TryCast(RootItem?.Tag, BedrockDataRoot)
        BtnReadWorlds.IsEnabled = Not Value AndAlso Root IsNot Nothing AndAlso Root.SupportsWorlds
        BtnRestore.IsEnabled = Not Value AndAlso Root IsNot Nothing AndAlso Root.SupportsWorlds
        ComboWorlds.IsEnabled = Not Value AndAlso ComboWorlds.Items.Count > 0
        Dim WorldItem = TryCast(ComboWorlds.SelectedItem, MyComboBoxItem)
        Dim World = TryCast(WorldItem?.Tag, BedrockWorldInfo)
        BtnBackup.IsEnabled = Not Value AndAlso World IsNot Nothing AndAlso World.HasLevelData
    End Sub
End Class
