Imports PCLCS.Bedrock

Public Class PageBedrockRight
    Private ReadOnly Service As New BedrockService(New WindowsBedrockPackageCatalog(), New WindowsBedrockActivator())
    Private HasLoaded As Boolean
    Private IsBusy As Boolean
    Private SelectedPackPath As String
    Private SelectedPackIsDirectory As Boolean
    Private WorldAddonScanIncomplete As Boolean = True

    Private Class WorldAddonChoice
        Public ReadOnly Root As BedrockDataRoot
        Public ReadOnly Addon As BedrockInstalledAddon
        Public ReadOnly IsWorldReference As Boolean
        Public Sub New(Root As BedrockDataRoot, Addon As BedrockInstalledAddon, Optional IsWorldReference As Boolean = False)
            Me.Root = Root
            Me.Addon = Addon
            Me.IsWorldReference = IsWorldReference
        End Sub
    End Class

    Private Class WorldAddonList
        Public ReadOnly Choices As IReadOnlyList(Of WorldAddonChoice)
        Public ReadOnly IncompleteRoots As IReadOnlyList(Of String)
        Public Sub New(Choices As IReadOnlyList(Of WorldAddonChoice), IncompleteRoots As IReadOnlyList(Of String))
            Me.Choices = Choices
            Me.IncompleteRoots = IncompleteRoots
        End Sub
    End Class

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
            If Result.Installations.Count = 0 AndAlso Result.Error IsNot Nothing Then
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
                LabStatus.Text = "已发现 " & Result.Installations.Count & " 个已注册的游戏安装。" &
                                 If(Result.Error Is Nothing, "", vbCrLf & "部分安装状态无法查询：" & Result.Error & vbCrLf & "部分数据操作需要完整检测，请刷新后重试。")
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
        ComboWorldAddons.Items.Clear()
        ComboAddons.Items.Clear()
        ComboTransactions.Items.Clear()
        ComboRemovals.Items.Clear()
        LabWorldStatus.Text = ""
        LabWorldDetails.Text = ""
        LabWorldAddonStatus.Text = ""
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
        ComboWorldAddons.Items.Clear()
        ComboAddons.Items.Clear()
        ComboTransactions.Items.Clear()
        ComboRemovals.Items.Clear()
        LabWorldStatus.Text = If(Root IsNot Nothing AndAlso Not Root.SupportsWorlds, "共享内容目录不用于管理账户世界。", "")
        LabWorldDetails.Text = ""
        LabWorldAddonStatus.Text = ""
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
        BtnRestoreRemoval.IsEnabled = Not IsBusy AndAlso CanRestoreRemoval(Removal)
        If Removal IsNot Nothing Then
            LabRemovalStatus.Text = Removal.State & vbCrLf & "事务：" & Removal.Path & vbCrLf & "原位置：" & If(Removal.OriginalPath, "未知") &
                                    vbCrLf & "外部备份：" & If(Removal.BackupPath, "未知") & If(Removal.Error Is Nothing, "", vbCrLf & Removal.Error)
        End If
    End Sub

    Private Shared Function CanRestoreRemoval(Removal As BedrockRemovalInfo) As Boolean
        Return Removal IsNot Nothing AndAlso Removal.Error Is Nothing AndAlso
               (Removal.State = "已隔离" OrElse Removal.State = "内容缺失")
    End Function

    Private Async Sub RestoreRemovalClicked(sender As Object, e As EventArgs) Handles BtnRestoreRemoval.Click
        Dim Root = TryCast(TryCast(ComboRoots.SelectedItem, MyComboBoxItem)?.Tag, BedrockDataRoot)
        Dim Removal = TryCast(TryCast(ComboRemovals.SelectedItem, MyComboBoxItem)?.Tag, BedrockRemovalInfo)
        Dim Installation = TryCast(TryCast(ComboInstall.SelectedItem, MyComboBoxItem)?.Tag, BedrockInstallation)
        If IsBusy OrElse Root Is Nothing OrElse Installation Is Nothing OrElse Not CanRestoreRemoval(Removal) Then Return
        Dim UseBackup = Removal.State = "内容缺失"
        If IsKnownBedrockProcessRunning() Then
            MyMsgBox("请先关闭 Minecraft 基岩版并等待同步完成。", "恢复附加包")
            Return
        End If
        SetBusy(True)
        Try
            LabRemovalStatus.Text = "正在检查目标和数据目录…"
            Dim Roots = Await Task.Run(Function() ValidateInstallTarget(Installation, Root))
            Dim Backup As String = Nothing
            If UseBackup Then
                Using Dialog As New System.Windows.Forms.FolderBrowserDialog With {.Description = "选择原始或已移动的附加包备份目录", .ShowNewFolderButton = False}
                    If Not String.IsNullOrWhiteSpace(Removal.BackupPath) AndAlso Directory.Exists(Removal.BackupPath) Then Dialog.SelectedPath = Removal.BackupPath
                    If Dialog.ShowDialog() <> System.Windows.Forms.DialogResult.OK Then
                        LabRemovalStatus.Text = "已取消恢复。"
                        Return
                    End If
                    Backup = Path.GetFullPath(Dialog.SelectedPath).TrimEnd(Path.DirectorySeparatorChar)
                End Using
            End If
            If MyMsgBox("请确认游戏已关闭且同步完成。" & vbCrLf & vbCrLf & "数据目录：" & Root.Path & vbCrLf &
                        "恢复到：" & Removal.OriginalPath & vbCrLf & "事务：" & Removal.Path & vbCrLf &
                        If(UseBackup, "隔离内容缺失，将复制选定的外部备份：" & Backup & vbCrLf, "恢复来源：事务内的隔离内容。" & vbCrLf) &
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
                                              Return If(UseBackup, BedrockAddonRemoval.RestoreFromSelectedBackup(Root, Removal.Path, CurrentRoots, Backup),
                                                        BedrockAddonRemoval.Restore(Root, Removal.Path, CurrentRoots))
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
                Dim Name = If(Addon.Manifest Is Nothing, Path.GetFileName(Addon.Path), Addon.Manifest.DisplayName)
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
        BtnCheckDependencies.IsEnabled = Not IsBusy AndAlso Addon IsNot Nothing AndAlso Addon.Manifest IsNot Nothing AndAlso Addon.Manifest.Dependencies.Count > 0
        RefreshRemoveAddonButton()
        If Addon Is Nothing Then
            LabAddonDetails.Text = ""
        ElseIf Addon.Manifest Is Nothing Then
            LabAddonDetails.Text = Addon.Error
        Else
            Dim Pack = Addon.Manifest
            LabAddonDetails.Text = $"清单名称：{Pack.Name}（可能为本地化键）　版本：{Pack.Version}" & vbCrLf & $"UUID：{Pack.Uuid}　类型：{String.Join(", ", Pack.Modules)}" & vbCrLf &
                                   If(Pack.DisplayLanguage Is Nothing OrElse Pack.DisplayName = Pack.Name, "", $"名称候选（{Pack.DisplayLanguage}）：{Pack.DisplayName}" & vbCrLf) &
                                   If(Pack.Modules.Contains("resources"), "资源包使用范围：" & Pack.PackScope & vbCrLf, "") &
                                   If(Pack.Dependencies.Count = 0, "", "依赖：" & String.Join(", ", Pack.Dependencies) & vbCrLf) &
                                   If(Addon.Warnings.Count = 0, "", "提示：" & String.Join("；", Addon.Warnings))
        End If
    End Sub

    Private Async Sub RemoveAddonClicked(sender As Object, e As EventArgs) Handles BtnRemoveAddon.Click
        Dim Installation = TryCast(TryCast(ComboInstall.SelectedItem, MyComboBoxItem)?.Tag, BedrockInstallation)
        Dim Root = TryCast(TryCast(ComboRoots.SelectedItem, MyComboBoxItem)?.Tag, BedrockDataRoot)
        Dim Addon = TryCast(TryCast(ComboAddons.SelectedItem, MyComboBoxItem)?.Tag, BedrockInstalledAddon)
        If IsBusy OrElse Not CanRemoveAddon(Installation, Root, Addon) Then Return
        If IsKnownBedrockProcessRunning() Then
            MyMsgBox("请先关闭 Minecraft 基岩版并等待同步完成。", "移除附加包")
            Return
        End If
        SetBusy(True)
        Try
            LabAddonStatus.Text = "正在检查附加包和世界引用…"
            Dim Roots = Await Task.Run(Function() ValidateInstallTarget(Installation, Root))
            Dim WorldRoots = Roots.Where(Function(Item) Item.SupportsWorlds AndAlso Directory.Exists(Path.Combine(Item.Path, "minecraftWorlds"))).ToArray()
            If WorldRoots.Length = 0 Then Throw New InvalidOperationException("没有可检查的世界目录，不能安全移除。")
            Dim Preview = Await Task.Run(Function() BedrockDirectoryInstaller.Preview(Addon.Path))
            If Preview.Pack.Uuid <> Addon.Manifest.Uuid OrElse Preview.Pack.Version <> Addon.Manifest.Version Then
                Throw New InvalidOperationException("附加包已变化，请重新读取附加包。")
            End If
            Dim Usage = Await Task.Run(Function() BedrockWorldPackUsage.Check(WorldRoots, Preview.Pack.Uuid))
            If Not Usage.Complete Then Throw New InvalidOperationException("部分世界无法完整检查，不能安全移除。")
            If Usage.References.Count > 0 Then Throw New InvalidOperationException("有世界仍引用此附加包，请先检查世界配置。")
            Dim GlobalUsage = Await Task.Run(Function() BedrockGlobalPackUsage.Check(Roots, Preview.Pack.Uuid))
            If GlobalUsage.IncompletePaths.Count > 0 Then Throw New InvalidOperationException("候选全局资源配置无法完整检查，不能安全移除。")
            If GlobalUsage.References.Count > 0 Then Throw New InvalidOperationException("候选全局资源配置仍引用此附加包，请先检查游戏设置。")
            Dim Dependents = Await Task.Run(Function() BedrockAddonDependents.Check(Roots, Addon.Path, Preview.Pack.Uuid))
            If Not Dependents.Complete Then Throw New InvalidOperationException("部分附加包无法识别，不能完整检查反向依赖。")
            If Dependents.Dependents.Count > 0 Then Throw New InvalidOperationException("其他附加包声明依赖此包 UUID；无论是否启用或版本是否相同，当前均禁止自动移除：" & vbCrLf &
                String.Join(vbCrLf, Dependents.Dependents.Take(5).Select(Function(Item) Item.Addon.Path)))
            Using Dialog As New System.Windows.Forms.FolderBrowserDialog With {.Description = "选择游戏数据目录之外的备份文件夹", .ShowNewFolderButton = True}
                If Dialog.ShowDialog() <> System.Windows.Forms.DialogResult.OK Then
                    LabAddonStatus.Text = "已取消移除。"
                    Return
                End If
                Dim Backup = Path.GetFullPath(Dialog.SelectedPath).TrimEnd(Path.DirectorySeparatorChar)
                If Roots.Any(Function(Item) IsWithinRoot(Item.Path, Backup)) Then Throw New InvalidOperationException("备份不能位于候选游戏数据目录内。")
                Dim Skipped = Roots.Where(Function(Item) Item.SupportsWorlds AndAlso Not WorldRoots.Any(Function(WorldRoot) String.Equals(WorldRoot.Path, Item.Path, StringComparison.OrdinalIgnoreCase))).ToArray()
                If MyMsgBox("请确认游戏已关闭且同步完成。此功能尚未在真实用户附加包上验证。" & vbCrLf & vbCrLf &
                            "附加包：" & Preview.Pack.Name & "　" & Preview.Pack.Version & vbCrLf & "UUID：" & Preview.Pack.Uuid.ToString() & vbCrLf &
                            "原目录：" & Addon.Path & vbCrLf & "备份目录：" & Backup & vbCrLf &
                            "已检查世界目录：" & vbCrLf & String.Join(vbCrLf, WorldRoots.Select(Function(Item) Item.Path)) & vbCrLf &
                            If(Skipped.Length > 0, "未检查的候选世界目录：" & vbCrLf & String.Join(vbCrLf, Skipped.Select(Function(Item) Item.Path)) & vbCrLf, "") &
                            "已检查发现目录中的附加包声明及 minecraftpe 下的候选全局配置；其他引用、云端和未列出的账户仍无法检查。将先创建外部备份，再移入可恢复的隔离目录。", "确认实验性移除", "移除", "取消") <> 1 Then
                    LabAddonStatus.Text = "已取消移除。"
                    Return
                End If
                LabAddonStatus.Text = "正在备份并隔离附加包…"
                Dim Removed = Await Task.Run(Function()
                                                 If IsKnownBedrockProcessRunning() Then Throw New InvalidOperationException("Minecraft 正在运行，请关闭后重试。")
                                                 Dim CurrentRoots = ValidateInstallTarget(Installation, Root)
                                                 If Not SameRoots(Roots, CurrentRoots) Then Throw New InvalidOperationException("数据目录范围已变化，请重新确认。")
                                                 Dim CurrentWorldRoots = CurrentRoots.Where(Function(Item) Item.SupportsWorlds AndAlso Directory.Exists(Path.Combine(Item.Path, "minecraftWorlds"))).ToArray()
                                                 If Not SameRoots(WorldRoots, CurrentWorldRoots) Then Throw New InvalidOperationException("世界目录范围已变化，请重新确认。")
                                                 Return BedrockAddonRemoval.Remove(Root, Addon, Preview, Backup, WorldRoots, CurrentRoots)
                                             End Function)
                ComboAddons.Items.Clear()
                LabAddonDetails.Text = ""
                LabAddonStatus.Text = "附加包已移入隔离目录。外部备份：" & Removed.BackupPath & vbCrLf & "事务：" & Removed.TransactionPath &
                                      vbCrLf & "可在移除记录中恢复；游戏引用状态需自行确认。"
            End Using
        Catch ex As Exception
            LabAddonStatus.Text = "移除未完成：" & ex.Message
            Logger.Warn(ex, "实验性基岩版附加包移除失败")
        Finally
            SetBusy(False)
        End Try
    End Sub

    Private Shared Function IsWithinRoot(Root As String, Candidate As String) As Boolean
        Dim BasePath = Path.GetFullPath(Root).TrimEnd(Path.DirectorySeparatorChar)
        Dim TargetPath = Path.GetFullPath(Candidate).TrimEnd(Path.DirectorySeparatorChar)
        Return String.Equals(BasePath, TargetPath, StringComparison.OrdinalIgnoreCase) OrElse
               TargetPath.StartsWith(BasePath & Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
    End Function

    Private Shared Function CanRemoveAddon(Installation As BedrockInstallation, Root As BedrockDataRoot, Addon As BedrockInstalledAddon) As Boolean
        Return IsValidatedResourceTarget(Installation, Root) AndAlso Addon IsNot Nothing AndAlso Addon.Manifest IsNot Nothing AndAlso
               ((Addon.Category = "资源包" AndAlso Addon.Manifest.Modules.Contains("resources") AndAlso Not Addon.Manifest.Modules.Contains("data")) OrElse
                (Addon.Category = "行为包" AndAlso Addon.Manifest.Modules.Contains("data") AndAlso Not Addon.Manifest.Modules.Contains("resources")))
    End Function

    Private Sub RefreshRemoveAddonButton()
        If BtnRemoveAddon Is Nothing OrElse ComboInstall Is Nothing OrElse ComboRoots Is Nothing OrElse ComboAddons Is Nothing Then Return
        Dim Installation = TryCast(TryCast(ComboInstall.SelectedItem, MyComboBoxItem)?.Tag, BedrockInstallation)
        Dim Root = TryCast(TryCast(ComboRoots.SelectedItem, MyComboBoxItem)?.Tag, BedrockDataRoot)
        Dim Addon = TryCast(TryCast(ComboAddons.SelectedItem, MyComboBoxItem)?.Tag, BedrockInstalledAddon)
        BtnRemoveAddon.IsEnabled = Not IsBusy AndAlso CanRemoveAddon(Installation, Root, Addon)
    End Sub

    Private Async Sub CheckDependenciesClicked(sender As Object, e As EventArgs) Handles BtnCheckDependencies.Click
        Dim Installation = TryCast(TryCast(ComboInstall.SelectedItem, MyComboBoxItem)?.Tag, BedrockInstallation)
        Dim Root = TryCast(TryCast(ComboRoots.SelectedItem, MyComboBoxItem)?.Tag, BedrockDataRoot)
        Dim Addon = TryCast(TryCast(ComboAddons.SelectedItem, MyComboBoxItem)?.Tag, BedrockInstalledAddon)
        If IsBusy OrElse Installation Is Nothing OrElse Root Is Nothing OrElse Addon Is Nothing OrElse Addon.Manifest Is Nothing Then Return
        Dim PreviousDetails = LabAddonDetails.Text
        SetBusy(True)
        LabAddonStatus.Text = "正在检查候选数据目录中的依赖…"
        Try
            Dim Report = Await Task.Run(Function()
                                            Dim Roots = ValidateInstallTarget(Installation, Root, False)
                                            Dim Current = BedrockAddonCatalog.Read(Root).FirstOrDefault(Function(Item) String.Equals(Item.Path, Addon.Path, StringComparison.OrdinalIgnoreCase))
                                            If Current Is Nothing OrElse Current.Manifest Is Nothing OrElse Current.Manifest.Uuid <> Addon.Manifest.Uuid OrElse
                                               Current.Manifest.Version <> Addon.Manifest.Version OrElse Not Current.Manifest.Dependencies.SequenceEqual(Addon.Manifest.Dependencies) Then
                                                Throw New InvalidOperationException("所选包的清单已变化，请重新读取附加包。")
                                            End If
                                            Return BedrockDependencyCheck.Analyze(Current.Manifest, Roots)
                                        End Function)
            Dim Lines = Report.Results.Select(Function(Item) Item.Dependency & "：" & Item.Status)
            LabAddonDetails.Text = PreviousDetails & vbCrLf & "依赖检查：" & vbCrLf & String.Join(vbCrLf, Lines)
            LabAddonStatus.Text = If(Report.IncompleteRoots.Count = 0, "已检查候选目录；结果不表示世界已启用依赖。",
                                     "部分候选目录无法完整读取；未发现的依赖不能判定为缺失。")
        Catch ex As Exception
            LabAddonStatus.Text = "检查依赖失败：" & ex.Message
            Logger.Warn(ex, "检查基岩版附加包依赖失败")
        Finally
            SetBusy(False)
        End Try
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
        If ComboWorldAddons IsNot Nothing Then ComboWorldAddons.Items.Clear()
        WorldAddonScanIncomplete = True
        If LabWorldAddonStatus IsNot Nothing Then LabWorldAddonStatus.Text = ""
        RefreshWorldAddonButtons()
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

    Private Async Sub ReadWorldAddonsClicked(sender As Object, e As EventArgs) Handles BtnReadWorldAddons.Click
        Dim Installation = TryCast(TryCast(ComboInstall.SelectedItem, MyComboBoxItem)?.Tag, BedrockInstallation)
        Dim Root = TryCast(TryCast(ComboRoots.SelectedItem, MyComboBoxItem)?.Tag, BedrockDataRoot)
        Dim World = TryCast(TryCast(ComboWorlds.SelectedItem, MyComboBoxItem)?.Tag, BedrockWorldInfo)
        If IsBusy OrElse Installation Is Nothing OrElse Root Is Nothing OrElse Not Root.SupportsWorlds OrElse World Is Nothing Then Return
        ComboWorldAddons.Items.Clear()
        WorldAddonScanIncomplete = True
        LabWorldAddonStatus.Text = "正在读取候选目录中的附加包…"
        SetBusy(True)
        Try
            Dim Result = Await Task.Run(Function()
                                            Dim Roots = ValidateInstallTarget(Installation, Root, False)
                                            Return CollectWorldAddons(Roots, Root, World)
                                        End Function)
            WorldAddonScanIncomplete = Result.IncompleteRoots.Count > 0
            For Each Choice In Result.Choices
                Dim Pack = Choice.Addon.Manifest
                ComboWorldAddons.Items.Add(New MyComboBoxItem With {.Content = If(Choice.IsWorldReference, "世界引用", Choice.Root.Source) & " · " & Pack.DisplayName & " · " & Pack.Version, .Tag = Choice})
            Next
            LabWorldAddonStatus.Text = If(Result.Choices.Count = 0, "候选目录中没有可识别的附加包。", $"发现 {Result.Choices.Count} 个候选包；请选择后检查其使用范围。") &
                                       If(Result.IncompleteRoots.Count = 0, "", vbCrLf & "部分目录无法完整读取；已安装包的配置修改已禁用，现有世界引用仍可在备份后停用。") &
                                       If(IsEligibleWorldTarget(Installation, Root, World), "", vbCrLf & "当前目标仅支持只读查看；世界配置写入尚未验证。")
        Catch ex As Exception
            LabWorldAddonStatus.Text = "读取候选附加包失败：" & ex.Message
            Logger.Warn(ex, "读取世界附加包候选失败")
        Finally
            SetBusy(False)
        End Try
    End Sub

    Private Shared Function CollectWorldAddons(Roots As IReadOnlyList(Of BedrockDataRoot), WorldRoot As BedrockDataRoot, World As BedrockWorldInfo) As WorldAddonList
        Dim Choices As New List(Of WorldAddonChoice)
        Dim Incomplete As New List(Of String)
        For Each Root In Roots
            Dim Addons As IReadOnlyList(Of BedrockInstalledAddon)
            Try
                Addons = BedrockAddonCatalog.Read(Root)
            Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
                Incomplete.Add(Root.Path)
                Continue For
            End Try
            If Addons.Any(Function(Item) Item.Manifest Is Nothing) Then Incomplete.Add(Root.Path)
            For Each Addon In Addons
                Dim Pack = Addon.Manifest
                If Pack Is Nothing Then Continue For
                Dim Resource = Pack.Modules.Contains("resources")
                Dim Behavior = Pack.Modules.Contains("data")
                If Resource = Behavior Then Continue For
                If Choices.Count >= 4096 Then Throw New InvalidDataException("候选附加包过多。")
                Choices.Add(New WorldAddonChoice(Root, Addon))
            Next
        Next
        AddWorldReferences(Choices, WorldRoot, World, World.ResourcePacks, "resources")
        AddWorldReferences(Choices, WorldRoot, World, World.BehaviorPacks, "data")
        Return New WorldAddonList(Choices, Incomplete)
    End Function

    Private Shared Sub AddWorldReferences(Choices As List(Of WorldAddonChoice), WorldRoot As BedrockDataRoot, World As BedrockWorldInfo,
                                          References As IReadOnlyList(Of String), ModuleType As String)
        For Each Reference In References
            Dim Parts = Reference.Split({" "c}, 2)
            Dim Id As Guid
            If Parts.Length <> 2 OrElse Not Guid.TryParse(Parts(0), Id) OrElse Id = Guid.Empty OrElse
               Not System.Text.RegularExpressions.Regex.IsMatch(Parts(1), "^[0-9]+\.[0-9]+\.[0-9]+$") Then Continue For
            If Choices.Count >= 4096 Then Throw New InvalidDataException("候选附加包过多。")
            Dim Pack As New BedrockPackInfo("世界中的" & If(ModuleType = "resources", "资源包引用", "行为包引用"), "", Id, Parts(1), {ModuleType}, {}, Nothing)
            Choices.Add(New WorldAddonChoice(WorldRoot, New BedrockInstalledAddon(World.Path, "世界引用", Pack, Nothing), True))
        Next
    End Sub

    Private Sub WorldAddonSelectionChanged(sender As Object, e As SelectionChangedEventArgs) Handles ComboWorldAddons.SelectionChanged
        Dim Choice = TryCast(TryCast(ComboWorldAddons.SelectedItem, MyComboBoxItem)?.Tag, WorldAddonChoice)
        If Choice IsNot Nothing Then
            Dim Pack = Choice.Addon.Manifest
            LabWorldAddonStatus.Text = $"清单名称：{Pack.Name}（可能为本地化键）　版本：{Pack.Version}" & vbCrLf & $"UUID：{Pack.Uuid}　类型：{String.Join(", ", Pack.Modules)}" & vbCrLf &
                                       If(Pack.DisplayLanguage Is Nothing OrElse Pack.DisplayName = Pack.Name, "", $"名称候选（{Pack.DisplayLanguage}）：{Pack.DisplayName}" & vbCrLf) &
                                       If(Pack.Modules.Contains("resources"), "资源包使用范围：" & Pack.PackScope & vbCrLf, "") &
                                       If(Choice.IsWorldReference, "来源：当前世界配置；可停用该引用。", "包目录：" & Choice.Addon.Path)
            Dim Installation = TryCast(TryCast(ComboInstall.SelectedItem, MyComboBoxItem)?.Tag, BedrockInstallation)
            Dim Root = TryCast(TryCast(ComboRoots.SelectedItem, MyComboBoxItem)?.Tag, BedrockDataRoot)
            Dim World = TryCast(TryCast(ComboWorlds.SelectedItem, MyComboBoxItem)?.Tag, BedrockWorldInfo)
            If Not IsEligibleWorldTarget(Installation, Root, World) Then LabWorldAddonStatus.Text &= vbCrLf & "当前目标仅支持只读查看；世界配置写入尚未验证。"
            If WorldAddonScanIncomplete Then LabWorldAddonStatus.Text &= vbCrLf & "部分目录无法完整读取；已安装包的配置修改已禁用，现有世界引用仍可在备份后停用。"
        End If
        RefreshWorldAddonButtons()
    End Sub

    Private Async Sub WorldEnableAddonClicked(sender As Object, e As EventArgs) Handles BtnWorldEnableAddon.Click
        Await ChangeWorldAddon(True)
    End Sub

    Private Async Sub WorldDisableAddonClicked(sender As Object, e As EventArgs) Handles BtnWorldDisableAddon.Click
        Await ChangeWorldAddon(False)
    End Sub

    Private Async Function ChangeWorldAddon(Enable As Boolean) As Task
        Dim Installation = TryCast(TryCast(ComboInstall.SelectedItem, MyComboBoxItem)?.Tag, BedrockInstallation)
        Dim Root = TryCast(TryCast(ComboRoots.SelectedItem, MyComboBoxItem)?.Tag, BedrockDataRoot)
        Dim World = TryCast(TryCast(ComboWorlds.SelectedItem, MyComboBoxItem)?.Tag, BedrockWorldInfo)
        Dim Choice = TryCast(TryCast(ComboWorldAddons.SelectedItem, MyComboBoxItem)?.Tag, WorldAddonChoice)
        If IsBusy OrElse Not IsEligibleWorldTarget(Installation, Root, World) OrElse Choice Is Nothing OrElse (Enable AndAlso Choice.IsWorldReference) Then Return
        If IsKnownBedrockProcessRunning() Then
            MyMsgBox("请先关闭 Minecraft 基岩版并等待同步完成。", "世界附加包配置")
            Return
        End If
        SetBusy(True)
        Dim FullBackup As String = Nothing
        Try
            LabWorldAddonStatus.Text = "正在检查世界、附加包和依赖…"
            Dim Roots = Await Task.Run(Function() ValidateInstallTarget(Installation, Root))
            Dim Preview As BedrockDirectoryPreview = Nothing
            If Not Choice.IsWorldReference Then Preview = Await Task.Run(Function() ValidateWorldAddon(Choice, Roots))
            Dim Pack = If(Choice.IsWorldReference, Choice.Addon.Manifest, Preview.Pack)
            Dim Kind = If(Pack.Modules.Contains("resources"), "resource", "behavior")
            If Enable AndAlso Kind = "resource" AndAlso Pack.PackScope <> "any" AndAlso Pack.PackScope <> "world" Then
                Throw New InvalidOperationException("此资源包不能确认可用于世界。")
            End If
            Dim Snapshot = Await Task.Run(Function() BedrockWorldPackEditor.Read(Root, World, Kind))
            Dim Report = Await Task.Run(Function() BedrockDependencyCheck.Analyze(Pack, Roots))
            Dim WorldDependencies As BedrockWorldDependencyResult = Nothing
            If Enable AndAlso Pack.Dependencies.Count > 0 Then
                WorldDependencies = Await Task.Run(Function() BedrockWorldDependencyCheck.Check(Root, World, Pack))
                Dim SelectedSnapshot = If(Kind = "resource", WorldDependencies.Resources, WorldDependencies.Behaviors)
                If SelectedSnapshot.Exists <> Snapshot.Exists OrElse SelectedSnapshot.Hash <> Snapshot.Hash Then
                    Throw New InvalidOperationException("世界配置已变化，请重新读取。")
                End If
            End If
            Dim Reverse As BedrockWorldDependentsResult = Nothing
            If Not Enable Then
                Reverse = Await Task.Run(Function() BedrockWorldDependents.Check(Roots, Root, World, Pack.Uuid))
                Dim SelectedSnapshot = If(Kind = "resource", Reverse.Resources, Reverse.Behaviors)
                If SelectedSnapshot.Exists <> Snapshot.Exists OrElse SelectedSnapshot.Hash <> Snapshot.Hash Then
                    Throw New InvalidOperationException("世界配置已变化，请重新读取。")
                End If
            End If
            Dim DependencyLines = Report.Results.Select(Function(Item) Item.Dependency & "：" & Item.Status).ToArray()
            Dim DependencyText = If(DependencyLines.Length = 0, "无声明依赖。", String.Join(vbCrLf, DependencyLines.Take(12)) &
                                    If(DependencyLines.Length > 12, vbCrLf & $"另有 {DependencyLines.Length - 12} 项未显示。", ""))
            If Choice.IsWorldReference Then DependencyText = "此条目仅来自世界配置，无法检查原包声明的依赖。"
            If Report.IncompleteRoots.Count > 0 Then DependencyText &= vbCrLf & "部分候选目录无法完整读取。"
            If WorldDependencies IsNot Nothing Then
                Dim WorldLines = WorldDependencies.Dependencies.Select(Function(Item) Item.Dependency & "：" & Item.Status).ToArray()
                DependencyText &= vbCrLf & "世界配置中的依赖：" & vbCrLf & String.Join(vbCrLf, WorldLines.Take(12)) &
                                  If(WorldLines.Length > 12, vbCrLf & $"另有 {WorldLines.Length - 12} 项未显示。", "")
            End If
            If Reverse IsNot Nothing Then
                DependencyText &= vbCrLf & "当前世界中声明依赖此 UUID 的包：" &
                                  If(Reverse.Dependents.Count = 0, "未发现可读取的匹配项。", vbCrLf &
                                     String.Join(vbCrLf, Reverse.Dependents.Take(8).Select(Function(Item) Item.Addon.Manifest.DisplayName & " · " & Item.Addon.Path)) &
                                     If(Reverse.Dependents.Count > 8, vbCrLf & $"另有 {Reverse.Dependents.Count - 8} 项未显示。", "")) &
                                  vbCrLf & "按 UUID 保守匹配，未核验版本；缺失包和未发现目录的依赖无法确认。"
                If Reverse.IncompleteRoots.Count > 0 Then DependencyText &= vbCrLf & "部分候选附加包无法读取，反向依赖检查不完整。"
            End If
            Using Dialog As New System.Windows.Forms.FolderBrowserDialog With {.Description = "选择游戏数据目录之外的世界及配置备份文件夹", .ShowNewFolderButton = True}
                If Dialog.ShowDialog() <> System.Windows.Forms.DialogResult.OK Then
                    LabWorldAddonStatus.Text = "已取消修改。"
                    Return
                End If
                Dim BackupParent = Path.GetFullPath(Dialog.SelectedPath).TrimEnd(Path.DirectorySeparatorChar)
                If Roots.Any(Function(Item) IsWithinRoot(Item.Path, BackupParent)) Then Throw New InvalidOperationException("备份不能位于候选游戏数据目录内。")
                If MyMsgBox("请确认游戏已关闭且同步完成。此功能尚未验证真实游戏对配置修改的处理。" & vbCrLf & vbCrLf &
                            "世界：" & World.Name & vbCrLf & "世界目录：" & World.Path & vbCrLf & "账户目录：" & Root.Path & vbCrLf &
                            "附加包：" & Pack.Name & "　" & Pack.Version & vbCrLf & "UUID：" & Pack.Uuid.ToString() & vbCrLf &
                            If(Choice.IsWorldReference, "此条目来自世界配置，包本身可能已不存在；只会停用引用。" & vbCrLf, "") &
                            "操作：" & If(Enable, "启用", "停用") & vbCrLf & "备份目录：" & BackupParent & vbCrLf &
                            "依赖检查（不代表世界已启用依赖）：" & vbCrLf & DependencyText & vbCrLf &
                            "将先创建并校验完整世界备份，再备份和修改配置；游戏及云同步可能仍影响结果。", "确认实验性世界配置", "继续", "取消") <> 1 Then
                    LabWorldAddonStatus.Text = "已取消修改。"
                    Return
                End If
                FullBackup = Path.Combine(BackupParent, "PCL-WorldBeforePack-" & Guid.NewGuid().ToString("N") & ".zip")
                LabWorldAddonStatus.Text = "正在备份世界，并重新检查后修改配置…"
                Dim Change = Await Task.Run(Function()
                                                If IsKnownBedrockProcessRunning() Then Throw New InvalidOperationException("Minecraft 正在运行，请关闭后重试。")
                                                Dim CurrentRoots = ValidateInstallTarget(Installation, Root)
                                                If Not SameRoots(Roots, CurrentRoots) Then Throw New InvalidOperationException("数据目录范围已变化，请重新确认。")
                                                Using LockScope = BedrockOperationScope.Acquire(CurrentRoots)
                                                    If IsKnownBedrockProcessRunning() Then Throw New InvalidOperationException("Minecraft 正在运行，请关闭后重试。")
                                                    Dim LockedRoots = ValidateInstallTarget(Installation, Root)
                                                    If Not SameRoots(CurrentRoots, LockedRoots) Then Throw New InvalidOperationException("数据目录范围已变化，请重新确认。")
                                                    BedrockWorldBackup.Create(Root, World, FullBackup, LockedRoots)
                                                    If IsKnownBedrockProcessRunning() Then Throw New InvalidOperationException("Minecraft 正在运行，请关闭后重试。")
                                                    Dim AfterBackupRoots = ValidateInstallTarget(Installation, Root)
                                                    If Not SameRoots(LockedRoots, AfterBackupRoots) Then Throw New InvalidOperationException("数据目录范围已变化，请重新确认。")
                                                    If Reverse IsNot Nothing Then
                                                        Dim CurrentReverse = BedrockWorldDependents.Check(LockedRoots, Root, World, Pack.Uuid)
                                                        If Not BedrockWorldDependents.Equivalent(Reverse, CurrentReverse) Then Throw New InvalidOperationException("世界配置或反向依赖已变化，请重新确认。")
                                                    End If
                                                    If Not Choice.IsWorldReference Then
                                                        Dim CurrentPack = ValidateWorldAddon(Choice, LockedRoots)
                                                        If CurrentPack.Fingerprint <> Preview.Fingerprint Then Throw New InvalidOperationException("附加包内容已变化，请重新确认。")
                                                        Pack = CurrentPack.Pack
                                                    End If
                                                    If Enable Then
                                                        Dim CurrentReport = BedrockDependencyCheck.Analyze(Pack, LockedRoots)
                                                        If Not BedrockDependencyCheck.Equivalent(Report, CurrentReport) Then Throw New InvalidOperationException("附加包依赖检查结果已变化，请重新确认。")
                                                        If WorldDependencies IsNot Nothing Then
                                                            Dim CurrentWorldDependencies = BedrockWorldDependencyCheck.Check(Root, World, Pack)
                                                            If Not BedrockWorldDependencyCheck.Equivalent(WorldDependencies, CurrentWorldDependencies) Then Throw New InvalidOperationException("世界依赖配置已变化，请重新确认。")
                                                        End If
                                                    End If
                                                    Return BedrockWorldPackEditor.SetEnabled(Root, World, Snapshot, Pack, Enable, BackupParent)
                                                End Using
                                            End Function)
                LabWorldAddonStatus.Text = If(Change.BackupPath Is Nothing, "配置没有变化。", "配置已写入；游戏是否识别需自行确认。") &
                                           vbCrLf & "世界备份：" & FullBackup & If(Change.BackupPath Is Nothing, "", vbCrLf & "配置备份：" & Change.BackupPath) &
                                           If(Change.DisplacedPath Is Nothing, "", vbCrLf & "原配置副本：" & Change.DisplacedPath) &
                                           If(Change.StagePath Is Nothing, "", vbCrLf & "暂存目录：" & Change.StagePath) & vbCrLf & "请重新读取世界列表。"
            End Using
        Catch ex As Exception
            LabWorldAddonStatus.Text = "世界配置操作未完成：" & ex.Message & If(FullBackup Is Nothing, "", vbCrLf & "世界备份路径：" & FullBackup)
            Logger.Warn(ex, "实验性世界附加包配置失败")
        Finally
            SetBusy(False)
        End Try
    End Function

    Private Shared Function ValidateWorldAddon(Choice As WorldAddonChoice, Roots As IReadOnlyList(Of BedrockDataRoot)) As BedrockDirectoryPreview
        Dim Pack = Choice.Addon.Manifest
        Dim Result = BedrockAddonScope.Check(Roots, {Pack.Uuid})
        If Not Result.Complete OrElse Result.Conflicts.Count <> 1 OrElse
           Not String.Equals(Result.Conflicts(0).Addon.Path, Choice.Addon.Path, StringComparison.OrdinalIgnoreCase) Then
            Throw New InvalidOperationException("附加包冲突或候选目录无法完整检查，请重新读取。")
        End If
        Dim Preview = BedrockDirectoryInstaller.Preview(Choice.Addon.Path)
        If Preview.Pack.Uuid <> Pack.Uuid OrElse Preview.Pack.Version <> Pack.Version Then Throw New InvalidOperationException("附加包已变化，请重新读取。")
        Return Preview
    End Function

    Private Async Sub RestoreWorldConfigClicked(sender As Object, e As EventArgs) Handles BtnRestoreWorldConfig.Click
        Dim Installation = TryCast(TryCast(ComboInstall.SelectedItem, MyComboBoxItem)?.Tag, BedrockInstallation)
        Dim Root = TryCast(TryCast(ComboRoots.SelectedItem, MyComboBoxItem)?.Tag, BedrockDataRoot)
        Dim World = TryCast(TryCast(ComboWorlds.SelectedItem, MyComboBoxItem)?.Tag, BedrockWorldInfo)
        If IsBusy OrElse Not IsEligibleWorldTarget(Installation, Root, World) Then Return
        If IsKnownBedrockProcessRunning() Then
            MyMsgBox("请先关闭 Minecraft 基岩版并等待同步完成。", "恢复世界配置")
            Return
        End If
        Dim Dialog As New Microsoft.Win32.OpenFileDialog With {.Filter = "PCL 配置备份 (*.json)|*.json", .CheckFileExists = True, .Multiselect = False}
        If Dialog.ShowDialog() <> True Then Return
        Dim Backup = Dialog.FileName
        Dim FullBackup As String = Nothing
        SetBusy(True)
        Try
            Dim Roots = Await Task.Run(Function() ValidateInstallTarget(Installation, Root))
            If Roots.Any(Function(Item) IsWithinRoot(Item.Path, Backup)) Then Throw New InvalidOperationException("请选择候选游戏数据目录之外的配置备份。")
            FullBackup = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(Backup)), "PCL-WorldBeforeConfigRestore-" & Guid.NewGuid().ToString("N") & ".zip")
            If MyMsgBox("请确认游戏已关闭且同步完成。" & vbCrLf & "世界：" & World.Name & vbCrLf & "世界目录：" & World.Path & vbCrLf &
                        "配置备份：" & Backup & vbCrLf & "恢复前世界备份：" & FullBackup & vbCrLf &
                        "先备份整个世界；仅当当前配置与备份记录的修改结果一致时才恢复，恢复前的配置会保留。", "确认恢复世界配置", "恢复", "取消") <> 1 Then
                LabWorldAddonStatus.Text = "已取消恢复。"
                Return
            End If
            LabWorldAddonStatus.Text = "正在备份世界，并校验后恢复配置…"
            Dim Preserved = Await Task.Run(Function()
                                               If IsKnownBedrockProcessRunning() Then Throw New InvalidOperationException("Minecraft 正在运行，请关闭后重试。")
                                               Dim CurrentRoots = ValidateInstallTarget(Installation, Root)
                                               If Not SameRoots(Roots, CurrentRoots) Then Throw New InvalidOperationException("数据目录范围已变化，请重新确认。")
                                               Using LockScope = BedrockOperationScope.Acquire(CurrentRoots)
                                                   If IsKnownBedrockProcessRunning() Then Throw New InvalidOperationException("Minecraft 正在运行，请关闭后重试。")
                                                   Dim LockedRoots = ValidateInstallTarget(Installation, Root)
                                                   If Not SameRoots(CurrentRoots, LockedRoots) Then Throw New InvalidOperationException("数据目录范围已变化，请重新确认。")
                                                   BedrockWorldBackup.Create(Root, World, FullBackup, LockedRoots)
                                                   If IsKnownBedrockProcessRunning() Then Throw New InvalidOperationException("Minecraft 正在运行，请关闭后重试。")
                                                   Dim AfterBackupRoots = ValidateInstallTarget(Installation, Root)
                                                   If Not SameRoots(LockedRoots, AfterBackupRoots) Then Throw New InvalidOperationException("数据目录范围已变化，请重新确认。")
                                                   Return BedrockWorldPackEditor.Restore(Root, World, Backup)
                                               End Using
                                           End Function)
            LabWorldAddonStatus.Text = "配置已恢复；游戏是否识别需自行确认。" & vbCrLf & "世界备份：" & FullBackup & vbCrLf &
                                       "恢复前副本：" & Preserved & vbCrLf & "请重新读取世界列表。"
        Catch ex As Exception
            LabWorldAddonStatus.Text = "恢复未完成：" & ex.Message & If(FullBackup Is Nothing, "", vbCrLf & "世界备份路径：" & FullBackup)
            Logger.Warn(ex, "恢复世界附加包配置失败")
        Finally
            SetBusy(False)
        End Try
    End Sub

    Private Shared Function IsEligibleWorldTarget(Installation As BedrockInstallation, Root As BedrockDataRoot, World As BedrockWorldInfo) As Boolean
        Return BedrockCapabilities.CanEditWorldPacks(Installation, Root, World)
    End Function

    Private Sub RefreshWorldAddonButtons()
        If BtnReadWorldAddons Is Nothing OrElse ComboWorldAddons Is Nothing OrElse ComboInstall Is Nothing OrElse ComboRoots Is Nothing OrElse ComboWorlds Is Nothing Then Return
        Dim Installation = TryCast(TryCast(ComboInstall.SelectedItem, MyComboBoxItem)?.Tag, BedrockInstallation)
        Dim Root = TryCast(TryCast(ComboRoots.SelectedItem, MyComboBoxItem)?.Tag, BedrockDataRoot)
        Dim World = TryCast(TryCast(ComboWorlds.SelectedItem, MyComboBoxItem)?.Tag, BedrockWorldInfo)
        Dim Choice = TryCast(TryCast(ComboWorldAddons.SelectedItem, MyComboBoxItem)?.Tag, WorldAddonChoice)
        Dim CanRead = Not IsBusy AndAlso Installation IsNot Nothing AndAlso Root IsNot Nothing AndAlso Root.SupportsWorlds AndAlso World IsNot Nothing
        Dim CanWrite = CanRead AndAlso IsEligibleWorldTarget(Installation, Root, World)
        BtnReadWorldAddons.IsEnabled = CanRead
        BtnRestoreWorldConfig.IsEnabled = CanWrite
        ComboWorldAddons.IsEnabled = CanRead AndAlso ComboWorldAddons.Items.Count > 0
        BtnWorldEnableAddon.IsEnabled = CanWrite AndAlso Not WorldAddonScanIncomplete AndAlso Choice IsNot Nothing AndAlso Not Choice.IsWorldReference
        BtnWorldDisableAddon.IsEnabled = CanWrite AndAlso Choice IsNot Nothing AndAlso (Choice.IsWorldReference OrElse Not WorldAddonScanIncomplete)
    End Sub

    Private Async Sub BackupClicked(sender As Object, e As EventArgs) Handles BtnBackup.Click
        Dim Installation = TryCast(TryCast(ComboInstall.SelectedItem, MyComboBoxItem)?.Tag, BedrockInstallation)
        Dim RootItem = TryCast(ComboRoots.SelectedItem, MyComboBoxItem)
        Dim WorldItem = TryCast(ComboWorlds.SelectedItem, MyComboBoxItem)
        Dim Root = TryCast(RootItem?.Tag, BedrockDataRoot)
        Dim World = TryCast(WorldItem?.Tag, BedrockWorldInfo)
        If IsBusy OrElse Installation Is Nothing OrElse Root Is Nothing OrElse World Is Nothing Then Return
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
                                          Dim Roots = ValidateInstallTarget(Installation, Root)
                                          Return BedrockWorldBackup.Create(Root, World, BackupPath, Roots)
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
        Dim Installation = TryCast(TryCast(ComboInstall.SelectedItem, MyComboBoxItem)?.Tag, BedrockInstallation)
        Dim RootItem = TryCast(ComboRoots.SelectedItem, MyComboBoxItem)
        Dim Root = TryCast(RootItem?.Tag, BedrockDataRoot)
        If IsBusy OrElse Installation Is Nothing OrElse Root Is Nothing Then Return
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
            Dim Roots = Await Task.Run(Function() ValidateInstallTarget(Installation, Root))
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
                                              Dim CurrentRoots = ValidateInstallTarget(Installation, Root)
                                              If Not SameRoots(Roots, CurrentRoots) Then Throw New InvalidOperationException("数据目录范围已变化，请重新确认。")
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

    Private Sub FolderClicked(sender As Object, e As EventArgs) Handles BtnFolder.Click
        Using Dialog As New System.Windows.Forms.FolderBrowserDialog With {.Description = "选择包含 manifest.json 的附加包文件夹", .ShowNewFolderButton = False}
            If Dialog.ShowDialog() <> System.Windows.Forms.DialogResult.OK Then Return
            PreviewPackageFolder(Dialog.SelectedPath)
        End Using
    End Sub

    Public Async Sub PreviewPackageFolder(Folder As String)
        If IsBusy Then Return
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
            MyMsgBox("请先关闭 Minecraft 基岩版并等待同步完成。", "安装附加包")
            Return
        End If
        SetBusy(True)
        Dim Transaction As String = Nothing
        Try
            Dim Roots = Await Task.Run(Function() ValidateInstallTarget(Installation, Root))
            LabPackInfo.Text = "正在准备并校验附加包…"
            Dim Packs = Await Task.Run(Function() PrepareSupportedPacks(Source, IsDirectory, Roots))
            LabPackInfo.Text = "正在检查附加包依赖…"
            Dim Dependencies = Await Task.Run(Function() DescribeDependencies(Packs, Roots))
            Dim DependencyText = If(Dependencies.Count = 0, "无声明依赖。", String.Join(vbCrLf, Dependencies.Take(12)) &
                                    If(Dependencies.Count > 12, vbCrLf & $"另有 {Dependencies.Count - 12} 项未显示，请检查包的完整依赖。", ""))
            Dim Scope = String.Join(vbCrLf, Roots.Select(Function(Item) Item.Path))
            If MyMsgBox("请确认游戏已关闭且同步完成。仅在一台设备上观察到资源包和行为包；界面安装流程尚未实测。" & vbCrLf & vbCrLf &
                        "目标目录：" & Root.Path & vbCrLf & "检查目录：" & vbCrLf & Scope & vbCrLf & vbCrLf &
                        DescribePacks(Packs.Select(Function(Item) Item.Preview.Pack)) & vbCrLf & "依赖检查：" & vbCrLf & DependencyText & vbCrLf &
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
            LabPackInfo.Text = "正在提交附加包；事务目录：" & Transaction
            Await Task.Run(Sub()
                               If IsKnownBedrockProcessRunning() Then Throw New InvalidOperationException("Minecraft 正在运行，请关闭后重试。")
                               Dim CurrentRoots = ValidateInstallTarget(Installation, Root)
                               If Not SameRoots(Roots, CurrentRoots) Then Throw New InvalidOperationException("数据目录范围已变化，请重新确认。")
                               BedrockAddonTransaction.Commit(Root, Transaction, CurrentRoots)
                           End Sub)
            LabPackInfo.Text = "附加包文件已放置。游戏是否识别或启用需在游戏中确认。事务目录：" & Transaction
        Catch ex As Exception
            LabPackInfo.Text = "安装未完成：" & ex.Message & If(Transaction Is Nothing, "", vbCrLf & "保留事务并在数据页面检查恢复：" & Transaction)
            Logger.Warn(ex, "实验性基岩版附加包安装失败")
        Finally
            SetBusy(False)
        End Try
    End Sub

    Private Shared Function PrepareSupportedPacks(Source As String, IsDirectory As Boolean, Roots As IReadOnlyList(Of BedrockDataRoot)) As IReadOnlyList(Of BedrockPreparedPack)
        Dim Packs As IReadOnlyList(Of BedrockPreparedPack)
        If IsDirectory Then
            Packs = {New BedrockPreparedPack(Source, BedrockDirectoryInstaller.Preview(Source))}
        ElseIf Path.GetExtension(Source).Equals(".mcpack", StringComparison.OrdinalIgnoreCase) Then
            Packs = {BedrockArchivePreparer.PrepareMcpack(Source, Path.GetTempPath(), Roots)}
        ElseIf Path.GetExtension(Source).Equals(".mcaddon", StringComparison.OrdinalIgnoreCase) Then
            Packs = BedrockArchivePreparer.PrepareMcaddon(Source, Path.GetTempPath(), Roots).Packs
        Else
            Throw New InvalidDataException("不支持的附加包格式。")
        End If
        If Packs.Count > 16 Then Throw New InvalidDataException("每次最多安装 16 个包；准备目录已保留：" & vbCrLf &
                                                                  String.Join(vbCrLf, Packs.Select(Function(Item) Item.Directory)))
        If Packs.Any(Function(Item) Item.Preview.Pack.Modules.Contains("resources") = Item.Preview.Pack.Modules.Contains("data")) Then
            Throw New InvalidDataException("每个包必须能够明确区分为资源包或行为包；准备目录已保留：" &
                                           vbCrLf & String.Join(vbCrLf, Packs.Select(Function(Item) Item.Directory)))
        End If
        Return Packs
    End Function

    Private Shared Function DescribeDependencies(Packs As IReadOnlyList(Of BedrockPreparedPack), Roots As IReadOnlyList(Of BedrockDataRoot)) As IReadOnlyList(Of String)
        Dim Planned = Packs.Select(Function(Item) Item.Preview.Pack).ToArray()
        Dim Lines As New List(Of String)
        For Each Pack In Planned
            If Pack.Dependencies.Count = 0 Then Continue For
            Dim Report = BedrockDependencyCheck.Analyze(Pack, Roots, Planned)
            If Report.IncompleteRoots.Count > 0 Then Lines.Add(Pack.Name & "：部分目录未完整读取。")
            For Each Dependency In Report.Results
                Lines.Add(Pack.Name & " → " & Dependency.Dependency & "：" & Dependency.Status)
            Next
        Next
        Return Lines
    End Function

    Private Function ValidateInstallTarget(Installation As BedrockInstallation, Root As BedrockDataRoot,
                                           Optional RequireComplete As Boolean = True) As IReadOnlyList(Of BedrockDataRoot)
        Dim Result = Service.Detect()
        If BedrockService.MatchInstallation(Result, Installation, RequireComplete) Is Nothing Then
            Throw New InvalidOperationException("游戏注册状态已变化，请刷新后重试。")
        End If
        Dim Roots = (New BedrockDataRootFinder()).Find(Installation)
        If Not Roots.Any(Function(Item) String.Equals(Item.Path, Root.Path, StringComparison.OrdinalIgnoreCase) AndAlso Item.Kind = Root.Kind AndAlso Item.SupportsWorlds = Root.SupportsWorlds) Then
            Throw New InvalidOperationException("目标数据目录已变化，请重新选择。")
        End If
        Return Roots
    End Function

    Private Shared Function SameRoots(First As IReadOnlyList(Of BedrockDataRoot), Second As IReadOnlyList(Of BedrockDataRoot)) As Boolean
        Return First.Count = Second.Count AndAlso First.All(Function(Item) Second.Any(Function(Other) String.Equals(Item.Path, Other.Path, StringComparison.OrdinalIgnoreCase) AndAlso
                                                                                                  Item.Kind = Other.Kind AndAlso Item.SupportsWorlds = Other.SupportsWorlds))
    End Function

    Private Shared Function IsValidatedResourceTarget(Installation As BedrockInstallation, Root As BedrockDataRoot) As Boolean
        Return BedrockCapabilities.CanManageResourcePacks(Installation, Root)
    End Function

    Private Sub RefreshInstallPackButton()
        If BtnInstallPack Is Nothing OrElse ComboInstall Is Nothing OrElse ComboRoots Is Nothing Then Return
        Dim Installation = TryCast(TryCast(ComboInstall.SelectedItem, MyComboBoxItem)?.Tag, BedrockInstallation)
        Dim Root = TryCast(TryCast(ComboRoots.SelectedItem, MyComboBoxItem)?.Tag, BedrockDataRoot)
        BtnInstallPack.IsEnabled = Not IsBusy AndAlso Not String.IsNullOrEmpty(SelectedPackPath) AndAlso IsValidatedResourceTarget(Installation, Root)
        If LabInstallHint Is Nothing Then Return
        If String.IsNullOrEmpty(SelectedPackPath) OrElse Root Is Nothing Then
            LabInstallHint.Text = "请先预览附加包，并在数据与世界中选择目标目录。"
        ElseIf Not IsValidatedResourceTarget(Installation, Root) Then
            LabInstallHint.Text = "此实验功能仅开放给正式版 x64 包 1.26.5203.0 的 GDK 共享目录；其他安装类型和版本尚待验证。"
        Else
            LabInstallHint.Text = "支持类型明确的资源包和行为包；安装前将校验内容与目标，并要求确认游戏已关闭且同步完成。"
        End If
    End Sub

    Private Shared Function DescribePacks(Packs As IEnumerable(Of BedrockPackInfo)) As String
        Dim Lines As New List(Of String)
        For Each Pack In Packs
            Dim Description = If(Pack.Description.Length > 300, Pack.Description.Substring(0, 300) & "…", Pack.Description)
            Dim DisplayDescription = If(Pack.DisplayDescription.Length > 300, Pack.DisplayDescription.Substring(0, 300) & "…", Pack.DisplayDescription)
            Lines.Add($"清单名称：{Pack.Name}（可能为本地化键）　版本：{Pack.Version}" & vbCrLf &
                      If(Description.Length = 0, "", "清单描述：" & Description & "（可能为本地化键）" & vbCrLf) &
                      If(Pack.DisplayLanguage Is Nothing OrElse Pack.DisplayName = Pack.Name, "", $"名称候选（{Pack.DisplayLanguage}）：{Pack.DisplayName}" & vbCrLf) &
                      If(Pack.DisplayLanguage Is Nothing OrElse DisplayDescription = Description OrElse DisplayDescription.Length = 0, "", $"描述候选（{Pack.DisplayLanguage}）：{DisplayDescription}" & vbCrLf) &
                      $"UUID：{Pack.Uuid}　类型：{String.Join(", ", Pack.Modules)}" & vbCrLf &
                      If(Pack.Modules.Contains("resources"), "资源包使用范围：" & Pack.PackScope & vbCrLf, "") &
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
        BtnRestoreRemoval.IsEnabled = Not Value AndAlso CanRestoreRemoval(Removal)
        ComboTransactions.IsEnabled = Not Value AndAlso ComboTransactions.Items.Count > 0
        Dim TransactionItem = TryCast(ComboTransactions.SelectedItem, MyComboBoxItem)
        Dim Transaction = TryCast(TransactionItem?.Tag, BedrockAddonTransactionInfo)
        BtnRollbackTransaction.IsEnabled = Not Value AndAlso Transaction IsNot Nothing AndAlso Transaction.Error Is Nothing AndAlso Transaction.Installed > 0
        ComboAddons.IsEnabled = Not Value AndAlso ComboAddons.Items.Count > 0
        Dim SelectedAddon = TryCast(TryCast(ComboAddons.SelectedItem, MyComboBoxItem)?.Tag, BedrockInstalledAddon)
        BtnCheckDependencies.IsEnabled = Not Value AndAlso SelectedAddon IsNot Nothing AndAlso SelectedAddon.Manifest IsNot Nothing AndAlso SelectedAddon.Manifest.Dependencies.Count > 0
        RefreshRemoveAddonButton()
        ComboWorlds.IsEnabled = Not Value AndAlso ComboWorlds.Items.Count > 0
        Dim WorldItem = TryCast(ComboWorlds.SelectedItem, MyComboBoxItem)
        Dim World = TryCast(WorldItem?.Tag, BedrockWorldInfo)
        BtnBackup.IsEnabled = Not Value AndAlso World IsNot Nothing AndAlso World.HasLevelData
        RefreshInstallPackButton()
        RefreshWorldAddonButtons()
    End Sub
End Class
