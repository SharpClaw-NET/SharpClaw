using System.Text;
using SharpClaw.Modules.Providers.LlamaSharp.Clients;
using SharpClaw.Contracts.Providers;

namespace SharpClaw.Modules.Providers.LlamaSharp.LocalInference;

/// <summary>
/// Incremental parser for the grammar-constrained tool-call envelope.
/// <para>
/// Consumes raw tokens from the grammar-constrained stream and yields
/// <see cref="ChatToolCallDelta"/> values describing newly-seen portions
/// of the current tool call. Because <see cref="LlamaSharpToolGrammar"/>
/// guarantees the exact envelope shape, this walker needs no family
/// matrix and no lookahead — a small character-by-character state
/// machine is sufficient.
/// </para>
/// <para>
/// The walker is only activated once <c>"mode":"tool_calls"</c> has been
/// detected by the outer streaming loop; it is fed every subsequent
/// character and emits:
/// <list type="bullet">
///   <item>One delta carrying <c>Id</c> when a call's <c>"id":"…"</c> string closes.</item>
///   <item>One delta carrying <c>Name</c> when a call's <c>"name":"…"</c> string closes.</item>
///   <item>A series of deltas carrying <c>ArgumentsFragment</c> as the
///         <c>"args":{…}</c> object streams, one per character (or run
///         of safe characters) emitted inside the outer braces.</item>
/// </list>
/// Drift-tolerant: if the model emits whitespace the grammar still
/// permits, the walker preserves it inside args and skips it between
/// structural tokens. The outer loop's full-buffer parse remains the
/// source of truth for the <c>Final</c> chunk.
/// </para>
/// </summary>
internal sealed class EnvelopeStreamWalker
{
    private enum Phase
    {
        /// <summary>Skip until we've seen the <c>"calls":[</c> opener.</summary>
        SeekingCallsArray,
        /// <summary>Between calls in the array — skip whitespace/commas; <c>{</c> starts a call; <c>]</c> ends the array.</summary>
        BetweenCalls,
        /// <summary>Inside a call object. Looking for <c>"&lt;key&gt;":</c> or <c>}</c>.</summary>
        InCallSeekKey,
        /// <summary>Reading a key string's contents (between the two quotes).</summary>
        ReadingKey,
        /// <summary>Key closed — skip whitespace until <c>:</c>, then value.</summary>
        AfterKey,
        /// <summary>Reading an <c>id</c> string value (between the two quotes).</summary>
        ReadingIdValue,
        /// <summary>Reading a <c>name</c> string value (between the two quotes).</summary>
        ReadingNameValue,
        /// <summary>Reading the <c>args</c> object value, tracking JSON depth + strings.</summary>
        ReadingArgsValue,
        /// <summary>Ignoring an unknown value (string/number/bool/null/object/array) until the next <c>,</c> or <c>}</c> at call-object depth.</summary>
        SkippingUnknownValue,
        /// <summary>After a value — skip whitespace until <c>,</c> (more keys) or <c>}</c> (end of call).</summary>
        AfterValue,
        /// <summary>Envelope consumed through end of calls array; everything after is ignored.</summary>
        Done,
    }

    private Phase _phase = Phase.SeekingCallsArray;
    private int _callIndex = -1;

    // SeekingCallsArray: rolling buffer of recent chars to detect "calls":[
    private readonly StringBuilder _seek = new();

    // ReadingKey accumulator.
    private readonly StringBuilder _keyBuf = new();
    private bool _keyEscape;

    // Value string accumulators.
    private readonly StringBuilder _valueBuf = new();
    private bool _valueEscape;

    // args depth tracking.
    private int _argsDepth;              // 0 before '{' seen; 1 once inside outer '{'.
    private bool _argsInString;
    private bool _argsEscape;
    private readonly StringBuilder _argsPending = new();

    // SkippingUnknownValue tracking — mirrors args structure so we can
    // find the end of the value regardless of shape.
    private int _skipDepth;
    private bool _skipInString;
    private bool _skipEscape;
    private bool _skipStarted;
    private char _skipTerminatorType; // for primitives: 0=unknown, 's'=string, 'p'=primitive

    /// <summary>
    /// Consumes one raw token (may be a single character, may be many)
    /// and returns zero-or-more deltas describing progress through the
    /// current tool call.
    /// </summary>
    public IEnumerable<ChatToolCallDelta> Feed(string token)
    {
        var deltas = new List<ChatToolCallDelta>();
        foreach (var ch in token)
            FeedChar(ch, deltas);
        return deltas;
    }

    private void FeedChar(char ch, List<ChatToolCallDelta> deltas)
    {
        switch (_phase)
        {
            case Phase.SeekingCallsArray:
                _seek.Append(ch);
                // Trim — only need the last ~16 chars to match `"calls":[`.
                if (_seek.Length > 32)
                    _seek.Remove(0, _seek.Length - 32);
                var s = _seek.ToString();
                var idx = s.IndexOf("\"calls\"", StringComparison.Ordinal);
                if (idx >= 0)
                {
                    // Now look for the '[' after possible whitespace and ':'.
                    var rest = s[(idx + "\"calls\"".Length)..];
                    // Track state via a mini-scan: find ':' then '['.
                    var colon = rest.IndexOf(':');
                    if (colon >= 0)
                    {
                        var afterColon = rest[(colon + 1)..];
                        var bracket = afterColon.IndexOf('[');
                        if (bracket >= 0)
                        {
                            _phase = Phase.BetweenCalls;
                            _seek.Clear();
                        }
                    }
                }
                break;

            case Phase.BetweenCalls:
                if (ch == '{')
                {
                    _callIndex++;
                    _phase = Phase.InCallSeekKey;
                }
                else if (ch == ']')
                {
                    _phase = Phase.Done;
                }
                // else whitespace/comma — ignore.
                break;

            case Phase.InCallSeekKey:
                if (ch == '"')
                {
                    _keyBuf.Clear();
                    _keyEscape = false;
                    _phase = Phase.ReadingKey;
                }
                else if (ch == '}')
                {
                    // End of call object.
                    _phase = Phase.BetweenCalls;
                }
                // else whitespace/comma — ignore.
                break;

            case Phase.ReadingKey:
                if (_keyEscape)
                {
                    _keyBuf.Append(ch);
                    _keyEscape = false;
                }
                else if (ch == '\\')
                {
                    _keyBuf.Append(ch);
                    _keyEscape = true;
                }
                else if (ch == '"')
                {
                    _phase = Phase.AfterKey;
                }
                else
                {
                    _keyBuf.Append(ch);
                }
                break;

            case Phase.AfterKey:
                if (ch == ':')
                {
                    var key = _keyBuf.ToString();
                    _valueBuf.Clear();
                    _valueEscape = false;
                    _argsDepth = 0;
                    _argsInString = false;
                    _argsEscape = false;
                    _argsPending.Clear();
                    _skipDepth = 0;
                    _skipInString = false;
                    _skipEscape = false;
                    _skipStarted = false;
                    _skipTerminatorType = (char)0;
                    _phase = key switch
                    {
                        "id"   => Phase.ReadingIdValue,
                        "name" => Phase.ReadingNameValue,
                        "args" => Phase.ReadingArgsValue,
                        _      => Phase.SkippingUnknownValue,
                    };
                    // For id/name we still need to consume whitespace and
                    // the opening quote; handled inline below by re-entry.
                    _awaitingStringOpen = _phase is Phase.ReadingIdValue or Phase.ReadingNameValue;
                    _awaitingArgsOpen = _phase == Phase.ReadingArgsValue;
                }
                // else whitespace — ignore.
                break;

            case Phase.ReadingIdValue:
            case Phase.ReadingNameValue:
                if (_awaitingStringOpen)
                {
                    if (ch == '"')
                        _awaitingStringOpen = false;
                    // else whitespace — ignore.
                    break;
                }
                if (_valueEscape)
                {
                    _valueBuf.Append(ch);
                    _valueEscape = false;
                }
                else if (ch == '\\')
                {
                    _valueBuf.Append(ch);
                    _valueEscape = true;
                }
                else if (ch == '"')
                {
                    // String closed — emit the delta.
                    var value = DecodeJsonString(_valueBuf.ToString());
                    if (_phase == Phase.ReadingIdValue)
                        deltas.Add(new ChatToolCallDelta(_callIndex, Id: value, Name: null, ArgumentsFragment: null));
                    else
                        deltas.Add(new ChatToolCallDelta(_callIndex, Id: null, Name: value, ArgumentsFragment: null));
                    _valueBuf.Clear();
                    _phase = Phase.AfterValue;
                }
                else
                {
                    _valueBuf.Append(ch);
                }
                break;

            case Phase.ReadingArgsValue:
                if (_awaitingArgsOpen)
                {
                    if (ch == '{')
                    {
                        _awaitingArgsOpen = false;
                        _argsDepth = 1;
                        _argsPending.Append(ch);
                    }
                    // else whitespace — ignore.
                    break;
                }

                _argsPending.Append(ch);

                if (_argsInString)
                {
                    if (_argsEscape)
                    {
                        _argsEscape = false;
                    }
                    else if (ch == '\\')
                    {
                        _argsEscape = true;
                    }
                    else if (ch == '"')
                    {
                        _argsInString = false;
                    }
                }
                else
                {
                    if (ch == '"') _argsInString = true;
                    else if (ch == '{' || ch == '[') _argsDepth++;
                    else if (ch == '}' || ch == ']')
                    {
                        _argsDepth--;
                        if (_argsDepth == 0)
                        {
                            // End of args object — flush and transition.
                            var fragment = _argsPending.ToString();
                            _argsPending.Clear();
                            deltas.Add(new ChatToolCallDelta(_callIndex, Id: null, Name: null, ArgumentsFragment: fragment));
                            _phase = Phase.AfterValue;
                            break;
                        }
                    }
                }

                // Flush whenever the pending buffer reaches a boundary
                // that's safe to emit (outside of incomplete escape).
                if (!_argsEscape && _argsPending.Length > 0)
                {
                    var frag = _argsPending.ToString();
                    _argsPending.Clear();
                    deltas.Add(new ChatToolCallDelta(_callIndex, Id: null, Name: null, ArgumentsFragment: frag));
                }
                break;

            case Phase.SkippingUnknownValue:
                if (!_skipStarted)
                {
                    if (char.IsWhiteSpace(ch)) break;
                    _skipStarted = true;
                    if (ch == '"')
                    {
                        _skipTerminatorType = 's';
                        _skipInString = true;
                    }
                    else if (ch == '{' || ch == '[')
                    {
                        _skipTerminatorType = 'o';
                        _skipDepth = 1;
                    }
                    else
                    {
                        // primitive: number / true / false / null — ends at , or } or whitespace.
                        _skipTerminatorType = 'p';
                    }
                    break;
                }

                if (_skipTerminatorType == 's')
                {
                    if (_skipEscape) { _skipEscape = false; }
                    else if (ch == '\\') { _skipEscape = true; }
                    else if (ch == '"') { _phase = Phase.AfterValue; }
                }
                else if (_skipTerminatorType == 'o')
                {
                    if (_skipInString)
                    {
                        if (_skipEscape) _skipEscape = false;
                        else if (ch == '\\') _skipEscape = true;
                        else if (ch == '"') _skipInString = false;
                    }
                    else
                    {
                        if (ch == '"') _skipInString = true;
                        else if (ch == '{' || ch == '[') _skipDepth++;
                        else if (ch == '}' || ch == ']')
                        {
                            _skipDepth--;
                            if (_skipDepth == 0) _phase = Phase.AfterValue;
                        }
                    }
                }
                else // primitive
                {
                    if (ch == ',' || ch == '}' || char.IsWhiteSpace(ch))
                    {
                        _phase = Phase.AfterValue;
                        // Re-dispatch the delimiter at AfterValue.
                        FeedChar(ch, deltas);
                    }
                }
                break;

            case Phase.AfterValue:
                if (ch == ',') _phase = Phase.InCallSeekKey;
                else if (ch == '}') _phase = Phase.BetweenCalls;
                // else whitespace — ignore.
                break;

            case Phase.Done:
                // Ignore trailing envelope characters.
                break;
        }
    }

    // Inline sub-states for value openers.
    private bool _awaitingStringOpen;
    private bool _awaitingArgsOpen;

    /// <summary>
    /// Decodes a raw JSON-string payload (the characters between the two
    /// surrounding quotes, including remaining backslash escapes) into
    /// its logical string value. Used for <c>id</c> and <c>name</c>
    /// deltas so consumers receive decoded text.
    /// </summary>
    private static string DecodeJsonString(string raw)
    {
        if (raw.Length == 0) return string.Empty;
        if (raw.IndexOf('\\') < 0) return raw;

        var sb = new StringBuilder(raw.Length);
        for (var i = 0; i < raw.Length; i++)
        {
            var c = raw[i];
            if (c != '\\' || i + 1 >= raw.Length)
            {
                sb.Append(c);
                continue;
            }
            var next = raw[++i];
            switch (next)
            {
                case '"':  sb.Append('"'); break;
                case '\\': sb.Append('\\'); break;
                case '/':  sb.Append('/'); break;
                case 'b':  sb.Append('\b'); break;
                case 'f':  sb.Append('\f'); break;
                case 'n':  sb.Append('\n'); break;
                case 'r':  sb.Append('\r'); break;
                case 't':  sb.Append('\t'); break;
                case 'u':
                    if (i + 4 < raw.Length
                        && ushort.TryParse(
                            raw.AsSpan(i + 1, 4),
                            System.Globalization.NumberStyles.HexNumber,
                            System.Globalization.CultureInfo.InvariantCulture,
                            out var code))
                    {
                        sb.Append((char)code);
                        i += 4;
                    }
                    else
                    {
                        sb.Append('\\').Append(next);
                    }
                    break;
                default:
                    sb.Append('\\').Append(next);
                    break;
            }
        }
        return sb.ToString();
    }
}
