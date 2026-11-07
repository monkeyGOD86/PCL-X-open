Imports PCLCS.PackSync

''' <summary>Non-blocking exclusion for writers to a specific mods folder.</summary>
Public NotInheritable Class PackSyncModFolderLock
    Private Shared ReadOnly Active As New System.Collections.Concurrent.ConcurrentDictionary(Of String, Byte)(StringComparer.OrdinalIgnoreCase)
    Private Sub New()
    End Sub

    Public Shared Function TryAcquire(Folder As String) As IDisposable
        Dim key = PathUtils.ForCompare(Folder)
        If Not Active.TryAdd(key, 0) Then Return Nothing
        Return New Lease(key)
    End Function

    Private NotInheritable Class Lease
        Implements IDisposable
        Private ReadOnly Key As String
        Private Disposed As Integer
        Public Sub New(Key As String)
            Me.Key = Key
        End Sub
        Public Sub Dispose() Implements IDisposable.Dispose
            If Threading.Interlocked.Exchange(Disposed, 1) = 0 Then
                Dim ignored As Byte
                Active.TryRemove(Key, ignored)
            End If
        End Sub
    End Class
End Class

''' <summary>Allows cancellation only until the transaction enters the file modification phase.</summary>
Public NotInheritable Class PackSyncPreparationControl
    Private ReadOnly Gate As New Object
    Private ReadOnly Source As New Threading.CancellationTokenSource
    Private Applying As Boolean
    Private Finalizing As Boolean
    Public ReadOnly Property Token As Threading.CancellationToken
        Get
            Return Source.Token
        End Get
    End Property
    Public Function CancelPreparation() As Boolean
        SyncLock Gate
            If Applying Then Return False
            Source.Cancel()
            Return True
        End SyncLock
    End Function
    Public Sub BeginApply()
        SyncLock Gate
            Source.Token.ThrowIfCancellationRequested()
            Applying = True
        End SyncLock
    End Sub
    Public Function RequestCancellation() As Boolean
        SyncLock Gate
            If Finalizing Then Return False
            Source.Cancel()
            Return True
        End SyncLock
    End Function
    Public Sub BeginCommit()
        SyncLock Gate
            Source.Token.ThrowIfCancellationRequested()
            Finalizing = True
        End SyncLock
    End Sub
End Class

''' <summary>Task-list view of a sync worker; cancellation never ends the task before its worker exits.</summary>
Public NotInheritable Class PackSyncTask
    Inherits LoaderBase
    Private ReadOnly Control As PackSyncPreparationControl
    Public Sub New(TaskName As String, Control As PackSyncPreparationControl)
        Me.Name = TaskName
        Me.Control = Control
    End Sub
    Public Overrides Sub Start(Optional Input As Object = Nothing, Optional IsForceRestart As Boolean = False)
        State = LoadState.Loading
    End Sub
    Public Overrides Sub Cancel()
        Control.RequestCancellation()
    End Sub
    Public Function TryCancelSync() As Boolean
        Return Control.RequestCancellation()
    End Function
    Public Overrides Sub Failed(Ex As Exception)
        [Error] = Ex
        State = LoadState.Failed
    End Sub
    Public Sub Complete(Ex As Exception)
        If TypeOf Ex Is OperationCanceledException Then
            State = LoadState.Canceled
        ElseIf Ex IsNot Nothing Then
            Failed(Ex)
        Else
            State = LoadState.Finished
        End If
    End Sub
    Public Function GetLoaderList() As List(Of LoaderBase)
        Return New List(Of LoaderBase) From {Me}
    End Function
End Class

''' <summary>Runs the confirmed plan while holding the instance and mods-folder locks.</summary>
Public NotInheritable Class PackSyncRunner
    Private Sub New()
    End Sub

    Public Shared Function Start(Instance As McInstance, ArchivePath As String, Package As PackSyncPackage,
                                 Plan As PackSyncOperationPlan, Environment As IDictionary(Of String, String),
                                 Control As PackSyncPreparationControl, Report As Action(Of String), Finished As Action(Of Exception)) As Boolean
        If Instance Is Nothing OrElse Package Is Nothing OrElse Plan Is Nothing Then Throw New ArgumentNullException("同步参数")
        If Control Is Nothing Then Throw New ArgumentNullException(NameOf(Control))
        Dim operationId = Guid.NewGuid().ToString("N")
        Dim failure As Exception = Nothing
        Dim task As New PackSyncTask("小队同步：" & Instance.Name & " #" & operationId.Substring(0, 8), Control)
        Dim Worker As New RedoableWorker(
        Sub()
            Try
                Using lease = PackSyncModFolderLock.TryAcquire(Instance.PathIndie & "mods\")
                    If lease Is Nothing Then Throw New IOException("Mod 文件夹正在被修改，请稍后重试。")
                    EnsureGameStopped(Instance)
                    Dim pending = PackSyncTransaction.FindPending(Instance.PathVersion)
                    If pending.Count > 0 Then Throw New IOException("存在未完成或损坏的同步事务，请先处理：" & pending(0).TransactionId)
                    Report?.Invoke("准备文件")
                    task.Progress = 0.05
                    Dim downloadFolder = Path.Combine(RequestTaskTempFolder(), "packsync-download")
                    Dim batch As New PackSyncDownloadBatch(Package, Plan, downloadFolder)
                    If batch.Files.Count > 0 Then
                        Dim download As New LoaderDownload("下载小队文件", batch.Files)
                        Control.Token.ThrowIfCancellationRequested()
                        Using registration = Control.Token.Register(Sub() download.Cancel())
                            Try
                                download.WaitForExit(c:=Control.Token, p:=task.CreateSyncProgressProvider(0.05, 0.45))
                            Finally
                                If Control.Token.IsCancellationRequested Then download.Cancel()
                            End Try
                        End Using
                    End If
                    task.Progress = 0.45
                    Dim stage = Path.Combine(Instance.PathVersion, "PCL", "PackSync", "staging", operationId)
                    Dim needed = Plan.Changes.Where(Function(c) c.AfterSha512 IsNot Nothing).Select(Function(c) c.Path).ToArray()
                    Dim prepared = PackSyncPreparation.Prepare(ArchivePath, Package, needed, stage, AddressOf batch.OpenDownloaded, Control.Token)
                    task.Progress = 0.55
                    'The installer manifest may have changed since the user confirmed the preview.
                    'Read it again under the instance lock before changing any files.
                    Dim manifest = Configs.McInstallManifest.Get(Instance.Config)
                    Dim components As IEnumerable(Of String) = If(manifest Is Nothing, Enumerable.Empty(Of String)(),
                        manifest.ManagedFiles.Where(Function(f) Not String.IsNullOrWhiteSpace(f.Component)).Select(Function(f) f.RelativePath))
                    Plan.ValidateComponents(components)
                    Control.BeginApply()
                    task.Progress = 0.65
                    Report?.Invoke("应用更新")
                    PackSyncTransaction.Backup(ArchivePath, Instance.PathVersion, Instance.PathIndie, Environment, Plan, operationId)
                    PackSyncTransaction.ApplyFiles(ArchivePath, Instance.PathVersion, Instance.PathIndie, Environment, Plan, operationId, prepared,
                        Control.Token, Sub() Report?.Invoke("恢复原文件"))
                    Try
                        Plan.ValidateApplied(ArchivePath, Instance.PathVersion, Instance.PathIndie, Environment)
                        Control.BeginCommit()
                        task.Progress = 0.9
                        PackSyncTransaction.CommitState(Instance.PathVersion, operationId, Plan, Package)
                    Catch commitError As Exception
                        Report?.Invoke("恢复原文件")
                        Try
                            PackSyncTransaction.RestoreInterrupted(Instance.PathVersion, operationId)
                        Catch restoreError As Exception
                            Throw New AggregateException("状态提交失败且恢复未完成，事务材料已保留。", commitError, restoreError)
                        End Try
                        Throw
                    End Try
                    Try
                        Dim skipped = PackSyncTransaction.PruneCompleted(Instance.PathVersion, operationId)
                        If skipped.Count > 0 Then Logger.Warn("未能清理部分旧同步备份：" & String.Join(", ", skipped))
                    Catch ex As Exception
                        Logger.Warn(ex, "清理旧同步备份失败，当前同步仍已完成")
                    End Try
                End Using
            Catch ex As Exception
                failure = ex
            End Try
        End Sub)
        If Not McInstanceLock.TryAcquire(Worker, Instance.PathVersion) Then Return False
        Try
            task.Start()
            LoaderTaskbar.Add(task)
            FrmMain.BtnExtraDownload.ShowRefresh()
            AddHandler Worker.Stopped, Sub()
                                           task.Complete(failure)
                                           Finished?.Invoke(failure)
                                       End Sub
            Worker.Start()
        Catch
            McInstanceLock.Release(Worker, Instance.PathVersion)
            LoaderTaskbar.Remove(task)
            Throw
        End Try
        Return True
    End Function

    Public Shared Function StartRecovery(Instance As McInstance, TransactionId As String, IsUndo As Boolean, Finished As Action(Of Exception)) As Boolean
        Dim failure As Exception = Nothing
        Dim worker As New RedoableWorker(
        Sub()
            Try
                Using lease = PackSyncModFolderLock.TryAcquire(Instance.PathIndie & "mods\")
                    If lease Is Nothing Then Throw New IOException("Mod 文件夹正在被修改，请稍后重试。")
                    If Not PathUtils.IsSame(Instance.PathVersion, Instance.PathIndie) Then Throw New IOException("恢复需要开启版本隔离。")
                    EnsureGameStopped(Instance)
                    If IsUndo Then
                        If PackSyncTransaction.FindPending(Instance.PathVersion).Count > 0 Then Throw New IOException("存在未完成事务，请先恢复。")
                        Dim state = PackSyncPreview.ReadState(Instance.PathVersion)
                        If state Is Nothing OrElse state.LastTransactionId <> TransactionId Then Throw New IOException("该事务不再是最近一次同步。")
                        PackSyncTransaction.Undo(Instance.PathVersion, TransactionId)
                    Else
                        Dim journal = PackSyncTransaction.Inspect(Instance.PathVersion, TransactionId)
                        If journal.Status = "preparing" Then
                            PackSyncTransaction.AbandonPreparation(Instance.PathVersion, TransactionId)
                        Else
                            PackSyncTransaction.RestoreInterrupted(Instance.PathVersion, TransactionId)
                        End If
                    End If
                End Using
            Catch ex As Exception
                failure = ex
            End Try
        End Sub)
        If Not McInstanceLock.TryAcquire(worker, Instance.PathVersion) Then Return False
        Try
            AddHandler worker.Stopped, Sub() Finished?.Invoke(failure)
            worker.Start()
        Catch
            McInstanceLock.Release(worker, Instance.PathVersion)
            Throw
        End Try
        Return True
    End Function

    Private Shared Sub EnsureGameStopped(Instance As McInstance)
        For Each watcher In McWatcherList.ToArray()
            If watcher.Instance IsNot Nothing AndAlso PathUtils.IsSame(watcher.Instance.PathVersion, Instance.PathVersion) Then
                If watcher.GameProcess Is Nothing OrElse Not watcher.GameProcess.HasExited Then Throw New IOException("该实例的游戏仍在运行，请先关闭游戏。")
            End If
        Next
    End Sub
End Class

''' <summary>Connects team pack file preparation to the launcher's download task system.</summary>
Public NotInheritable Class PackSyncDownloadBatch
    Public ReadOnly Property Files As IReadOnlyList(Of NetFile)
    Private ReadOnly DownloadPaths As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)

    Public Sub New(Package As PackSyncPackage, Plan As PackSyncOperationPlan, DownloadFolder As String)
        If Package Is Nothing Then Throw New ArgumentNullException(NameOf(Package))
        If Plan Is Nothing Then Throw New ArgumentNullException(NameOf(Plan))
        If Package.PackId <> Plan.PackId OrElse Not String.Equals(Package.ArchiveSha512, Plan.ArchiveSha512, StringComparison.OrdinalIgnoreCase) Then
            Throw New InvalidDataException("整合包与已确认的计划不一致。")
        End If
        Dim folder = Path.GetFullPath(DownloadFolder)
        Dim files As New List(Of NetFile)
        Dim required = New HashSet(Of String)(Plan.Changes.Where(Function(c) c.AfterSha512 IsNot Nothing).Select(Function(c) c.Path), StringComparer.OrdinalIgnoreCase)
        Dim number As Integer = 0
        For Each entry In Package.Files.Where(Function(f) f.ArchiveEntry Is Nothing AndAlso required.Contains(f.Path))
            number += 1
            Dim destination = Path.Combine(folder, number.ToString("D8") & ".download")
            Dim expected = Plan.Changes.Single(Function(c) String.Equals(c.Path, entry.Path, StringComparison.OrdinalIgnoreCase))
            If Not String.Equals(expected.AfterSha512, entry.Sha512, StringComparison.OrdinalIgnoreCase) Then Throw New InvalidDataException("下载文件与计划不一致：" & entry.Path)
            files.Add(New NetFile(entry.Downloads, destination, New FileChecker With {.Hash = entry.Sha512, .ActualSize = entry.Size}, True))
            DownloadPaths.Add(entry.Path, destination)
        Next
        Me.Files = files.AsReadOnly()
    End Sub

    ''' <summary>Supply this callback to PackSyncPreparation after LoaderDownload finishes.</summary>
    Public Function OpenDownloaded(Entry As PackSyncTargetFile, Cancellation As Threading.CancellationToken) As Stream
        Cancellation.ThrowIfCancellationRequested()
        Dim path As String = Nothing
        If Not DownloadPaths.TryGetValue(Entry.Path, path) Then Throw New InvalidDataException("没有找到已下载文件：" & Entry.Path)
        Return New FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)
    End Function
End Class
