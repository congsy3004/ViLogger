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
/// Automated tests for <see cref="FileSender"/> (cross-platform Core) and
/// <see cref="PortTabViewModel"/> file transmit integration (Windows WPF).
/// </summary>
public sealed class FileSenderAndViewModelTests : IDisposable
{
    private readonly string _tempDir;

    public FileSenderAndViewModelTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ARVSerialTool_Tests_" + Guid.NewGuid().ToString("N"));
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

    private string WriteTempBytes(string name, byte[] data)
    {
        string path = Path.Combine(_tempDir, name);
        File.WriteAllBytes(path, data);
        return path;
    }

    [Fact]
    public async Task RawMode_BinaryPayloadAcrossChunkSizes_PreservesExactBytes()
    {
        byte[] payload = new byte[10_000];
        for (int i = 0; i < payload.Length; i++)
            payload[i] = (byte)(i & 0xFF);

        string path = WriteTempBytes("raw10k.bin", payload);

        foreach (int chunkSize in new[] { 1, 256, 4096 })
        {
            var received = new MemoryStream();
            var result = await FileSender.SendAsync(
                new FileSendOptions { FilePath = path, Mode = FileSendMode.Raw, ChunkSize = chunkSize, BaudRate = 921600 },
                (buf, off, cnt) => received.Write(buf, off, cnt));

            byte[] actual = received.ToArray();
            Assert.Equal(payload.Length, result.WireBytesSent);
            Assert.True(actual.SequenceEqual(payload), $"ChunkSize={chunkSize}: received bytes differ from source.");
        }
    }

    [Fact]
    public async Task RawMode_InterChunkDelay_PacesBetweenChunks()
    {
        // 3 chunks of 100 bytes => 2 inter-chunk delays of 60 ms => ~120 ms (no trailing delay after EOF)
        byte[] payload = Enumerable.Repeat((byte)0xA5, 250).ToArray();
        string path = WriteTempBytes("raw_delay.bin", payload);

        var sw = Stopwatch.StartNew();
        var received = new MemoryStream();
        await FileSender.SendAsync(
            new FileSendOptions { FilePath = path, Mode = FileSendMode.Raw, ChunkSize = 100, DelayMs = 60, BaudRate = 921600 },
            (buf, off, cnt) => received.Write(buf, off, cnt));
        sw.Stop();

        Assert.Equal(250, received.Length);
        Assert.True(sw.ElapsedMilliseconds >= 95, $"Expected >= 95 ms elapsed for 2x60ms gaps, got {sw.ElapsedMilliseconds} ms.");
    }

    [Theory]
    [InlineData(9600, 192)]
    [InlineData(300, 6)]
    [InlineData(50, 1)]
    public async Task SlowBaud_WireSlicing_CapsSliceSize(int baudRate, int expectedMaxSlice)
    {
        byte[] payload = Enumerable.Range(0, 2048).Select(i => (byte)i).ToArray();
        string path = WriteTempBytes($"slowbaud_{baudRate}.bin", payload);

        var sliceSizes = new List<int>();
        var received = new MemoryStream();
        await FileSender.SendAsync(
            new FileSendOptions { FilePath = path, Mode = FileSendMode.Raw, ChunkSize = 2048, BaudRate = baudRate },
            (buf, off, cnt) =>
            {
                sliceSizes.Add(cnt);
                received.Write(buf, off, cnt);
            });

        Assert.True(received.ToArray().SequenceEqual(payload), $"Baud={baudRate}: data mismatch.");
        Assert.All(sliceSizes, s => Assert.InRange(s, 1, expectedMaxSlice));
    }

    [Fact]
    public async Task TextMode_BoundaryAndHighBytes_HandlesSplitCrlf()
    {
        // Place '\r' at index 8191 and '\n' at index 8192 across the internal 8192-byte read boundary
        var ms = new MemoryStream();
        ms.Write(Enumerable.Repeat((byte)'A', 8191).ToArray());
        ms.Write("\r\n"u8);
        ms.Write("Line2\rLine3\n\r\n"u8);
        ms.Write(new byte[] { 0x80, 0x9F, 0xC3, 0xBF });

        byte[] source = ms.ToArray();
        string path = WriteTempBytes("text_asis.txt", source);

        var received = new MemoryStream();
        await FileSender.SendAsync(
            new FileSendOptions { FilePath = path, Mode = FileSendMode.Text, LineEnding = FileLineEnding.AsIs, BaudRate = 921600 },
            (buf, off, cnt) => received.Write(buf, off, cnt));

        Assert.True(received.ToArray().SequenceEqual(source), "Text AsIs must preserve exact bytes across 8192-byte CRLF boundary and high bytes.");

        // Verify CRLF normalization merges the split '\r' + '\n' into a single CRLF
        var receivedCrlf = new MemoryStream();
        await FileSender.SendAsync(
            new FileSendOptions { FilePath = path, Mode = FileSendMode.Text, LineEnding = FileLineEnding.CRLF, BaudRate = 921600 },
            (buf, off, cnt) => receivedCrlf.Write(buf, off, cnt));

        var expectedCrlf = new MemoryStream();
        expectedCrlf.Write(Enumerable.Repeat((byte)'A', 8191).ToArray());
        expectedCrlf.Write("\r\n"u8);
        expectedCrlf.Write("Line2\r\nLine3\r\n\r\n"u8);
        expectedCrlf.Write(new byte[] { 0x80, 0x9F, 0xC3, 0xBF });
        expectedCrlf.Write("\r\n"u8);
        Assert.True(receivedCrlf.ToArray().SequenceEqual(expectedCrlf.ToArray()), "Text CRLF split across 8192 boundary produced wrong line breaks.");
    }

    [Theory]
    [InlineData(FileLineEnding.CRLF, "\r\n")]
    [InlineData(FileLineEnding.LF, "\n")]
    [InlineData(FileLineEnding.CR, "\r")]
    public async Task TextMode_LineEndingNormalization_PreservesHighBytesAndReportsProgress(FileLineEnding eol, string expectedEol)
    {
        byte[] tailBytes = { 0xC3, 0xA9, 0x80, 0xFF };
        byte[] src = Encoding.ASCII.GetBytes("One\r\nTwo\nThree\rFour\r\n\n").Concat(tailBytes).ToArray();
        string path = WriteTempBytes($"text_mixed_{eol}.txt", src);

        var received = new MemoryStream();
        var lastProgress = (cur: 0L, tot: 0L);
        await FileSender.SendAsync(
            new FileSendOptions { FilePath = path, Mode = FileSendMode.Text, LineEnding = eol, BaudRate = 921600 },
            (buf, off, cnt) => received.Write(buf, off, cnt),
            onProgress: (_, c, t) => lastProgress = (c, t));

        byte[] expected = Encoding.ASCII.GetBytes($"One{expectedEol}Two{expectedEol}Three{expectedEol}Four{expectedEol}{expectedEol}")
            .Concat(tailBytes)
            .Concat(Encoding.ASCII.GetBytes(expectedEol))
            .ToArray();

        Assert.True(received.ToArray().SequenceEqual(expected), $"EOL={eol} mismatch.");
        Assert.Equal(src.Length, lastProgress.cur);
        Assert.Equal(src.Length, lastProgress.tot);
    }

    [Fact]
    public async Task HexMode_RoundTripsLogFileWriterHexCapture()
    {
        string logDir = Path.Combine(_tempDir, "hexlog");
        var rng = new Random(42);
        byte[] original = new byte[500];
        rng.NextBytes(original);

        var writer = new LogFileWriter(logDir, LogFormat.Hex, new LogFileNameTemplate("roundtrip", ".log"), "COM99");
        writer.Start();
        writer.Queue.TryWrite(new DataPacket(0, "COM99", original[..123]));
        writer.Queue.TryWrite(new DataPacket(0, "COM99", original[123..]));
        writer.Stop();

        string logPath = writer.CurrentFilePath!;
        var received = new MemoryStream();
        var result = await FileSender.SendAsync(
            new FileSendOptions { FilePath = logPath, Mode = FileSendMode.Hex, ChunkSize = 64, BaudRate = 921600 },
            (buf, off, cnt) => received.Write(buf, off, cnt));

        Assert.Equal(original.Length, result.WireBytesSent);
        Assert.True(received.ToArray().SequenceEqual(original), "Hex log round-trip bytes differ from original.");
    }

    [Fact]
    public async Task HexMode_ParsesPrefixesSeparatorsBomAndComments()
    {
        byte[] bom = { 0xEF, 0xBB, 0xBF };
        string content =
            "# Leading comment\n" +
            "0x48, 0X65; 6C-6C 6F // inline comment\r\n" +
            "0D0A 0x00FF 01\n" +
            "0xAB#nospacecomment\n" +
            "CD//nospacecomment2\n";

        byte[] fullBytes = bom.Concat(Encoding.ASCII.GetBytes(content)).ToArray();
        string path = WriteTempBytes("variants.hex", fullBytes);

        byte[] expected = { 0x48, 0x65, 0x6C, 0x6C, 0x6F, 0x0D, 0x0A, 0x00, 0xFF, 0x01, 0xAB, 0xCD };
        var received = new MemoryStream();
        await FileSender.SendAsync(
            new FileSendOptions { FilePath = path, Mode = FileSendMode.Hex, ChunkSize = 5, BaudRate = 921600 },
            (buf, off, cnt) => received.Write(buf, off, cnt));

        Assert.True(received.ToArray().SequenceEqual(expected), "Hex variants decode mismatch.");
    }

    [Fact]
    public async Task PreValidation_InvalidOrEmptyFiles_WriteZeroWireBytes()
    {
        (string name, string? ascii, byte[]? raw, FileSendMode mode, Type expectedEx)[] cases =
        {
            ("empty.bin", null, Array.Empty<byte>(), FileSendMode.Raw, typeof(InvalidDataException)),
            ("comments_only.hex", "# comment\n// another\n   \r\n", null, FileSendMode.Hex, typeof(FormatException)),
            ("odd_mid.hex", "48 6 6C", null, FileSendMode.Hex, typeof(FormatException)),
            ("odd_long.hex", "48656", null, FileSendMode.Hex, typeof(FormatException)),
            ("lone_0x.hex", "48 0x 6C", null, FileSendMode.Hex, typeof(FormatException)),
            ("bad_0x_prefix.hex", "10x48", null, FileSendMode.Hex, typeof(FormatException)),
            ("invalid_char.hex", "48 ZZ", null, FileSendMode.Hex, typeof(FormatException)),
            ("intel_hex.hex", ":10010000214601360121470136007EFE09D2190140\r\n", null, FileSendMode.Hex, typeof(FormatException)),
            ("srec.s19", "S1137AF00A0A0D0000000000000000000000000061\r\n", null, FileSendMode.Hex, typeof(FormatException))
        };

        foreach (var c in cases)
        {
            string path = WriteTempBytes(c.name, c.raw ?? Encoding.ASCII.GetBytes(c.ascii!));
            long bytesWritten = 0;

            Exception? caught = await Record.ExceptionAsync(() =>
                FileSender.SendAsync(
                    new FileSendOptions { FilePath = path, Mode = c.mode, BaudRate = 921600 },
                    (_, _, cnt) => bytesWritten += cnt));

            Assert.NotNull(caught);
            Assert.IsType(c.expectedEx, caught);
            Assert.Equal(0, bytesWritten);
        }
    }

    [Fact]
    public async Task Cancellation_MidTransfer_StopsCleanly()
    {
        byte[] payload = Enumerable.Repeat((byte)0x55, 2000).ToArray();
        string path = WriteTempBytes("cancel.bin", payload);

        using var cts = new CancellationTokenSource();
        long sent = 0;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await FileSender.SendAsync(
                new FileSendOptions { FilePath = path, Mode = FileSendMode.Raw, ChunkSize = 256, DelayMs = 100, BaudRate = 921600 },
                (_, _, cnt) =>
                {
                    sent += cnt;
                    if (sent >= 512)
                        cts.Cancel();
                },
                ct: cts.Token);
        });

        Assert.InRange(sent, 512, payload.Length - 1);
    }

    [Fact]
    public async Task WriteError_PropagatesToCaller()
    {
        byte[] payload = new byte[512];
        string path = WriteTempBytes("writeerr.bin", payload);

        await Assert.ThrowsAsync<TimeoutException>(() =>
            FileSender.SendAsync(
                new FileSendOptions { FilePath = path, Mode = FileSendMode.Raw, ChunkSize = 128, BaudRate = 921600 },
                (_, _, _) => throw new TimeoutException("Simulated write timeout")));
    }

#if WINDOWS_WPF_TESTS
    private PortTabViewModel CreateMockTab(MemoryStream sink, List<string> statuses, Action<int>? onSlice = null)
    {
        var ports = new ObservableCollection<PortInfo> { new PortInfo("COM99", "Mock Serial") };
        var bauds = new ObservableCollection<int> { 9600, 115200 };

        var vm = new PortTabViewModel(
            ports,
            bauds,
            connectAction: _ => { },
            disconnectAction: _ => { },
            sendAction: (_, data) => sink.Write(data, 0, data.Length),
            removeAction: _ => { },
            canRemove: () => false,
            getLogDirectory: () => _tempDir,
            getLogFileTemplate: () => "{port}",
            reportStatus: s => statuses.Add(s),
            sendSliceAction: (_, buf, off, cnt) =>
            {
                lock (sink) { sink.Write(buf, off, cnt); }
                onSlice?.Invoke(cnt);
            });

        vm.SelectedPort = ports[0];
        vm.BaudRateText = "115200";
        vm.ApplyConfigCommand.Execute(null);
        vm.IsConnected = true;
        vm.TxMode = "ASCII";
        vm.TxInput = "PING";
        return vm;
    }

    [Fact]
    public async Task PortTabViewModel_SendFileCoreAsync_LocksManualTxAndUpdatesTerminal()
    {
        byte[] payload = Encoding.ASCII.GetBytes("AT\r\nAT+GMR\r\n");
        string path = WriteTempBytes("vm_cmd.txt", payload);

        var sink = new MemoryStream();
        var statuses = new List<string>();
        bool? sendCmdCanExecuteDuringTransfer = null;

        PortTabViewModel? vmRef = null;
        var vm = CreateMockTab(sink, statuses, _ =>
        {
            if (vmRef != null && sendCmdCanExecuteDuringTransfer == null)
            {
                sendCmdCanExecuteDuringTransfer = vmRef.SendCommand.CanExecute(null);
            }
        });
        vmRef = vm;

        vm.SelectedFilePath = path;
        vm.SelectedFileSendMode = FileSendMode.Raw;
        vm.FileChunkSizeText = "4";
        vm.FileDelayMsText = "10";

        Assert.True(vm.SendFileCommand.CanExecute(null), "SendFileCommand should be executable.");
        await vm.SendFileCoreAsync();
        vm.FlushDisplay();

        Assert.False(sendCmdCanExecuteDuringTransfer);
        Assert.True(vm.SendCommand.CanExecute(null), "Manual SendCommand must re-enable after transfer finishes.");
        Assert.False(vm.IsSendingFile);
        Assert.Equal(payload.Length, vm.BytesSent);
        Assert.True(sink.ToArray().SequenceEqual(payload), "Sent bytes mismatch.");
        Assert.Equal(100, vm.FileSendProgressPercent);

        vm.Terminal.TryTakeUpdate(out _, out string termText);
        Assert.Contains("TX File start: vm_cmd.txt (Raw)", termText);
        Assert.Contains("TX File complete: vm_cmd.txt", termText);
    }

    [Fact]
    public async Task PortTabViewModel_AutoCancelsActiveFileSendOnDisconnect()
    {
        byte[] payload = Enumerable.Repeat((byte)0x7E, 1000).ToArray();
        string path = WriteTempBytes("vm_disconnect.bin", payload);

        var sink = new MemoryStream();
        var statuses = new List<string>();
        PortTabViewModel? vm = null;
        int writes = 0;

        vm = CreateMockTab(sink, statuses, _ =>
        {
            writes++;
            if (writes == 2)
            {
                // Simulate port disconnecting mid-transfer
                vm!.IsConnected = false;
            }
        });

        vm.SelectedFilePath = path;
        vm.SelectedFileSendMode = FileSendMode.Raw;
        vm.FileChunkSizeText = "100";
        vm.FileDelayMsText = "150";

        await vm.SendFileCoreAsync();
        vm.FlushDisplay();

        Assert.False(vm.IsSendingFile);
        Assert.InRange(sink.Length, 200, payload.Length - 1);
        vm.Terminal.TryTakeUpdate(out _, out string termText);
        Assert.Contains("TX File cancelled: vm_disconnect.bin", termText);
    }
#endif
}
