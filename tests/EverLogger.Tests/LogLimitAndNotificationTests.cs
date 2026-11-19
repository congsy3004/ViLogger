using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EverLogger.Core.Data;
using EverLogger.Core.Logging;
using EverLogger.Core.Serial;
#if WINDOWS_WPF_TESTS
using EverLogger.App.Helpers;
using EverLogger.App.ViewModels;
#endif
using Xunit;

namespace EverLogger.Tests;

/// <summary>
/// Tests for <see cref="LogFileWriter"/> size limits (Rotate / Stop) and in-app event notification popups.
/// </summary>
public sealed class LogLimitAndNotificationTests : IDisposable
{
    private readonly string _tempDir;

    public LogLimitAndNotificationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ARVSerialTool_LogLimitTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }
        catch
        {
        }
    }

    private static void WaitUntil(Func<bool> condition, int timeoutMs = 5000)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs)
                throw new TimeoutException("Timed out waiting for expected condition.");
            Thread.Sleep(10);
        }
    }

    [Fact]
    public void LogFileWriter_RotateMode_Binary_SplitsLosslesslyAcrossMultipleParts()
    {
        string logDir = Path.Combine(_tempDir, "rotate_bin");
        byte[] payload = Enumerable.Range(0, 425).Select(i => (byte)(i & 0xFF)).ToArray();

        var writer = new LogFileWriter(
            logDir,
            LogFormat.Binary,
            new LogFileNameTemplate("test", ".bin"),
            "COM99",
            maxFileSizeBytes: 100,
            limitAction: LogLimitAction.Rotate);

        writer.Start();
        // Send packets larger and smaller than the 100-byte limit to test multi-part splitting.
        writer.Queue.TryWrite(new DataPacket(0, "COM99", payload[..260]));
        writer.Queue.TryWrite(new DataPacket(0, "COM99", payload[260..340]));
        writer.Queue.TryWrite(new DataPacket(0, "COM99", payload[340..]));
        writer.Stop();

        Assert.Null(writer.Fault);
        Assert.False(writer.StoppedByLimit);
        Assert.Equal(payload.Length, writer.BytesWritten);
        Assert.Equal(5, writer.CurrentPartNumber);

        string[] expectedFiles =
        {
            Path.Combine(logDir, "test.bin"),
            Path.Combine(logDir, "test_part2.bin"),
            Path.Combine(logDir, "test_part3.bin"),
            Path.Combine(logDir, "test_part4.bin"),
            Path.Combine(logDir, "test_part5.bin")
        };

        var reassembled = new MemoryStream();
        for (int i = 0; i < expectedFiles.Length; i++)
        {
            Assert.True(File.Exists(expectedFiles[i]), $"Expected part file missing: {expectedFiles[i]}");
            byte[] partBytes = File.ReadAllBytes(expectedFiles[i]);
            long expectedLength = i < 4 ? 100 : 25;
            Assert.Equal(expectedLength, partBytes.Length);
            reassembled.Write(partBytes, 0, partBytes.Length);
        }

        Assert.True(reassembled.ToArray().SequenceEqual(payload), "Concatenated rotated parts do not match source bytes.");

        var events = new List<LogWriterEvent>();
        while (writer.TryDequeueEvent(out var ev))
        {
            events.Add(ev);
        }

        Assert.Equal(4, events.Count);
        Assert.All(events, e => Assert.Equal(LogWriterEventKind.Rotated, e.Kind));
        Assert.Equal(expectedFiles[0], events[0].CompletedFilePath);
        Assert.Equal(expectedFiles[1], events[0].NewFilePath);
        Assert.Equal(1, events[0].CompletedPartNumber);
        Assert.Equal(2, events[0].NewPartNumber);
        Assert.Equal(expectedFiles[4], writer.CurrentFilePath);
    }

    [Theory]
    [InlineData(48)]
    [InlineData(50)]
    public async Task LogFileWriter_RotateMode_Hex_EnforcesFileSizeLimitAndRoundTrips(long limitBytes)
    {
        string logDir = Path.Combine(_tempDir, $"rotate_hex_{limitBytes}");
        var rng = new Random(123);
        byte[] payload = new byte[120];
        rng.NextBytes(payload);

        var writer = new LogFileWriter(
            logDir,
            LogFormat.Hex,
            new LogFileNameTemplate("hexcap", ".log"),
            "COM99",
            maxFileSizeBytes: limitBytes,
            limitAction: LogLimitAction.Rotate);

        writer.Start();
        writer.Queue.TryWrite(new DataPacket(0, "COM99", payload[..35]));
        writer.Queue.TryWrite(new DataPacket(0, "COM99", payload[35..80]));
        writer.Queue.TryWrite(new DataPacket(0, "COM99", payload[80..]));
        writer.Stop();

        Assert.Null(writer.Fault);
        Assert.Equal(payload.Length, writer.BytesWritten);

        string[] partFiles = Directory.GetFiles(logDir, "hexcap*.log")
            .OrderBy(p => p == Path.Combine(logDir, "hexcap.log") ? 1 : int.Parse(Path.GetFileNameWithoutExtension(p).Replace("hexcap_part", "")))
            .ToArray();

        Assert.True(partFiles.Length >= 7, $"Expected at least 7 part files for 120 bytes in hex at {limitBytes} B limit.");

        var decoded = new MemoryStream();
        foreach (string partFile in partFiles)
        {
            byte[] fileBytes = File.ReadAllBytes(partFile);
            Assert.InRange(fileBytes.Length, 1, limitBytes);
            Assert.Equal((byte)'\n', fileBytes[^1]);

            await FileSender.SendAsync(
                new FileSendOptions { FilePath = partFile, Mode = FileSendMode.Hex, ChunkSize = 64, BaudRate = 921600 },
                (buf, off, cnt) => decoded.Write(buf, off, cnt));
        }

        Assert.True(decoded.ToArray().SequenceEqual(payload), "Round-tripped hex parts do not match original bytes.");
    }

    [Fact]
    public void LogFileWriter_RotateMode_SkipsExistingPartFilesWithoutOverwriting()
    {
        string logDir = Path.Combine(_tempDir, "skip_existing");
        Directory.CreateDirectory(logDir);

        string existingPart2 = Path.Combine(logDir, "cap_part2.bin");
        byte[] sentinel = Encoding.ASCII.GetBytes("EXISTING_PART_2");
        File.WriteAllBytes(existingPart2, sentinel);

        byte[] payload = Enumerable.Repeat((byte)0x5A, 40).ToArray();
        var writer = new LogFileWriter(
            logDir,
            LogFormat.Binary,
            new LogFileNameTemplate("cap", ".bin"),
            "COM99",
            maxFileSizeBytes: 24,
            limitAction: LogLimitAction.Rotate);

        writer.Start();
        writer.Queue.TryWrite(new DataPacket(0, "COM99", payload));
        writer.Stop();

        Assert.Equal(sentinel, File.ReadAllBytes(existingPart2));
        Assert.True(File.Exists(Path.Combine(logDir, "cap_part3.bin")));
        Assert.Equal(3, writer.CurrentPartNumber);
        Assert.Equal(16, File.ReadAllBytes(Path.Combine(logDir, "cap_part3.bin")).Length);
    }

    [Fact]
    public void LogFileWriter_StopMode_BinaryAndHex_StopsAtLimitAndEmitsEvent()
    {
        // Binary stop mode
        string binDir = Path.Combine(_tempDir, "stop_bin");
        byte[] binPayload = Enumerable.Range(0, 200).Select(i => (byte)i).ToArray();

        var binWriter = new LogFileWriter(
            binDir,
            LogFormat.Binary,
            new LogFileNameTemplate("stopbin", ".bin"),
            "COM99",
            maxFileSizeBytes: 64,
            limitAction: LogLimitAction.Stop);

        binWriter.Start();
        binWriter.Queue.TryWrite(new DataPacket(0, "COM99", binPayload));
        WaitUntil(() => binWriter.StoppedByLimit && !binWriter.IsRunning);
        binWriter.Stop();

        Assert.Null(binWriter.Fault);
        Assert.True(binWriter.StoppedByLimit);
        Assert.Equal(64, binWriter.BytesWritten);
        Assert.Equal(64, binWriter.CurrentFileBytes);
        byte[] binActual = File.ReadAllBytes(binWriter.CurrentFilePath!);
        Assert.Equal(binPayload[..64], binActual);
        Assert.True(binWriter.TryDequeueEvent(out var binEvent));
        Assert.Equal(LogWriterEventKind.LimitReachedStopped, binEvent.Kind);
        Assert.Null(binEvent.NewFilePath);
        Assert.Equal(64, binEvent.CompletedFileBytes);
        Assert.False(binWriter.TryDequeueEvent(out _));

        // Hex stop mode (non-line-aligned limit 50 B -> fits 1 full 16-byte line = 48 B)
        string hexDir = Path.Combine(_tempDir, "stop_hex");
        byte[] hexPayload = Enumerable.Range(0, 40).Select(i => (byte)i).ToArray();

        var hexWriter = new LogFileWriter(
            hexDir,
            LogFormat.Hex,
            new LogFileNameTemplate("stophex", ".log"),
            "COM99",
            maxFileSizeBytes: 50,
            limitAction: LogLimitAction.Stop);

        hexWriter.Start();
        hexWriter.Queue.TryWrite(new DataPacket(0, "COM99", hexPayload));
        WaitUntil(() => hexWriter.StoppedByLimit && !hexWriter.IsRunning);
        hexWriter.Stop();

        Assert.Null(hexWriter.Fault);
        Assert.True(hexWriter.StoppedByLimit);
        Assert.Equal(16, hexWriter.BytesWritten);
        byte[] hexActual = File.ReadAllBytes(hexWriter.CurrentFilePath!);
        Assert.InRange(hexActual.Length, 1, 50);
        Assert.Equal((byte)'\n', hexActual[^1]);
        Assert.True(hexWriter.TryDequeueEvent(out var hexEvent));
        Assert.Equal(LogWriterEventKind.LimitReachedStopped, hexEvent.Kind);
        Assert.Equal(hexActual.Length, hexEvent.CompletedFileBytes);
    }

    [Fact]
    public void LogFileWriter_StopMode_PreExistingFileAlreadyAtLimit_StopsImmediately()
    {
        string logDir = Path.Combine(_tempDir, "pre_full");
        Directory.CreateDirectory(logDir);
        string existingPath = Path.Combine(logDir, "full.bin");
        byte[] initial = Enumerable.Repeat((byte)0xAB, 32).ToArray();
        File.WriteAllBytes(existingPath, initial);

        var writer = new LogFileWriter(
            logDir,
            LogFormat.Binary,
            new LogFileNameTemplate("full", ".bin"),
            "COM99",
            maxFileSizeBytes: 32,
            limitAction: LogLimitAction.Stop);

        writer.Start();
        WaitUntil(() => writer.StoppedByLimit && !writer.IsRunning);
        writer.Queue.TryWrite(new DataPacket(0, "COM99", new byte[] { 1, 2, 3 }));
        writer.Stop();

        Assert.Null(writer.Fault);
        Assert.True(writer.StoppedByLimit);
        Assert.Equal(0, writer.BytesWritten);
        Assert.Equal(initial, File.ReadAllBytes(existingPath));
        Assert.True(writer.TryDequeueEvent(out var ev));
        Assert.Equal(LogWriterEventKind.LimitReachedStopped, ev.Kind);
        Assert.Equal(32, ev.CompletedFileBytes);
    }

    [Fact]
    public void LogFileWriter_InvalidMaxFileSize_ThrowsArgumentOutOfRange()
    {
        var template = new LogFileNameTemplate("t", ".bin");

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new LogFileWriter(_tempDir, LogFormat.Binary, template, "COM99", maxFileSizeBytes: -1));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new LogFileWriter(_tempDir, LogFormat.Binary, template, "COM99", maxFileSizeBytes: 8));

        // 0 means unlimited and starts/stops cleanly.
        var unlimited = new LogFileWriter(_tempDir, LogFormat.Binary, template, "COM99", maxFileSizeBytes: 0);
        unlimited.Start();
        unlimited.Queue.TryWrite(new DataPacket(0, "COM99", new byte[] { 0x11, 0x22 }));
        unlimited.Stop();
        Assert.Equal(2, unlimited.BytesWritten);
    }

#if WINDOWS_WPF_TESTS
    private sealed record RecordedNotification(
        NotificationSeverity Severity,
        string Title,
        string Message,
        string? SourcePort,
        string? CoalesceKey);

    private PortTabViewModel CreateMockTab(
        string logDir,
        string template,
        List<RecordedNotification> notifications,
        Func<long>? getMaxBytes = null,
        Func<LogLimitAction>? getAction = null,
        Func<bool>? isMaxSizeValid = null,
        MemoryStream? txSink = null,
        Action<int>? onTxSlice = null)
    {
        var ports = new ObservableCollection<PortInfo> { new PortInfo("COM99", "Mock Serial") };
        var bauds = new ObservableCollection<int> { 9600, 115200 };
        var sink = txSink ?? new MemoryStream();

        var vm = new PortTabViewModel(
            ports,
            bauds,
            connectAction: _ => { },
            disconnectAction: _ => { },
            sendAction: (_, data) => sink.Write(data, 0, data.Length),
            removeAction: _ => { },
            canRemove: () => false,
            getLogDirectory: () => logDir,
            getLogFileTemplate: () => template,
            sendSliceAction: (_, buf, off, cnt) =>
            {
                lock (sink) { sink.Write(buf, off, cnt); }
                onTxSlice?.Invoke(cnt);
            },
            getMaxLogFileSizeBytes: getMaxBytes ?? (() => 0L),
            getLogLimitAction: getAction ?? (() => LogLimitAction.Rotate),
            isMaxLogFileSizeValid: isMaxSizeValid ?? (() => true),
            reportNotification: (sev, title, msg, port, key) =>
                notifications.Add(new RecordedNotification(sev, title, msg, port, key)));

        vm.SelectedPort = ports[0];
        vm.BaudRateText = "115200";
        vm.ApplyConfigCommand.Execute(null);
        vm.IsConnected = true;
        return vm;
    }

    [Fact]
    public void PortTabViewModel_LogLimitRotate_EmitsWarningPopupAndKeepsLogging()
    {
        string logDir = Path.Combine(_tempDir, "vm_rotate");
        var notifications = new List<RecordedNotification>();
        var vm = CreateMockTab(
            logDir,
            "rot_{port}",
            notifications,
            getMaxBytes: () => 32,
            getAction: () => LogLimitAction.Rotate);
        vm.SelectedLogFormat = LogFormat.Binary;

        vm.StartLogging();
        Assert.True(vm.IsLogging);

        byte[] payload = Enumerable.Repeat((byte)0x3C, 70).ToArray();
        vm.EnqueueLogData(new DataPacket(0, "COM99", payload));

        WaitUntil(() =>
        {
            vm.FlushDisplay();
            return notifications.Count >= 2 && vm.BytesLogged == payload.Length;
        });

        Assert.True(vm.IsLogging);
        Assert.All(notifications, n =>
        {
            Assert.Equal(NotificationSeverity.Warning, n.Severity);
            Assert.Equal("log-rotated:COM99", n.CoalesceKey);
            Assert.Contains("Rotated", n.Title);
        });

        vm.Terminal.TryTakeUpdate(out _, out string termText);
        Assert.Contains("rotated to rot_COM99_part2.bin", termText);
        Assert.Contains("rotated to rot_COM99_part3.bin", termText);

        vm.StopLogging();
        Assert.False(vm.IsLogging);
    }

    [Fact]
    public void PortTabViewModel_LogLimitStop_StopsLoggingAndEmitsWarningPopup()
    {
        string logDir = Path.Combine(_tempDir, "vm_stop");
        var notifications = new List<RecordedNotification>();
        var vm = CreateMockTab(
            logDir,
            "stop_{port}",
            notifications,
            getMaxBytes: () => 32,
            getAction: () => LogLimitAction.Stop);
        vm.SelectedLogFormat = LogFormat.Binary;

        vm.StartLogging();
        Assert.True(vm.IsLogging);

        byte[] payload = Enumerable.Repeat((byte)0x4D, 80).ToArray();
        vm.EnqueueLogData(new DataPacket(0, "COM99", payload));

        WaitUntil(() =>
        {
            vm.FlushDisplay();
            return !vm.IsLogging;
        });

        Assert.False(vm.IsLogging);
        Assert.True(vm.OpenLogFileCommand.CanExecute(null));
        Assert.Equal(32, vm.BytesLogged);
        Assert.Contains("limit", vm.StatusText, StringComparison.OrdinalIgnoreCase);

        var popup = Assert.Single(notifications);
        Assert.Equal(NotificationSeverity.Warning, popup.Severity);
        Assert.Contains("Stopped", popup.Title);
        Assert.Contains("stop_COM99.bin", popup.Message);

        vm.Terminal.TryTakeUpdate(out _, out string termText);
        Assert.Contains("stopped logging", termText);
    }

    [Fact]
    public void PortTabViewModel_LogStartInvalidSizeOrOpenError_EmitsErrorPopup()
    {
        string logDir = Path.Combine(_tempDir, "vm_errors");
        Directory.CreateDirectory(logDir);

        // 1. Invalid Max Size setting blocks logging start and shows Error popup.
        var invalidSizeNotifications = new List<RecordedNotification>();
        var invalidVm = CreateMockTab(
            logDir,
            "{port}",
            invalidSizeNotifications,
            isMaxSizeValid: () => false);
        invalidVm.SelectedLogFormat = LogFormat.Binary;

        invalidVm.StartLogging();
        Assert.False(invalidVm.IsLogging);
        var invalidPopup = Assert.Single(invalidSizeNotifications);
        Assert.Equal(NotificationSeverity.Error, invalidPopup.Severity);
        Assert.Contains("Logging Failed to Start", invalidPopup.Title);

        // 2. Exclusive lock on log file target fails Start() and shows Error popup.
        string lockedPath = Path.Combine(logDir, "COM99.bin");
        using var lockStream = new FileStream(lockedPath, FileMode.Create, FileAccess.Write, FileShare.None);

        var openErrNotifications = new List<RecordedNotification>();
        var openErrVm = CreateMockTab(logDir, "{port}", openErrNotifications);
        openErrVm.SelectedLogFormat = LogFormat.Binary;

        openErrVm.StartLogging();
        Assert.False(openErrVm.IsLogging);
        var openPopup = Assert.Single(openErrNotifications);
        Assert.Equal(NotificationSeverity.Error, openPopup.Severity);
        Assert.Contains("Logging Failed to Start", openPopup.Title);
    }

    [Fact]
    public async Task PortTabViewModel_SendFile_CompletedAndFailedEmitPopups_CancelDoesNot()
    {
        string logDir = Path.Combine(_tempDir, "vm_sendfile");
        var notifications = new List<RecordedNotification>();
        var sink = new MemoryStream();
        int writes = 0;
        PortTabViewModel? vm = null;

        vm = CreateMockTab(
            logDir,
            "{port}",
            notifications,
            txSink: sink,
            onTxSlice: _ =>
            {
                writes++;
                if (writes == 2 && vm!.FileDelayMsText == "150")
                {
                    vm.CancelFileSend();
                }
            });

        // 1. Successful transfer -> Success popup
        string okPath = Path.Combine(_tempDir, "ok.bin");
        File.WriteAllBytes(okPath, Encoding.ASCII.GetBytes("HELLO"));
        vm.SelectedFilePath = okPath;
        vm.SelectedFileSendMode = FileSendMode.Raw;
        vm.FileChunkSizeText = "16";
        vm.FileDelayMsText = "0";
        await vm.SendFileCoreAsync();

        Assert.Single(notifications);
        Assert.Equal(NotificationSeverity.Success, notifications[0].Severity);
        Assert.Contains("File Send Completed", notifications[0].Title);

        // 2. Missing file -> Error popup
        vm.SelectedFilePath = Path.Combine(_tempDir, "does_not_exist.bin");
        await vm.SendFileCoreAsync();

        Assert.Equal(2, notifications.Count);
        Assert.Equal(NotificationSeverity.Error, notifications[1].Severity);
        Assert.Contains("File Send Failed", notifications[1].Title);

        // 3. Invalid hex file -> Error popup
        string badHexPath = Path.Combine(_tempDir, "bad.hex");
        File.WriteAllText(badHexPath, "48 ZZ");
        vm.SelectedFilePath = badHexPath;
        vm.SelectedFileSendMode = FileSendMode.Hex;
        await vm.SendFileCoreAsync();

        Assert.Equal(3, notifications.Count);
        Assert.Equal(NotificationSeverity.Error, notifications[2].Severity);
        Assert.Contains("File Send Failed", notifications[2].Title);

        // 4. User-cancelled transfer -> NO popup added
        string cancelPath = Path.Combine(_tempDir, "cancel.bin");
        File.WriteAllBytes(cancelPath, Enumerable.Repeat((byte)0x7E, 500).ToArray());
        vm.SelectedFilePath = cancelPath;
        vm.SelectedFileSendMode = FileSendMode.Raw;
        vm.FileChunkSizeText = "100";
        vm.FileDelayMsText = "150";
        writes = 0;
        await vm.SendFileCoreAsync();

        Assert.Equal(3, notifications.Count);
    }

    [Fact]
    public void MainViewModel_Notifications_ParseCoalescePinExpireAndCap()
    {
        Assert.True(MainViewModel.TryParseMaxLogSizeBytes("0", out long b0));
        Assert.Equal(0, b0);
        Assert.True(MainViewModel.TryParseMaxLogSizeBytes("", out long bEmpty));
        Assert.Equal(0, bEmpty);
        Assert.True(MainViewModel.TryParseMaxLogSizeBytes("10", out long b10));
        Assert.Equal(10L * 1024 * 1024, b10);
        Assert.True(MainViewModel.TryParseMaxLogSizeBytes("102400", out long bMax));
        Assert.Equal(102_400L * 1024 * 1024, bMax);
        Assert.False(MainViewModel.TryParseMaxLogSizeBytes("-1", out _));
        Assert.False(MainViewModel.TryParseMaxLogSizeBytes("102401", out _));
        Assert.False(MainViewModel.TryParseMaxLogSizeBytes("abc", out _));

        var mainVm = new MainViewModel();
        try
        {
            mainVm.MaxLogFileSizeMbText = "abc";
            Assert.False(mainVm.IsMaxLogFileSizeValid);
            Assert.Equal(0, mainVm.MaxLogFileSizeBytes);

            mainVm.MaxLogFileSizeMbText = "25";
            Assert.True(mainVm.IsMaxLogFileSizeValid);
            Assert.Equal(25L * 1024 * 1024, mainVm.MaxLogFileSizeBytes);

            // Coalescing repeated Warning notifications
            mainVm.PostNotification(NotificationSeverity.Warning, "COM1 — Rotated", "Part 2", "COM1", "log-rotated:COM1");
            mainVm.PostNotification(NotificationSeverity.Warning, "COM1 — Rotated", "Part 3", "COM1", "log-rotated:COM1");

            var rotatedCard = Assert.Single(mainVm.Notifications);
            Assert.Equal(2, rotatedCard.OccurrenceCount);
            Assert.Equal("COM1 — Rotated (×2)", rotatedCard.DisplayTitle);
            Assert.Equal("Part 3", rotatedCard.Message);
            Assert.False(rotatedCard.AutoDismiss);
            Assert.True(mainVm.HasNotifications);
            Assert.False(mainVm.HasMultipleNotifications);

            // Success auto-dismisses while pinned Warning remains
            mainVm.PostNotification(NotificationSeverity.Success, "Sent", "Done", "COM1");
            Assert.Equal(2, mainVm.Notifications.Count);
            Assert.True(mainVm.HasMultipleNotifications);

            mainVm.ExpireNotifications(DateTime.UtcNow.AddSeconds(MainViewModel.InfoAutoDismissSeconds + 2));
            Assert.Single(mainVm.Notifications);
            Assert.Equal(NotificationSeverity.Warning, mainVm.Notifications[0].Severity);

            // Stack cap evicts auto-dismissible cards before pinned Warning/Error cards
            mainVm.PostNotification(NotificationSeverity.Success, "Info 1", "Auto 1", "COM1");
            mainVm.PostNotification(NotificationSeverity.Error, "Err 1", "Pinned 1", "COM2");
            mainVm.PostNotification(NotificationSeverity.Error, "Err 2", "Pinned 2", "COM3");
            mainVm.PostNotification(NotificationSeverity.Error, "Err 3", "Pinned 3", "COM4");
            // Adding 6th card must evict "Info 1" (AutoDismiss == true) and keep all 4 pinned cards + new card.
            mainVm.PostNotification(NotificationSeverity.Error, "Err 4", "Pinned 4", "COM5");

            Assert.Equal(MainViewModel.MaxVisibleNotifications, mainVm.Notifications.Count);
            Assert.DoesNotContain(mainVm.Notifications, n => n.Title == "Info 1");
            Assert.Contains(mainVm.Notifications, n => n.CoalesceKey == "log-rotated:COM1");

            // Dismiss single card & Dismiss All
            mainVm.Notifications[0].DismissCommand.Execute(null);
            Assert.Equal(MainViewModel.MaxVisibleNotifications - 1, mainVm.Notifications.Count);

            Assert.True(mainVm.DismissAllNotificationsCommand.CanExecute(null));
            mainVm.DismissAllNotificationsCommand.Execute(null);
            Assert.Empty(mainVm.Notifications);
            Assert.False(mainVm.HasNotifications);
            Assert.False(mainVm.DismissAllNotificationsCommand.CanExecute(null));
        }
        finally
        {
            mainVm.Shutdown();
        }
    }
#endif
}
