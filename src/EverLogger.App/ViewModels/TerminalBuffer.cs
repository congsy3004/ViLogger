using System;
using System.Text;
using EverLogger.Core.Logging;

namespace EverLogger.App.ViewModels;

/// <summary>
/// Text model behind a port's terminal view. Converts received bytes to display text and keeps
/// only the most recent part of it.
/// </summary>
/// <remarks>
/// <para>
/// The terminal is a quick view, not a record — the log file is the record. This class keeps the
/// view cheap and predictable:
/// </para>
/// <list type="bullet">
/// <item>No per-byte or per-packet UI work. Text is collected here and handed to the view in one
///       piece per UI tick via <see cref="TryTakeUpdate"/>.</item>
/// <item>Bounded memory. When the text exceeds <see cref="MaxChars"/>, the oldest part is dropped
///       (at a line boundary) and the view is told to reload the whole remaining text once.</item>
/// <item>No state shared with other threads: every member must be called on the UI thread.</item>
/// </list>
/// </remarks>
public sealed class TerminalBuffer
{
    /// <summary>Maximum characters kept for display.</summary>
    public const int MaxChars = 120_000;

    /// <summary>Characters kept after trimming (trimming happens in large steps, so rarely).</summary>
    public const int TrimToChars = 80_000;

    private const int BytesPerHexLine = 16;
    private const string HexDigits = "0123456789ABCDEF";

    private readonly StringBuilder _text = new();     // everything currently displayable
    private readonly StringBuilder _pending = new();  // appended since the view last synced
    private readonly StringBuilder _scratch = new();
    private bool _resetPending = true;                 // view must reload the whole text

    private LogFormat? _lastFormat;
    private bool _atLineStart = true;
    private bool _prevWasCr;
    private int _hexColumn;
    private long _binaryBytes;

    /// <summary>True if the view is out of date.</summary>
    public bool HasUpdate => _resetPending || _pending.Length > 0 || _binaryBytes > 0;

    /// <summary>
    /// Appends received bytes, rendered in the given view format.
    /// </summary>
    public void AppendBytes(byte[] data, LogFormat format)
    {
        if (data.Length == 0) return;

        if (_lastFormat != format)
        {
            // Start the new representation on a fresh line.
            FlushBinaryCount();
            EnsureLineStart();
            _lastFormat = format;
            _prevWasCr = false;
        }

        switch (format)
        {
            case LogFormat.Ascii:
                AppendAscii(data);
                break;
            case LogFormat.Hex:
                AppendHex(data);
                break;
            default:
                // Binary view shows only byte counts; consecutive packets are combined into one
                // line per UI update rather than one line per driver read.
                _binaryBytes += data.Length;
                break;
        }
    }

    /// <summary>
    /// Appends a complete line of informational text (TX echo, connect/disconnect notices).
    /// Always starts on its own line.
    /// </summary>
    public void AppendLine(string line)
    {
        FlushBinaryCount();
        EnsureLineStart();
        Append(line);
        Append("\n");
        _atLineStart = true;
        _prevWasCr = false;
    }

    /// <summary>
    /// Removes all text. The view will be emptied on its next update.
    /// </summary>
    public void Clear()
    {
        _text.Clear();
        _pending.Clear();
        _resetPending = true;
        _atLineStart = true;
        _prevWasCr = false;
        _hexColumn = 0;
        _binaryBytes = 0;
    }

    /// <summary>
    /// Returns what the view needs to become up to date, and marks the view as up to date.
    /// </summary>
    /// <param name="reset">True: replace the whole view text. False: append.</param>
    /// <param name="text">The full text (reset) or the text to append.</param>
    /// <returns>False if the view is already up to date.</returns>
    public bool TryTakeUpdate(out bool reset, out string text)
    {
        FlushBinaryCount();

        if (_resetPending)
        {
            reset = true;
            text = _text.ToString();
            _resetPending = false;
            _pending.Clear();
            return true;
        }

        if (_pending.Length > 0)
        {
            reset = false;
            text = _pending.ToString();
            _pending.Clear();
            return true;
        }

        reset = false;
        text = string.Empty;
        return false;
    }

    /// <summary>
    /// Forces the next update to be a full reload (used when a view is (re)attached).
    /// </summary>
    public void RequestReset()
    {
        _resetPending = true;
        _pending.Clear();
    }

    // ───────────────── Rendering ─────────────────

    private void AppendAscii(byte[] data)
    {
        _scratch.Clear();
        foreach (byte b in data)
        {
            switch (b)
            {
                case (byte)'\r':
                    // CR, LF and CRLF all end a line (CRLF counts once).
                    _scratch.Append('\n');
                    _atLineStart = true;
                    _prevWasCr = true;
                    continue;
                case (byte)'\n':
                    if (!_prevWasCr)
                    {
                        _scratch.Append('\n');
                        _atLineStart = true;
                    }
                    _prevWasCr = false;
                    continue;
                case (byte)'\t':
                    _scratch.Append('\t');
                    break;
                default:
                    _scratch.Append(b < 0x20 || b > 0x7E ? '.' : (char)b);
                    break;
            }
            _atLineStart = false;
            _prevWasCr = false;
        }
        Append(_scratch);
    }

    private void AppendHex(byte[] data)
    {
        _scratch.Clear();
        foreach (byte b in data)
        {
            if (_hexColumn > 0) _scratch.Append(' ');
            _scratch.Append(HexDigits[b >> 4]).Append(HexDigits[b & 0x0F]);
            _atLineStart = false;

            if (++_hexColumn == BytesPerHexLine)
            {
                _scratch.Append('\n');
                _hexColumn = 0;
                _atLineStart = true;
            }
        }
        Append(_scratch);
    }

    private void FlushBinaryCount()
    {
        if (_binaryBytes == 0) return;
        long count = _binaryBytes;
        _binaryBytes = 0;
        EnsureLineStart();
        Append($"[Binary data: {count:N0} bytes]\n");
        _atLineStart = true;
    }

    private void EnsureLineStart()
    {
        if (!_atLineStart)
        {
            Append("\n");
            _atLineStart = true;
        }
        _hexColumn = 0;
    }

    // ───────────────── Storage ─────────────────

    private void Append(string s)
    {
        if (s.Length == 0) return;
        _text.Append(s);
        if (!_resetPending) _pending.Append(s);
        TrimIfNeeded();
    }

    private void Append(StringBuilder sb)
    {
        if (sb.Length == 0) return;
        _text.Append(sb);
        if (!_resetPending) _pending.Append(sb);
        TrimIfNeeded();
    }

    private void TrimIfNeeded()
    {
        if (_text.Length <= MaxChars) return;

        int cut = _text.Length - TrimToChars;

        // Prefer cutting just after a line break so the view starts with a whole line.
        int limit = Math.Min(_text.Length, cut + 2000);
        for (int i = cut; i < limit; i++)
        {
            if (_text[i] == '\n')
            {
                cut = i + 1;
                break;
            }
        }

        _text.Remove(0, cut);

        // The view's copy no longer matches; reload it once instead of appending.
        _resetPending = true;
        _pending.Clear();
    }
}
