Imports PCLCS.Bedrock

Public Class PageBedrockRight
    Private ReadOnly Service As New BedrockService(New WindowsBedrockPackageCatalog(), New WindowsBedrockActivator())
    Private HasLoaded As Boolean
    Private IsBusy As Boolean
    Private SelectedPackPath As String
    Private SelectedPackIsDirectory As Boolean

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
        If CardWorlds IsNot Nothing Then
            CardWorlds.Visibility = Visibility.Collapsed
            ComboRoots.Items.Clear()
        End If
        RefreshInstallPackButton()
    End Sub

    Private Async Sub WorldsClicked(sender As Object, e As EventArgs) Handles BtnWorlds.Click
        Dim Item = TryCast(ComboInstall.SelectedItem, MyComboBoxItem)
        Dim Installation = TryCast(Item?.Tag, BedrockInstallation)
        If IsBusy OrElse Installation Is Nothing Then Return
        CardWorlds.Visibility = Visibility.Visible
        Dim ScrollOperation = Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, New Action(Sub() PanBack.ScrollToBottom()))
        ComboRoots.Items.Clear()
        ComboWorlds.Items.Clear()
        ComboAddons.Items.Clear()
        ComboTransactions.Items.Clear()
        ComboRemovals.Items.Clear()
        LabWorldStatus.Text = ""
        LabWorldDetails.Text = ""
        LabAddonStatus.Text = ""
        LabAddonDetails.Text = ""
        LabTransactionStatus.Text = ""
        LabRemovalStatus.Text = ""
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
        BtnListAddons.IsEnabled = Not IsBusy AndAlso Root IsNot Nothing
        BtnListTransactions.IsEnabled = Not IsBusy AndAlso Root IsNot Nothing
        BtnListRemovals.IsEnabled = Not IsBusy AndAlso Root IsNot Nothing
        ComboWorlds.Items.Clear()
        ComboAddons.Items.Clear()
        ComboTransactions.Items.Clear()
        ComboRemovals.Items.Clear()
        LabWorldStatus.Text = If(Root IsNot Nothing AndAlso Not Root.SupportsWorlds, "共享内容目录不用于管理账户世界。", "")
        LabWorldDetails.Text = ""
        LabAddonStatus.Text = ""
        LabAddonDetails.Text = ""
        LabTransactionStatus.Text = ""
        LabRemovalStatus.Text = ""
        RefreshInstallPackButton()
    End Sub

    Private Async Sub ListRemovalsClicked(sender As Object, e As EventArgs) Handles BtnListRemovals.Click
        Dim Root = TryCast(TryCast(ComboRoots.SelectedItem, MyComboBoxItem)?.Tag, BedrockDataRoot)
        If IsBusy OrElse Root Is Nothing Then Return
        ComboRemovals.Items.Clear()
        LabRemovalStatus.Text = "正在读取移除记录…"
        SetBusy(True)
        Try
            Dim Removals = Await Task.Run(Function() BedrockAddonRemoval.List(Root))
            For Each Removal In Removals
                ComboRemovals.Items.Add(New MyComboBoxItem With {.Content = Removal.State & " · " & Path.GetFileName(Removal.Path), .Tag = Removal})
            Next
            LabRemovalStatus.Text = If(Removals.Count = 0, "此目录没有发现移除记录。", $"发现 {Removals.Count} 条移除记录；状态基于路径，恢复时会校验内容。")
        Catch ex As Exception
            LabRemovalStatus.Text = "读取移除记录失败：" & ex.Message
            Logger.Warn(ex, "读取基岩版附加包移除记录失败")
        Finally
            SetBusy(False)
        End Try
    End Sub

    Private Sub RemovalSelectionChanged(sender As Object, e As SelectionChangedEventArgs) Handles ComboRemovals.SelectionChanged
        Dim Removal = TryCast(TryCast(ComboRemovals.SelectedItem, MyComboBoxItem)?.Tag, BedrockRemovalInfo)
        BtnRestoreRemoval.IsEnabled = Not IsBusy AndAlso Removal IsNot Nothing AndAlso Removal.Error Is Nothing AndAlso Removal.State = "已隔离"
        If Removal IsNot Nothing Then
            LabRemovalStatus.Text = Removal.State & vbCrLf & "事务：" & Removal.Path & vbCrLf & "原位置：" & If(Removal.OriginalPath, "未知") &
                                    vbCrLf & "外部备份：" & If(Removal.BackupPath, "未知") & If(Removal.Error Is Nothing, "", vbCrLf & Removal.Error)
        End If
    End Sub

    Private Async Sub RestoreRemovalClicked(sender As Object, e As EventArgs) Handles BtnRestoreRemoval.Click
        Dim Root = TryCast(TryCast(ComboRoots.SelectedItem, MyComboBoxItem)?.Tag, BedrockDataRoot)
        Dim Removal = TryCast(TryCast(ComboRemovals.SelectedItem, MyComboBoxItem)?.Tag, BedrockRemovalInfo)
        Dim Installation = TryCast(TryCast(ComboInstall.SelectedItem, MyComboBoxItem)?.Tag, BedrockInstallation)
        If IsBusy OrElse Root Is Nothing OrElse Installation Is Nothing OrElse Removal Is Nothing OrElse Removal.State <> "已隔离" Then Return
        If IsKnownBedrockProcessRunning() Then
            MyMsgBox("请先关闭 Minecraft 基岩版并等待同步完成。", "恢复附加包")
            Return
        End If
        SetBusy(True)
        Try
            LabRemovalStatus.Text = "正在检查目标和数据目录…"
            Dim Roots = Await Task.Run(Function() ValidateInstallTarget(Installation, Root))
            If MyMsgBox("请确认游戏已关闭且同步完成。" & vbCrLf & vbCrLf & "数据目录：" & Root.Path & vbCrLf &
                        "恢复到：" & Removal.OriginalPath & vbCrLf & "事务：" & Removal.Path & vbCrLf &
                        "检查目录：" & vbCrLf & String.Join(vbCrLf, Roots.Select(Function(Item) Item.Path)) & vbCrLf &
                        "恢复前会校验内容和这些目录内的冲突，不覆盖已有文件；外部备份会保留。", "确认恢复附加包", "恢复", "取消") <> 1 Then
                LabRemovalStatus.Text = "已取消恢复。"
                Return
            End If
            LabRemovalStatus.Text = "正在校验并恢复附加包…"
            Dim Restored = Await Task.Run(Function()
                                              If IsKnownBedrockProcessRunning() Then Throw New InvalidOperationException("Minecraft 正在运行，请关闭后重试。")
                                              Dim CurrentRoots = ValidateInstallTarget(Installation, Root)
                                              If Not SameRoots(Roots, CurrentRoots) Then Throw New InvalidOperationException("数据目录范围已变化，请重新确认。")
                                              Return BedrockAddonRemoval.Restore(Root, Removal.Path, CurrentRoots)
                                          End Function)
            ComboRemovals.Items.Clear()
            LabRemovalStatus.Text = "已恢复到原位置；请在游戏中确认。" & vbCrLf & Restored
        Catch ex As Exception
            LabRemovalStatus.Text = "恢复未完成：" & ex.Message
            Logger.Warn(ex, "恢复已移除的基岩版附加包失败")
        Finally
            SetBusy(False)
        End Try
    End Sub

    Private Async Sub ListTransactionsClicked(sender As Object, e As EventArgs) Handles BtnListTransactions.Click
        Dim RootItem = TryCast(ComboRoots.SelectedItem, MyComboBoxItem)
        Dim Root = TryCast(RootItem?.Tag, BedrockDataRoot)
        If IsBusy OrElse Root Is Nothing Then Return
        ComboTransactions.Items.Clear()
        LabTransactionStatus.Text = "正在检查事务记录…"
        SetBusy(True)
        Try
            Dim Transactions = Await Task.Run(Function() BedrockAddonTransaction.List(Root))
            For Each Transaction In Transactions
                ComboTransactions.Items.Add(New MyComboBoxItem With {.Content = Transaction.State & " · " & Path.GetFileName(Transaction.Path), .Tag = Transaction})
            Next
            LabTransactionStatus.Text = If(Transactions.Count = 0, "此目录没有发现事务记录。", $"发现 {Transactions.Count} 条事务记录；请选择查看。")
        Catch ex As Exception
            LabTransactionStatus.Text = "读取事务失败：" & ex.Message
            Logger.Warn(ex, "读取基岩版附加包事务失败")
        Finally
            SetBusy(False)
        End Try
    End Sub

    Private Sub TransactionSelectionChanged(sender As Object, e As SelectionChangedEventArgs) Handles ComboTransactions.SelectionChanged
        Dim Item = TryCast(ComboTransactions.SelectedItem, MyComboBoxItem)
        Dim Transaction = TryCast(Item?.Tag, BedrockAddonTransactionInfo)
        BtnRollbackTransaction.IsEnabled = Not IsBusy AndAlso Transaction IsNot Nothing AndAlso Transaction.Error Is Nothing AndAlso Transaction.Installed > 0
        If Transaction IsNot Nothing Then
            LabTransactionStatus.Text = $"{Transaction.State}：暂存 {Transaction.Staged}，已移动 {Transaction.Installed}，缺失 {Transaction.Missing}。" &
                vbCrLf & Transaction.Path & If(Transaction.Error Is Nothing, "", vbCrLf & Transaction.Error)
        End If
    End Sub

    Private Async Sub RollbackTransactionClicked(sender As Object, e As EventArgs) Handles BtnRollbackTransaction.Click
        Dim Root = TryCast(TryCast(ComboRoots.SelectedItem, MyComboBoxItem)?.Tag, BedrockDataRoot)
        Dim Transaction = TryCast(TryCast(ComboTransactions.SelectedItem, MyComboBoxItem)?.Tag, BedrockAddonTransactionInfo)
        If IsBusy OrElse Root Is Nothing OrElse Transaction Is Nothing Then Return
        If IsKnownBedrockProcessRunning() Then
            MyMsgBox("请先关闭 Minecraft 基岩版，再恢复事务。", "附加包事务恢复")
            Return
        End If
        If MyMsgBox("请确认游戏已关闭且存档同步已完成。" & vbCrLf & vbCrLf &
                    "数据目录：" & Root.Path & vbCrLf & "事务目录：" & Transaction.Path & vbCrLf &
                    "未修改的事务包将移回暂存目录，不会删除文件；有变化时操作会停止。", "确认恢复事务", "恢复", "取消") <> 1 Then Return
        SetBusy(True)
        LabTransactionStatus.Text = "正在校验并恢复事务…"
        Try
            Await Task.Run(Sub()
                               If IsKnownBedrockProcessRunning() Then Throw New InvalidOperationException("Minecraft 正在运行，请关闭后重试。")
                               BedrockAddonTransaction.Rollback(Root, Transaction.Path)
                           End Sub)
            LabTransactionStatus.Text = "已将可恢复的包移回暂存目录；请重新读取事务记录。目录：" & Transaction.Path
            ComboTransactions.Items.Clear()
        Catch ex As Exception
            LabTransactionStatus.Text = "恢复未完成：" & ex.Message
            Logger.Warn(ex, "恢复基岩版附加包事务失败")
        Finally
            SetBusy(False)
        End Try
    End Sub

    Private Async Sub ReadAddonsClicked(sender As Object, e As EventArgs) Handles BtnListAddons.Click
        Dim Item = TryCast(ComboRoots.SelectedItem, MyComboBoxItem)
        Dim Root = TryCast(Item?.Tag, BedrockDataRoot)
        If IsBusy OrElse Root Is Nothing Then Return
        ComboAddons.Items.Clear()
        LabAddonDetails.Text = ""
        LabAddonStatus.Text = "正在读取附加包…"
        SetBusy(True)
        Try
            Dim Addons = Await Task.Run(Function() BedrockAddonCatalog.Read(Root))
            For Each Addon In Addons
                Dim Name = If(Addon.Manifest Is Nothing, Path.GetFileName(Addon.Path), Addon.Manifest.Name)
                ComboAddons.Items.Add(New MyComboBoxItem With {.Content = Addon.Category & " · " & Name, .Tag = Addon})
            Next
            LabAddonStatus.Text = If(Addons.Count = 0, "此目录中没有发现附加包。", $"发现 {Addons.Count} 个附加包，请选择查看详情。")
        Catch ex As Exception
            LabAddonStatus.Text = "读取附加包失败：" & ex.Message
            Logger.Warn(ex, "读取基岩版附加包失败")
        Finally
            SetBusy(False)
        End Try
    End Sub

    Private Sub AddonSelectionChanged(sender As Object, e As SelectionChangedEventArgs) Handles ComboAddons.SelectionChanged
        Dim Item = TryCast(ComboAddons.SelectedItem, MyComboBoxItem)
        Dim Addon = TryCast(Item?.Tag, BedrockInstalledAddon)
        If Addon Is Nothing Then
            LabAddonDetails.Text = ""
        ElseIf Addon.Manifest Is Nothing Then
            LabAddonDetails.Text = Addon.Error
        Else
            Dim Pack = Addon.Manifest
            LabAddonDetails.Text = $"{Pack.Name}　{Pack.Version}" & vbCrLf & $"UUID：{Pack.Uuid}　类型：{String.Join(", ", Pack.Modules)}" & vbCrLf &
                                   If(Pack.Dependencies.Count = 0, "", "依赖：" & String.Join(", ", Pack.Dependencies) & vbCrLf) &
                                   If(Addon.Warnings.Count = 0, "", "提示：" & String.Join("；", Addon.Warnings))
        End If
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

    Private Async Sub FolderClicked(sender As Object, e As EventArgs) Handles BtnFolder.Click
        Using Dialog As New System.Windows.Forms.FolderBrowserDialog With {.Description = "选择包含 manifest.json 的附加包文件夹", .ShowNewFolderButton = False}
            If Dialog.ShowDialog() <> System.Windows.Forms.DialogResult.OK Then Return
            Dim Folder = Dialog.SelectedPath
            SelectedPackPath = Nothing
            SetBusy(True)
            LabPackInfo.Text = "正在检查附加包文件夹…"
            Try
                Dim Pack = Await Task.Run(Function() BedrockPackInspector.InspectDirectory(Folder))
                SelectedPackPath = Folder
                SelectedPackIsDirectory = True
                LabPackInfo.Text = DescribePacks({Pack}) & vbCrLf & "已完成只读预览。实验性安装需先选择已验证的目标目录。"
            Catch ex As Exception
                LabPackInfo.Text = "无法预览附加包文件夹：" & ex.Message
                Logger.Warn(ex, "基岩版附加包预览失败")
            Finally
                SetBusy(False)
            End Try
        End Using
    End Sub

    Public Async Sub PreviewPackage(FilePath As String)
        If IsBusy Then Return
        SelectedPackPath = Nothing
        SetBusy(True)
        LabPackInfo.Text = "正在检查附加包…"
        Try
            Dim Packs = Await Task.Run(Function() BedrockPackInspector.Inspect(FilePath))
            SelectedPackPath = FilePath
            SelectedPackIsDirectory = False
            LabPackInfo.Text = DescribePacks(Packs) & vbCrLf & "已完成只读预览。实验性安装需先选择已验证的目标目录。"
        Catch ex As Exception
            LabPackInfo.Text = "无法预览附加包：" & ex.Message
            Logger.Warn(ex, "基岩版附加包预览失败")
        Finally
            SetBusy(False)
        End Try
    End Sub

    Private Async Sub InstallPackClicked(sender As Object, e As EventArgs) Handles BtnInstallPack.Click
        Dim Installation = TryCast(TryCast(ComboInstall.SelectedItem, MyComboBoxItem)?.Tag, BedrockInstallation)
        Dim Root = TryCast(TryCast(ComboRoots.SelectedItem, MyComboBoxItem)?.Tag, BedrockDataRoot)
        Dim Source = SelectedPackPath
        Dim IsDirectory = SelectedPackIsDirectory
        If IsBusy OrElse String.IsNullOrEmpty(Source) OrElse Not IsValidatedResourceTarget(Installation, Root) Then Return
        If IsKnownBedrockProcessRunning() Then
            MyMsgBox("请先关闭 Minecraft 基岩版并等待同步完成。", "安装资源包")
            Return
        End If
        SetBusy(True)
        Dim Transaction As String = Nothing
        Try
            LabPackInfo.Text = "正在准备并校验资源包…"
            Dim Packs = Await Task.Run(Function() PrepareResourcePacks(Source, IsDirectory))
            Dim Roots = Await Task.Run(Function() ValidateInstallTarget(Installation, Root))
            Dim Scope = String.Join(vbCrLf, Roots.Select(Function(Item) Item.Path))
            If MyMsgBox("请确认游戏已关闭且同步完成。此功能仅在一台设备上验证过资源包布局。" & vbCrLf & vbCrLf &
                        "目标目录：" & Root.Path & vbCrLf & "检查目录：" & vbCrLf & Scope & vbCrLf & vbCrLf &
                        DescribePacks(Packs.Select(Function(Item) Item.Preview.Pack)) & vbCrLf &
                        "将创建可恢复事务和新包目录，不覆盖现有包。游戏是否识别需自行确认。", "确认实验性安装", "安装", "取消") <> 1 Then
                LabPackInfo.Text = "已取消安装；准备目录已保留：" & vbCrLf & String.Join(vbCrLf, Packs.Select(Function(Item) Item.Directory))
                Return
            End If
            LabPackInfo.Text = "正在创建可恢复事务…"
            Transaction = Await Task.Run(Function()
                                             If IsKnownBedrockProcessRunning() Then Throw New InvalidOperationException("Minecraft 正在运行，请关闭后重试。")
                                             Dim CurrentRoots = ValidateInstallTarget(Installation, Root)
                                             If Not SameRoots(Roots, CurrentRoots) Then Throw New InvalidOperationException("数据目录范围已变化，请重新确认。")
                                             Return BedrockAddonTransaction.Prepare(Root, Packs, CurrentRoots)
                                         End Function)
            LabPackInfo.Text = "正在提交资源包；事务目录：" & Transaction
            Await Task.Run(Sub()
                               If IsKnownBedrockProcessRunning() Then Throw New InvalidOperationException("Minecraft 正在运行，请关闭后重试。")
                               Dim CurrentRoots = ValidateInstallTarget(Installation, Root)
                               If Not SameRoots(Roots, CurrentRoots) Then Throw New InvalidOperationException("数据目录范围已变化，请重新确认。")
                               BedrockAddonTransaction.Commit(Root, Transaction, CurrentRoots)
                           End Sub)
            LabPackInfo.Text = "资源包文件已放置。游戏是否识别或启用需在游戏中确认。事务目录：" & Transaction
        Catch ex As Exception
            LabPackInfo.Text = "安装未完成：" & ex.Message & If(Transaction Is Nothing, "", vbCrLf & "保留事务并在数据页面检查恢复：" & Transaction)
            Logger.Warn(ex, "实验性基岩版资源包安装失败")
        Finally
            SetBusy(False)
        End Try
    End Sub

    Private Shared Function PrepareResourcePacks(Source As String, IsDirectory As Boolean) As IReadOnlyList(Of BedrockPreparedPack)
        Dim Packs As IReadOnlyList(Of BedrockPreparedPack)
        If IsDirectory Then
            Packs = {New BedrockPreparedPack(Source, BedrockDirectoryInstaller.Preview(Source))}
        ElseIf Path.GetExtension(Source).Equals(".mcpack", StringComparison.OrdinalIgnoreCase) Then
            Packs = {BedrockArchivePreparer.PrepareMcpack(Source, Path.GetTempPath())}
        ElseIf Path.GetExtension(Source).Equals(".mcaddon", StringComparison.OrdinalIgnoreCase) Then
            Packs = BedrockArchivePreparer.PrepareMcaddon(Source, Path.GetTempPath()).Packs
        Else
            Throw New InvalidDataException("不支持的附加包格式。")
        End If
        If Packs.Any(Function(Item) Not Item.Preview.Pack.Modules.Contains("resources") OrElse Item.Preview.Pack.Modules.Contains("data")) Then
            Throw New InvalidDataException("当前实验性安装仅支持资源包；行为包及混合包尚未验证。准备目录已保留：" &
                                           vbCrLf & String.Join(vbCrLf, Packs.Select(Function(Item) Item.Directory)))
        End If
        Return Packs
    End Function

    Private Function ValidateInstallTarget(Installation As BedrockInstallation, Root As BedrockDataRoot) As IReadOnlyList(Of BedrockDataRoot)
        Dim Result = Service.Detect()
        If Result.Error IsNot Nothing OrElse Not Result.Installations.Any(Function(Item) Item.FamilyName = Installation.FamilyName AndAlso
            Item.PackageFullName = Installation.PackageFullName AndAlso Item.AppUserModelId = Installation.AppUserModelId) Then
            Throw New InvalidOperationException("游戏注册状态已变化，请刷新后重试。")
        End If
        Dim Roots = (New BedrockDataRootFinder()).Find(Installation)
        If Not Roots.Any(Function(Item) String.Equals(Item.Path, Root.Path, StringComparison.OrdinalIgnoreCase) AndAlso Item.Source = Root.Source) Then
            Throw New InvalidOperationException("目标数据目录已变化，请重新选择。")
        End If
        Return Roots
    End Function

    Private Shared Function SameRoots(First As IReadOnlyList(Of BedrockDataRoot), Second As IReadOnlyList(Of BedrockDataRoot)) As Boolean
        Return First.Select(Function(Item) Item.Path).OrderBy(Function(Item) Item, StringComparer.OrdinalIgnoreCase).
            SequenceEqual(Second.Select(Function(Item) Item.Path).OrderBy(Function(Item) Item, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase)
    End Function

    Private Shared Function IsValidatedResourceTarget(Installation As BedrockInstallation, Root As BedrockDataRoot) As Boolean
        Return Installation IsNot Nothing AndAlso Root IsNot Nothing AndAlso
               Installation.FamilyName = "Microsoft.MinecraftUWP_8wekyb3d8bbwe" AndAlso
               Installation.Version = "1.26.5203.0" AndAlso Root.Source = "GDK 共享内容目录"
    End Function

    Private Sub RefreshInstallPackButton()
        If BtnInstallPack Is Nothing OrElse ComboInstall Is Nothing OrElse ComboRoots Is Nothing Then Return
        Dim Installation = TryCast(TryCast(ComboInstall.SelectedItem, MyComboBoxItem)?.Tag, BedrockInstallation)
        Dim Root = TryCast(TryCast(ComboRoots.SelectedItem, MyComboBoxItem)?.Tag, BedrockDataRoot)
        BtnInstallPack.IsEnabled = Not IsBusy AndAlso Not String.IsNullOrEmpty(SelectedPackPath) AndAlso IsValidatedResourceTarget(Installation, Root)
        If LabInstallHint Is Nothing Then Return
        If String.IsNullOrEmpty(SelectedPackPath) OrElse Root Is Nothing Then
            LabInstallHint.Text = "请先预览资源包，并在数据与世界中选择目标目录。"
        ElseIf Not IsValidatedResourceTarget(Installation, Root) Then
            LabInstallHint.Text = "此实验功能仅开放给正式版包 1.26.5203.0 的 GDK 共享目录；其他安装类型和版本尚待验证。"
        Else
            LabInstallHint.Text = "仅支持资源包；安装前将再次校验内容和目标，并要求确认游戏已关闭且同步完成。"
        End If
    End Sub

    Private Shared Function DescribePacks(Packs As IEnumerable(Of BedrockPackInfo)) As String
        Dim Lines As New List(Of String)
        For Each Pack In Packs
            Lines.Add($"{Pack.Name}　{Pack.Version}" & vbCrLf &
                      $"UUID：{Pack.Uuid}　类型：{String.Join(", ", Pack.Modules)}" & vbCrLf &
                      If(Pack.Dependencies.Count > 0, "依赖：" & String.Join(", ", Pack.Dependencies) & vbCrLf, "") &
                      If(Pack.MinEngineVersion IsNot Nothing, "最低游戏版本：" & Pack.MinEngineVersion & vbCrLf, ""))
        Next
        Return String.Join(vbCrLf, Lines)
    End Function

    Private Sub SetBusy(Value As Boolean)
        IsBusy = Value
        BtnRefresh.IsEnabled = Not Value
        BtnAddons.IsEnabled = Not Value
        BtnFolder.IsEnabled = Not Value
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
        BtnListAddons.IsEnabled = Not Value AndAlso Root IsNot Nothing
        BtnListTransactions.IsEnabled = Not Value AndAlso Root IsNot Nothing
        BtnListRemovals.IsEnabled = Not Value AndAlso Root IsNot Nothing
        ComboRemovals.IsEnabled = Not Value AndAlso ComboRemovals.Items.Count > 0
        Dim Removal = TryCast(TryCast(ComboRemovals.SelectedItem, MyComboBoxItem)?.Tag, BedrockRemovalInfo)
        BtnRestoreRemoval.IsEnabled = Not Value AndAlso Removal IsNot Nothing AndAlso Removal.Error Is Nothing AndAlso Removal.State = "已隔离"
        ComboTransactions.IsEnabled = Not Value AndAlso ComboTransactions.Items.Count > 0
        Dim TransactionItem = TryCast(ComboTransactions.SelectedItem, MyComboBoxItem)
        Dim Transaction = TryCast(TransactionItem?.Tag, BedrockAddonTransactionInfo)
        BtnRollbackTransaction.IsEnabled = Not Value AndAlso Transaction IsNot Nothing AndAlso Transaction.Error Is Nothing AndAlso Transaction.Installed > 0
        ComboAddons.IsEnabled = Not Value AndAlso ComboAddons.Items.Count > 0
        ComboWorlds.IsEnabled = Not Value AndAlso ComboWorlds.Items.Count > 0
        Dim WorldItem = TryCast(ComboWorlds.SelectedItem, MyComboBoxItem)
        Dim World = TryCast(WorldItem?.Tag, BedrockWorldInfo)
        BtnBackup.IsEnabled = Not Value AndAlso World IsNot Nothing AndAlso World.HasLevelData
        RefreshInstallPackButton()
    End Sub
End Class
