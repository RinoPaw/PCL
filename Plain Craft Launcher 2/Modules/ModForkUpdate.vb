Imports System.Diagnostics
Imports System.IO
Imports System.Net.Http
Imports System.Security.Cryptography
Imports System.Text.RegularExpressions
Imports System.Threading
Imports Newtonsoft.Json.Linq

''' <summary>
''' Self-update support for the RinoPaw fork. Downloads are exclusively sourced from
''' https://github.com/RinoPaw/PCL/releases and are verified with SHA256SUMS.txt.
''' </summary>
Friend Module ModForkUpdate

    Private Const ReleaseApi As String = "https://api.github.com/repos/RinoPaw/PCL/releases/latest"
    Private Const ReleaseAssetPrefix As String = "https://github.com/RinoPaw/PCL/releases/download/"
    Private UpdateBusy As Integer = 0
    Friend LatestBuildIsCurrent As Boolean? = Nothing

    ''' <summary>
    ''' Check the latest fork Release. The normal startup loader calls this in the background.
    ''' A manual check bypasses the user's automatic-update preference.
    ''' </summary>
    Friend Sub CheckForUpdates(IsManual As Boolean)
        If Interlocked.CompareExchange(UpdateBusy, 1, 0) <> 0 Then
            If IsManual Then Hint("正在检查启动器更新……")
            Return
        End If

        Dim StagingDirectory As String = Nothing
        Dim HandedOff As Boolean = False
        Try
            If Not IsManual AndAlso (BuildType <> BuildTypes.Release OrElse Settings.Get(Of Integer)("SystemSystemUpdate") >= 2) Then Return

            ' The CI workflow embeds the exact fork source SHA. Local builds cannot
            ' safely compare themselves with Releases using the upstream version number.
            If Not Regex.IsMatch(CommitHash, "\A[0-9a-fA-F]{40}\z") Then
                If IsManual Then Hint("当前构建没有 Fork 提交标识，请从 GitHub Releases 获取正式构建。")
                Return
            End If

            Using Client As New HttpClient()
                Client.Timeout = TimeSpan.FromMinutes(5)
                Client.DefaultRequestHeaders.UserAgent.ParseAdd("PCL-RinoPaw-ForkUpdater/1.0")
                Client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json")

                Dim Release = JObject.Parse(Client.GetStringAsync(ReleaseApi).GetAwaiter().GetResult())
                Dim Tag = CStr(Release("tag_name"))
                If Not Regex.IsMatch(Tag, "\Apcl-rinopaw-[0-9a-f]{12}\z") Then
                    Throw New InvalidDataException("GitHub Release 的版本标识不符合预期。")
                End If
                Dim LatestShortSha = Tag.Substring("pcl-rinopaw-".Length)
                LatestBuildIsCurrent = String.Equals(CommitHash.Substring(0, 12), LatestShortSha, StringComparison.OrdinalIgnoreCase)
                If LatestBuildIsCurrent.Value Then
                    If IsManual Then Hint("当前已经是 RinoPaw Fork 的最新发布版。", HintType.Green)
                    Return
                End If

                Dim Assets = TryCast(Release("assets"), JArray)
                If Assets Is Nothing Then Throw New InvalidDataException("GitHub Release 没有文件列表。")
                Dim ExpectedExeName = "PCL-RinoPaw-" & Tag & ".exe"
                Dim ExeUrl As String = Nothing
                Dim SumUrl As String = Nothing
                For Each Entry As JToken In Assets
                    Dim Name = CStr(Entry("name"))
                    Dim Url = CStr(Entry("browser_download_url"))
                    If Name = ExpectedExeName Then ExeUrl = Url
                    If Name = "SHA256SUMS.txt" Then SumUrl = Url
                Next
                If String.IsNullOrWhiteSpace(ExeUrl) OrElse String.IsNullOrWhiteSpace(SumUrl) Then
                    Throw New InvalidDataException("GitHub Release 缺少 EXE 或 SHA-256 校验文件。")
                End If
                Dim ExpectedBase = ReleaseAssetPrefix & Tag & "/"
                If Not String.Equals(ExeUrl, ExpectedBase & ExpectedExeName, StringComparison.Ordinal) OrElse
                   Not String.Equals(SumUrl, ExpectedBase & "SHA256SUMS.txt", StringComparison.Ordinal) Then
                    Throw New InvalidDataException("Release 文件来源地址不符合预期。")
                End If

                Dim Preference = Settings.Get(Of Integer)("SystemSystemUpdate")
                If IsManual OrElse Preference = 1 Then
                    Dim Agreed As Boolean = RunInUiWait(Function() MyMsgBox(
                        "发现 RinoPaw Fork 的新版本：" & Tag & vbCrLf & "是否下载并更新启动器？",
                        "发现启动器更新", "下载并更新", "暂不更新") = 1)
                    If Not Agreed Then Return
                End If

                Hint("正在下载并验证 RinoPaw Fork 更新……")
                StagingDirectory = Path.Combine(Path.GetTempPath(), "PCL-RinoPaw-Update", Guid.NewGuid().ToString("N"))
                Directory.CreateDirectory(StagingDirectory)
                Dim NewExecutable = Path.Combine(StagingDirectory, "PCL-RinoPaw-Update.exe")
                Dim Checksums As String = Client.GetStringAsync(SumUrl).GetAwaiter().GetResult()
                Dim ExpectedSha As String = Nothing
                For Each Line In Checksums.TrimStart(ChrW(&HFEFF)).Split({vbCr, vbLf}, StringSplitOptions.RemoveEmptyEntries)
                    Dim Match = Regex.Match(Line.Trim(), "\A([0-9a-fA-F]{64})\s+(.+?)\z")
                    If Match.Success AndAlso String.Equals(Match.Groups(2).Value, ExpectedExeName, StringComparison.Ordinal) Then
                        ExpectedSha = Match.Groups(1).Value
                        Exit For
                    End If
                Next
                If ExpectedSha Is Nothing Then Throw New InvalidDataException("校验清单中没有对应 EXE 的 SHA-256。")

                Using Response = Client.GetAsync(ExeUrl, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult()
                    Response.EnsureSuccessStatusCode()
                    Dim TotalSize = Response.Content.Headers.ContentLength
                    If TotalSize.HasValue AndAlso (TotalSize.Value <= 0 OrElse TotalSize.Value > 100L * 1024 * 1024) Then
                        Throw New InvalidDataException("下载文件大小异常。")
                    End If
                    Using Source = Response.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
                        Using Destination As New FileStream(NewExecutable, FileMode.CreateNew, FileAccess.Write, FileShare.None)
                            Dim Buffer(65535) As Byte
                            Dim Size As Integer
                            Dim ReadBytes As Long = 0
                            Do
                                Size = Source.Read(Buffer, 0, Buffer.Length)
                                If Size = 0 Then Exit Do
                                ReadBytes += Size
                                If ReadBytes > 100L * 1024 * 1024 Then Throw New InvalidDataException("下载文件超过大小限制。")
                                Destination.Write(Buffer, 0, Size)
                            Loop
                            If ReadBytes = 0 Then Throw New InvalidDataException("下载文件为空。")
                            If TotalSize.HasValue AndAlso ReadBytes <> TotalSize.Value Then Throw New InvalidDataException("文件下载不完整。")
                        End Using
                    End Using
                End Using

                Dim ActualSha As String
                Using Sha = SHA256.Create()
                    Using Input As New FileStream(NewExecutable, FileMode.Open, FileAccess.Read, FileShare.Read)
                        ActualSha = BitConverter.ToString(Sha.ComputeHash(Input)).Replace("-", "")
                    End Using
                End Using
                If Not String.Equals(ExpectedSha, ActualSha, StringComparison.OrdinalIgnoreCase) Then
                    Throw New InvalidDataException("更新文件 SHA-256 校验失败，已拒绝安装。")
                End If

                Dim RestartNow As Boolean = RunInUiWait(Function() MyMsgBox(
                    "更新已下载并通过 SHA-256 校验。" & vbCrLf & "现在关闭启动器并安装更新？",
                    "更新已就绪", "立即重启更新", "取消") = 1)
                If Not RestartNow Then Return

                Dim TargetExecutable = Path.GetFullPath(PathExe)
                If Not File.Exists(TargetExecutable) Then Throw New FileNotFoundException("找不到当前启动器本体。", TargetExecutable)
                Dim CmdLine = "--update " & Process.GetCurrentProcess().Id.ToString() & " """ & TargetExecutable & """ """ & NewExecutable & """ true"
                Dim Info As New ProcessStartInfo(NewExecutable, CmdLine) With {
                    .WorkingDirectory = Path.GetDirectoryName(TargetExecutable),
                    .UseShellExecute = True
                }
                Process.Start(Info)
                HandedOff = True
                RunInUi(Sub() FrmMain.EndProgram(True))
            End Using
        Catch Ex As Exception
            Logger.Error(Ex, "检查或安装 RinoPaw Fork 更新失败")
            If IsManual Then
                RunInUi(Sub() MyMsgBox("更新失败：" & Ex.Message & vbCrLf &
                    "可以从 https://github.com/RinoPaw/PCL/releases 手动下载。", "检查更新失败"))
            End If
        Finally
            If Not HandedOff AndAlso StagingDirectory IsNot Nothing Then
                Try
                    Directory.Delete(StagingDirectory, True)
                Catch
                End Try
            End If
            Interlocked.Exchange(UpdateBusy, 0)
        End Try
    End Sub

    ''' <summary>
    ''' Runs in the verified downloaded EXE (--update), after the old instance exits.
    ''' Replaces the executable atomically on the same drive, keeping a backup.
    ''' </summary>
    Friend Sub ApplyDownloadedUpdate(OldProcessId As Integer, OldFileName As String, NewFileName As String, Restart As Boolean)
        Try
            Dim Target = Path.GetFullPath(OldFileName)
            Dim Replacement = Path.GetFullPath(NewFileName)
            If Not Target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) OrElse
               Not String.Equals(Replacement, Path.GetFullPath(Process.GetCurrentProcess().MainModule.FileName), StringComparison.OrdinalIgnoreCase) Then
                Throw New InvalidOperationException("更新器参数不合法。")
            End If
            Try
                Using OldProcess = Process.GetProcessById(OldProcessId)
                    If Not OldProcess.WaitForExit(30000) Then Throw New TimeoutException("原启动器未能在 30 秒内退出。")
                End Using
            Catch Ex As ArgumentException
                'The old launcher has already exited.
            End Try

            Dim Staged = Target & ".rinopaw-new"
            Dim Backup = Target & ".rinopaw-backup"
            Dim Replaced As Boolean = False
            Try
                File.Copy(Replacement, Staged, True)
                For Attempt = 1 To 20
                    Try
                        If File.Exists(Backup) Then File.Delete(Backup)
                        File.Replace(Staged, Target, Backup, True)
                        Replaced = True
                        Exit For
                    Catch Ex As IOException
                        If Attempt = 20 Then Throw
                        Thread.Sleep(250)
                    End Try
                Next
                If Restart Then
                    Try
                        Process.Start(New ProcessStartInfo(Target) With {
                            .WorkingDirectory = Path.GetDirectoryName(Target),
                            .UseShellExecute = True
                        })
                    Catch
                        'Restore the original binary if the new one cannot be launched.
                        If Replaced AndAlso File.Exists(Backup) Then File.Replace(Backup, Target, Nothing, True)
                        Throw
                    End Try
                End If
            Finally
                If File.Exists(Staged) Then File.Delete(Staged)
            End Try
        Catch Ex As Exception
            MsgBox("无法安装更新：" & Ex.Message & vbCrLf &
                   "请从 https://github.com/RinoPaw/PCL/releases 手动下载。",
                   MsgBoxStyle.Critical, "PCL 更新失败")
            Environment.Exit(ProcessReturnValues.Fail)
        End Try
    End Sub
End Module
