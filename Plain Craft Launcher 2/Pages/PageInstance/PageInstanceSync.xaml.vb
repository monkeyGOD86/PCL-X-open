Imports PCLCS.PackSync
Imports System.Threading.Tasks

Public Class PageInstanceSync
    Private SelectedPackagePath As String
    Private SelectedPackageHash As String
    Private SelectedInstance As McInstance
    Private CurrentPreview As PackSyncPreviewResult
    Private CurrentPlan As PackSyncOperationPlan
    Private PreparationControl As PackSyncPreparationControl
    Private ReadOnly Choices As New Dictionary(Of String, ComboBox)(StringComparer.OrdinalIgnoreCase)

    Private Sub PageInstanceSync_Loaded() Handles Me.Loaded
        If SelectedInstance IsNot PageInstanceLeft.Instance Then ResetPage()
        RefreshRecovery()
    End Sub

    Private Sub RefreshRecovery()
        ComboRecovery.Items.Clear()
        BtnRecover.IsEnabled = False
        BtnUndo.IsEnabled = False
        Try
            If PageInstanceLeft.Instance Is Nothing Then Return
            Dim instance = PageInstanceLeft.Instance
            Dim pending = PackSyncTransaction.FindPending(instance.PathVersion)
            For Each entry In pending
                ComboRecovery.Items.Add(New ComboBoxItem With {.Content = entry.TransactionId & "  " & If(entry.Error, entry.Status), .Tag = entry.TransactionId, .IsEnabled = entry.Error Is Nothing})
            Next
            ComboRecovery.Visibility = If(pending.Count > 0, Visibility.Visible, Visibility.Collapsed)
            If pending.Count > 0 Then
                ComboRecovery.SelectedIndex = 0
                BtnRecover.IsEnabled = pending(0).Error Is Nothing
                LabRecovery.Text = "存在未完成事务，请先恢复；损坏的事务请保留备份并检查。"
            Else
                Dim state = PackSyncPreview.ReadState(instance.PathVersion)
                BtnUndo.IsEnabled = state IsNot Nothing AndAlso Not String.IsNullOrEmpty(state.LastTransactionId)
                LabRecovery.Text = If(BtnUndo.IsEnabled, "可以撤销最近一次同步。", "没有需要恢复的事务。")
            End If
        Catch ex As Exception
            LabRecovery.Text = "无法读取恢复信息：" & ex.Message
        End Try
    End Sub

    Private Sub RecoverySelectionChanged() Handles ComboRecovery.SelectionChanged
        Dim item = TryCast(ComboRecovery.SelectedItem, ComboBoxItem)
        BtnRecover.IsEnabled = item IsNot Nothing AndAlso item.IsEnabled
    End Sub

    Private Sub OpenBackupFolder() Handles BtnBackupFolder.Click
        If PageInstanceLeft.Instance Is Nothing Then Return
        Dim folder = Path.Combine(PageInstanceLeft.Instance.PathVersion, "PCL", "PackSync", "transactions")
        If Directory.Exists(folder) Then OpenExplorer(folder) Else Hint("尚无同步备份。")
    End Sub

    Private Sub Recover() Handles BtnRecover.Click
        Dim item = TryCast(ComboRecovery.SelectedItem, ComboBoxItem)
        If item IsNot Nothing AndAlso item.IsEnabled Then StartRecovery(CStr(item.Tag), False)
    End Sub

    Private Sub Undo() Handles BtnUndo.Click
        Try
            Dim state = PackSyncPreview.ReadState(PageInstanceLeft.Instance.PathVersion)
            If state IsNot Nothing AndAlso Not String.IsNullOrEmpty(state.LastTransactionId) Then StartRecovery(state.LastTransactionId, True)
        Catch ex As Exception
            LabRecovery.Text = ex.Message
        End Try
    End Sub

    Private Sub StartRecovery(id As String, isUndo As Boolean)
        If MyMsgBox("请先关闭此实例的游戏，包括通过其他启动器启动的游戏。确认继续？", If(isUndo, "撤销上次同步", "恢复未完成事务"), "继续", "取消") <> 1 Then Return
        Dim instance = PageInstanceLeft.Instance
        BtnRecover.IsEnabled = False
        BtnUndo.IsEnabled = False
        BtnSelect.IsEnabled = False
        BtnImportNew.IsEnabled = False
        BtnPreview.IsEnabled = False
        BtnPlan.IsEnabled = False
        BtnApply.IsEnabled = False
        ComboRecovery.IsEnabled = False
        PanOptional.IsEnabled = False
        PanDifferences.IsEnabled = False
        LabRecovery.Text = "正在恢复原文件…"
        Try
            If Not PackSyncRunner.StartRecovery(instance, id, isUndo,
                Sub(err) RunInUi(Sub()
                                     BtnSelect.IsEnabled = True
                                     BtnImportNew.IsEnabled = SelectedPackagePath IsNot Nothing
                                     BtnPreview.IsEnabled = SelectedPackagePath IsNot Nothing
                                     ComboRecovery.IsEnabled = True
                                     PanOptional.IsEnabled = True
                                     PanDifferences.IsEnabled = True
                                     ClearPreview()
                                     If err IsNot Nothing Then
                                         RefreshRecovery()
                                         LabRecovery.Text = "恢复未完成：" & err.GetDisplay(True)
                                         LabResult.Text = "请重新检查差异。"
                                     Else
                                         RefreshRecovery()
                                         LabRecovery.Text = "恢复完成。"
                                         LabResult.Text = "恢复完成，请重新检查差异。"
                                         FrmInstanceMod?.ReloadModList(True)
                                     End If
                                 End Sub)) Then Throw New IOException("该实例正在被修改。")
        Catch ex As Exception
            BtnSelect.IsEnabled = True
            BtnImportNew.IsEnabled = SelectedPackagePath IsNot Nothing
            BtnPreview.IsEnabled = SelectedPackagePath IsNot Nothing
            ComboRecovery.IsEnabled = True
            PanOptional.IsEnabled = True
            PanDifferences.IsEnabled = True
            RefreshRecovery()
            LabRecovery.Text = ex.Message
        End Try
    End Sub

    Public Sub ResetPage()
        SelectedInstance = PageInstanceLeft.Instance
        SelectedPackagePath = Nothing
        SelectedPackageHash = Nothing
        TextPackagePath.Text = ""
        BtnImportNew.IsEnabled = False
        LabPackage.Text = "尚未选择整合包"
        LabOptional.Visibility = Visibility.Collapsed
        PanOptional.Children.Clear()
        BtnPreview.IsEnabled = False
        LabResult.Text = ""
        ClearPreview()
    End Sub

    Private Sub ClearPreview()
        CurrentPreview = Nothing
        CurrentPlan = Nothing
        Choices.Clear()
        PanDifferences.Children.Clear()
        BtnPlan.IsEnabled = False
        BtnApply.IsEnabled = False
        LabPlan.Text = ""
    End Sub

    Private Sub OptionalChanged(sender As Object, e As RoutedEventArgs)
        ClearPreview()
        LabResult.Text = "可选文件选择已更改，请重新检查差异。"
    End Sub

    Private Sub PackagePathChanged() Handles TextPackagePath.TextChanged
        If LabPackage Is Nothing OrElse BtnPreview Is Nothing OrElse PanOptional Is Nothing OrElse PanDifferences Is Nothing Then Return
        SelectedPackagePath = Nothing
        SelectedPackageHash = Nothing
        LabPackage.Text = "尚未读取整合包"
        BtnImportNew.IsEnabled = False
        BtnPreview.IsEnabled = False
        LabOptional.Visibility = Visibility.Collapsed
        PanOptional.Children.Clear()
        LabResult.Text = "路径已更改，请读取整合包。"
        ClearPreview()
    End Sub

    Private Async Sub SelectPackage() Handles BtnSelect.Click
        Dim dialog As New Microsoft.Win32.OpenFileDialog With {.Filter = "Modrinth 整合包 (*.mrpack)|*.mrpack", .Title = "选择小队整合包"}
        If dialog.ShowDialog() <> True Then Return
        TextPackagePath.Text = dialog.FileName
        Await LoadPackage(dialog.FileName)
    End Sub

    Private Async Sub LoadPackagePath() Handles BtnLoadPath.Click
        Dim archivePath = TextPackagePath.Text.Trim().Trim(""""c)
        If String.IsNullOrWhiteSpace(archivePath) Then
            LabResult.Text = "请先输入整合包路径。"
            Return
        End If
        Await LoadPackage(archivePath)
    End Sub

    Private Async Function LoadPackage(archivePath As String) As Task
        Dim instance = PageInstanceLeft.Instance
        BtnSelect.IsEnabled = False
        BtnPreview.IsEnabled = False
        SelectedPackagePath = Nothing
        SelectedPackageHash = Nothing
        BtnImportNew.IsEnabled = False
        PanOptional.Children.Clear()
        LabOptional.Visibility = Visibility.Collapsed
        ClearPreview()
        LabResult.Text = "正在读取整合包…"
        Try
            Dim package = Await Task.Run(Function() PackSyncReader.Read(archivePath))
            If instance IsNot PageInstanceLeft.Instance Then ResetPage() : Return
            Dim state As PackSyncState = Nothing
            Dim unsupportedState As String = Nothing
            Try
                state = PackSyncPreview.ReadState(instance.PathVersion)
            Catch ex As PackSyncCorruptStateException
                LabRecovery.Text = ex.Message
            Catch ex As NotSupportedException
                unsupportedState = ex.Message
            End Try
            Dim remembered As New HashSet(Of String)(If(state?.OptionalSelections, New List(Of String)), StringComparer.OrdinalIgnoreCase)
            SelectedInstance = instance
            SelectedPackagePath = Path.GetFullPath(archivePath)
            SelectedPackageHash = package.ArchiveSha512
            BtnImportNew.IsEnabled = True
            LabPackage.Text = $"{package.Name}  ·  {package.ReleaseId}  ·  {package.PackId}"
            For Each file In package.Files.Where(Function(f) f.Optional)
                Dim optionBox As New CheckBox With {.Content = file.Path, .Tag = file.Path, .IsChecked = state IsNot Nothing AndAlso state.PackId = package.PackId AndAlso remembered.Contains(file.Path), .Margin = New Thickness(0, 3, 0, 3)}
                AddHandler optionBox.Checked, AddressOf OptionalChanged
                AddHandler optionBox.Unchecked, AddressOf OptionalChanged
                PanOptional.Children.Add(optionBox)
            Next
            LabOptional.Visibility = If(PanOptional.Children.Count > 0, Visibility.Visible, Visibility.Collapsed)
            LabResult.Text = If(unsupportedState, "请选择需要的可选文件，然后检查差异。")
            BtnPreview.IsEnabled = unsupportedState Is Nothing
        Catch ex As Exception
            LabPackage.Text = "无法读取所选整合包"
            LabResult.Text = ex.Message
        Finally
            BtnSelect.IsEnabled = True
        End Try
    End Function

    Private Sub ImportNewInstance() Handles BtnImportNew.Click
        If SelectedPackagePath Is Nothing OrElse SelectedPackageHash Is Nothing Then Return
        Dim archivePath = SelectedPackagePath
        Dim expectedHash = SelectedPackageHash
        If MyMsgBox("将整合包安装到新实例。安装完成后，请在新实例的小队同步页面使用同一包核对并绑定。", "导入新实例", "继续", "取消") <> 1 Then Return
        RunInThread(
        Sub()
            Try
                'The installer reads the archive again asynchronously; give it a private,
                'verified task copy so a replacement of the selected file cannot change the install.
                Dim stagedArchive = Path.Combine(RequestTaskTempFolder(), "team.mrpack")
                Using source As New FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read)
                    Using destination As New FileStream(stagedArchive, FileMode.CreateNew, FileAccess.Write, FileShare.None)
                        source.CopyTo(destination)
                        destination.Flush(True)
                    End Using
                End Using
                Dim current = PackSyncReader.Read(stagedArchive)
                If Not String.Equals(current.ArchiveSha512, expectedHash, StringComparison.OrdinalIgnoreCase) Then Throw New IOException("整合包已变化，请重新选择。")
                ModpackInstall(stagedArchive)
            Catch ex As Exception
                If Not ex.IsCanceled Then Logger.Error(ex, "导入小队整合包失败", LogBehavior.Alert)
            End Try
        End Sub)
    End Sub

    Private Async Sub Preview() Handles BtnPreview.Click
        Dim instance = PageInstanceLeft.Instance
        Dim archivePath = SelectedPackagePath
        If instance Is Nothing OrElse archivePath Is Nothing Then Return
        Dim environment As New Dictionary(Of String, String) From {{"minecraft", instance.Version.VanillaName}}
        For Each pair In { (ModLoaders.Forge, "forge"), (ModLoaders.Fabric, "fabric-loader"), (ModLoaders.NeoForge, "neoforge") }
            If instance.Version.ModLoaders.HasFlagF(pair.Item1) Then environment.Add(pair.Item2, instance.Version.ModLoaderVersions(pair.Item1))
        Next
        Dim selected = PanOptional.Children.OfType(Of CheckBox)().Where(Function(c) c.IsChecked = True).Select(Function(c) CStr(c.Tag)).ToArray()
        BtnSelect.IsEnabled = False
        BtnImportNew.IsEnabled = False
        BtnPreview.IsEnabled = False
        LabResult.Text = "正在扫描本地文件…"
        ClearPreview()
        Try
            Dim result = Await Task.Run(Function() PackSyncPreview.Create(archivePath, instance.PathVersion, instance.PathIndie, environment, selected))
            If instance IsNot PageInstanceLeft.Instance Then ResetPage() : Return
            Dim labels As New Dictionary(Of PackSyncDifferenceKind, String) From {
                {PackSyncDifferenceKind.MissingTeamFile, "新增"}, {PackSyncDifferenceKind.TeamUpdate, "替换"},
                {PackSyncDifferenceKind.TeamRemoval, "移除"}, {PackSyncDifferenceKind.Conflict, "冲突"},
                {PackSyncDifferenceKind.LocalExtra, "本地附加"}}
            Dim previous = If(result.State Is Nothing, "尚未绑定", "上次版本：" & result.State.ReleaseId)
            LabResult.Text = $"{previous}；本次版本：{result.Package.ReleaseId}；发现 {result.Differences.Count} 项差异。" & If(result.StateWarning Is Nothing, "", vbCrLf & result.StateWarning)
            CurrentPreview = result
            Dim modsFolder = Path.Combine(instance.PathIndie, "mods")
            If Directory.Exists(modsFolder) Then
                Dim teamMods = New HashSet(Of String)(result.Package.Files.Where(Function(f) f.Path.StartsWith("mods/", StringComparison.OrdinalIgnoreCase) AndAlso
                    (Not f.Optional OrElse selected.Contains(f.Path, StringComparer.OrdinalIgnoreCase))).Select(Function(f) f.Path.Substring(5)), StringComparer.OrdinalIgnoreCase)
                For Each path In Directory.EnumerateFiles(modsFolder, "*", SearchOption.TopDirectoryOnly)
                    Dim resource As New LocalResourceFile(path)
                    If Not resource.IsEnabled AndAlso teamMods.Contains(resource.EnabledName) Then
                        PanDifferences.Children.Add(New TextBlock With {.Text = $"[已禁用] {resource.File.Name}：对应团队 Mod 当前有禁用副本，请检查。", .TextWrapping = TextWrapping.Wrap, .Margin = New Thickness(0, 4, 0, 4)})
                    End If
                Next
            End If
            For Each difference In result.Differences
                Dim panel As New StackPanel With {.Margin = New Thickness(0, 4, 0, 4)}
                panel.Children.Add(New TextBlock With {.Text = $"[{labels(difference.Kind)}] {difference.Path}：{DescribeDifference(difference)}", .TextWrapping = TextWrapping.Wrap})
                If difference.Kind = PackSyncDifferenceKind.Conflict OrElse
                   (difference.Kind = PackSyncDifferenceKind.LocalExtra AndAlso difference.Path.StartsWith("mods/", StringComparison.OrdinalIgnoreCase)) Then
                    Dim choice As New ComboBox With {.HorizontalAlignment = HorizontalAlignment.Left, .MinWidth = 180, .Margin = New Thickness(10, 3, 0, 0)}
                    choice.Items.Add(If(difference.LocalSha512 Is Nothing, "保留本地删除", "保留我的文件"))
                    choice.Items.Add(If(difference.TargetSha512 Is Nothing, "移出并备份", "使用团队文件"))
                    choice.SelectedIndex = 0
                    AddHandler choice.SelectionChanged, Sub()
                                                           CurrentPlan = Nothing
                                                           LabPlan.Text = ""
                                                           BtnApply.IsEnabled = False
                                                       End Sub
                    Choices.Add(difference.Path, choice)
                    panel.Children.Add(choice)
                End If
                PanDifferences.Children.Add(panel)
            Next
            BtnPlan.IsEnabled = True
        Catch ex As Exception
            LabResult.Text = ex.Message
        Finally
            BtnSelect.IsEnabled = True
            BtnImportNew.IsEnabled = SelectedPackagePath IsNot Nothing
            BtnPreview.IsEnabled = SelectedPackagePath IsNot Nothing
        End Try
    End Sub

    Private Shared Function DescribeDifference(difference As PackSyncDifference) As String
        Select Case difference.Kind
            Case PackSyncDifferenceKind.MissingTeamFile
                Return "团队需要此文件，本地没有。"
            Case PackSyncDifferenceKind.TeamUpdate
                Return "团队更新了文件，本地仍是上次团队版本。"
            Case PackSyncDifferenceKind.TeamRemoval
                Return "团队不再需要此文件，本地仍是上次团队版本。"
            Case PackSyncDifferenceKind.LocalExtra
                Return "此文件不属于团队清单。"
            Case PackSyncDifferenceKind.Conflict
                If difference.BaselineSha512 Is Nothing Then Return "团队需要此路径，但本地已有不同内容。"
                If difference.LocalSha512 Is Nothing Then Return "本地已删除此文件，团队仍需要它。"
                If difference.TargetSha512 Is Nothing Then Return "团队移除了此文件，但本地内容已修改。"
                Return "本地内容已修改，与当前团队要求不一致。"
            Case Else
                Throw New InvalidDataException("不支持的差异类型。")
        End Select
    End Function

    Private Sub ConfirmPlan() Handles BtnPlan.Click
        If CurrentPreview Is Nothing OrElse PageInstanceLeft.Instance IsNot SelectedInstance Then Return
        Try
            Dim decisions As New Dictionary(Of String, PackSyncChoice)(StringComparer.OrdinalIgnoreCase)
            For Each entry In Choices
                decisions.Add(entry.Key, If(entry.Value.SelectedIndex = 0, PackSyncChoice.KeepLocal, PackSyncChoice.UseTeam))
            Next
            Dim selected = PanOptional.Children.OfType(Of CheckBox)().Where(Function(c) c.IsChecked = True).Select(Function(c) CStr(c.Tag)).ToArray()
            Dim manifest = Configs.McInstallManifest.Get(SelectedInstance.Config)
            Dim components As IEnumerable(Of String) = If(manifest Is Nothing, Enumerable.Empty(Of String)(),
                manifest.ManagedFiles.Where(Function(f) Not String.IsNullOrWhiteSpace(f.Component)).Select(Function(f) f.RelativePath))
            CurrentPlan = PackSyncOperationPlan.Create(CurrentPreview, selected, decisions, components)
            Dim downloads = CurrentPreview.Package.Files.Where(Function(f) f.ArchiveEntry Is Nothing AndAlso CurrentPlan.Changes.Any(Function(c) String.Equals(c.Path, f.Path, StringComparison.OrdinalIgnoreCase) AndAlso c.AfterSha512 IsNot Nothing)).Sum(Function(f) f.Size)
            LabPlan.Text = $"已确认 {CurrentPlan.Changes.Count} 项文件变更，保留 {CurrentPlan.RetainedDifferences.Count} 项差异；待下载 {downloads} 字节。"
            BtnApply.IsEnabled = True
        Catch ex As Exception
            CurrentPlan = Nothing
            BtnApply.IsEnabled = False
            LabPlan.Text = ex.Message
        End Try
    End Sub

    Private Sub ApplyPlan() Handles BtnApply.Click
        If CurrentPlan Is Nothing OrElse CurrentPreview Is Nothing OrElse SelectedInstance IsNot PageInstanceLeft.Instance Then Return
        Dim stateWarning = If(CurrentPreview.StateWarning Is Nothing, "", vbCrLf & vbCrLf & "原小队状态损坏，无法核对原小队身份；将按首次绑定处理，并备份损坏状态。")
        If MyMsgBox("请先关闭此实例的游戏，包括通过其他启动器启动的游戏。确认应用所选文件变更？" & stateWarning, "确认小队同步", "应用", "取消") <> 1 Then Return
        Dim instance = SelectedInstance
        Dim plan = CurrentPlan
        Dim preview = CurrentPreview
        Dim archive = SelectedPackagePath
        Dim environment As New Dictionary(Of String, String) From {{"minecraft", instance.Version.VanillaName}}
        For Each pair In { (ModLoaders.Forge, "forge"), (ModLoaders.Fabric, "fabric-loader"), (ModLoaders.NeoForge, "neoforge") }
            If instance.Version.ModLoaders.HasFlagF(pair.Item1) Then environment.Add(pair.Item2, instance.Version.ModLoaderVersions(pair.Item1))
        Next
        BtnSelect.IsEnabled = False
        BtnImportNew.IsEnabled = False
        BtnPreview.IsEnabled = False
        BtnPlan.IsEnabled = False
        BtnApply.IsEnabled = False
        BtnUndo.IsEnabled = False
        BtnRecover.IsEnabled = False
        ComboRecovery.IsEnabled = False
        PanOptional.IsEnabled = False
        PanDifferences.IsEnabled = False
        LabPlan.Text = "正在准备同步…"
        PreparationControl = New PackSyncPreparationControl()
        BtnCancelPreparation.IsEnabled = True
        Try
            Dim started = PackSyncRunner.Start(instance, archive, preview.Package, plan, environment, PreparationControl,
                Sub(message) RunInUi(Sub()
                                         LabPlan.Text = message & "…"
                                         If message = "恢复原文件" Then BtnCancelPreparation.IsEnabled = False
                                     End Sub),
                Sub(errorResult) RunInUi(
                    Sub()
                        BtnCancelPreparation.IsEnabled = False
                        PreparationControl = Nothing
                        BtnSelect.IsEnabled = True
                        BtnImportNew.IsEnabled = SelectedPackagePath IsNot Nothing
                        ComboRecovery.IsEnabled = True
                        RefreshRecovery()
                        BtnPreview.IsEnabled = SelectedPackagePath IsNot Nothing
                        PanOptional.IsEnabled = True
                        PanDifferences.IsEnabled = True
                        If errorResult IsNot Nothing Then
                            LabPlan.Text = If(TypeOf errorResult Is OperationCanceledException, "同步已取消；已完成的文件变更已恢复。", "同步未完成：" & errorResult.GetDisplay(True))
                        Else
                            Dim retained = New HashSet(Of String)(plan.RetainedDifferences, StringComparer.OrdinalIgnoreCase)
                            Dim conflicts = preview.Differences.Where(Function(d) d.Kind = PackSyncDifferenceKind.Conflict AndAlso retained.Contains(d.Path)).Count()
                            Dim extras = preview.Differences.Where(Function(d) d.Kind = PackSyncDifferenceKind.LocalExtra AndAlso retained.Contains(d.Path)).Count()
                            If conflicts > 0 Then
                                LabPlan.Text = $"存在 {conflicts} 项本地差异。" & If(extras > 0, $"另保留 {extras} 个本地附加项。", "")
                            ElseIf extras > 0 Then
                                LabPlan.Text = $"团队文件已对齐，保留 {extras} 个本地附加项。"
                            Else
                                LabPlan.Text = "团队文件已对齐。"
                            End If
                            LabResult.Text = $"已处理版本：{preview.Package.ReleaseId}；请重新检查差异以查看当前状态。"
                            FrmInstanceMod?.ReloadModList(True)
                        End If
                        CurrentPlan = Nothing
                        BtnPlan.IsEnabled = False
                    End Sub))
            If Not started Then Throw New IOException("该实例正在被修改，请稍后再试。")
        Catch ex As Exception
            BtnCancelPreparation.IsEnabled = False
            PreparationControl = Nothing
            BtnSelect.IsEnabled = True
            BtnImportNew.IsEnabled = SelectedPackagePath IsNot Nothing
            BtnPreview.IsEnabled = True
            BtnPlan.IsEnabled = True
            ComboRecovery.IsEnabled = True
            RefreshRecovery()
            PanOptional.IsEnabled = True
            PanDifferences.IsEnabled = True
            LabPlan.Text = "同步未完成：" & ex.Message
        End Try
    End Sub

    Private Sub CancelPreparation() Handles BtnCancelPreparation.Click
        If PreparationControl Is Nothing Then Return
        If PreparationControl.RequestCancellation() Then
            LabPlan.Text = "正在取消；如已开始修改，将恢复原文件…"
        Else
            LabPlan.Text = "正在提交状态，请等待完成。"
        End If
        BtnCancelPreparation.IsEnabled = False
    End Sub
End Class
