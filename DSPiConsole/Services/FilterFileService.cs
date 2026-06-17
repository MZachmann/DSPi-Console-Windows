using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using DSPiConsole.Core.Models;

namespace DSPiConsole.Services;

/// <summary>
/// Service for importing and exporting filter settings to/from files.
/// Supports DSPi Console format (multi-channel) and REW format (single-channel).
/// </summary>
public static class FilterFileService
{
    /// <summary>
    /// Generates export string in DSPi Console format. When <paramref name="xoverData"/>
    /// is supplied, each output channel's crossover bands (wire bands 20-23) are
    /// written as <c>Crossover N:</c> lines after its PEQ filters. These lines use a
    /// distinct prefix so older parsers (and the REW reader) skip them harmlessly.
    /// </summary>
    public static string GenerateExportString(
        IReadOnlyDictionary<int, IReadOnlyList<FilterParams>> channelData,
        IReadOnlyDictionary<int, IReadOnlyList<FilterParams>>? xoverData = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# DSPi Console Filter Settings");
        sb.AppendLine($"# Exported: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine();

        foreach (var channel in Channel.All)
        {
            channelData.TryGetValue((int)channel.Id, out var filters);
            bool hasPeq = filters != null && filters.Any(f => f.Type != FilterType.Flat);

            IReadOnlyList<FilterParams>? xover = null;
            xoverData?.TryGetValue((int)channel.Id, out xover);
            bool hasXover = xover != null && xover.Any(f => f.Type.IsCrossover());

            if (!hasPeq && !hasXover)
                continue;

            sb.AppendLine($"[{channel.Name}]");

            if (filters != null)
            {
                for (int i = 0; i < filters.Count; i++)
                    sb.AppendLine(FormatFilter(i + 1, filters[i]));
            }

            if (hasXover)
            {
                for (int i = 0; i < xover!.Count; i++)
                    sb.AppendLine(FormatXoverFilter(i + 1, xover[i]));
            }

            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static string FormatFilter(int index, FilterParams filter)
    {
        var inv = CultureInfo.InvariantCulture;
        if (filter.Type == FilterType.Flat)
        {
            return string.Format(inv, "Filter {0,2}: OFF", index);
        }

        var typeCode = filter.Type switch
        {
            FilterType.Peaking => "PK",
            FilterType.LowShelf => "LS",
            FilterType.HighShelf => "HS",
            FilterType.LowPass => "LP",
            FilterType.HighPass => "HP",
            FilterType.Notch => "NO",
            FilterType.AllPass => "AP",
            _ => "PK"
        };

        var line = string.Format(inv, "Filter {0,2}: ON  {1,-8}Fc {2,7:F1} Hz",
            index, typeCode, filter.Frequency);

        if (filter.Type.HasGain())
        {
            line += string.Format(inv, "  Gain {0,5:+0.0;-0.0} dB", filter.Gain);
        }

        if (filter.Type.HasQ())
        {
            line += string.Format(inv, "  Q {0,5:F2}", filter.Q);
        }

        return line;
    }

    private static string FormatXoverFilter(int index, FilterParams filter)
    {
        var inv = CultureInfo.InvariantCulture;
        if (!CrossoverFilter.TryGetMeta(filter.Type, out var meta))
        {
            return string.Format(inv, "Crossover {0,2}: OFF", index);
        }

        return string.Format(inv,
            "Crossover {0,2}: ON  {1,-6} {2}  Fc {3,7:F1} Hz  Slope {4,3} dB/oct",
            index,
            CrossoverFilter.FamilyShortName(meta.Family),
            meta.IsHighPass ? "HP" : "LP",
            filter.Frequency,
            meta.SlopeDbPerOct);
    }

    /// <summary>
    /// Parses a filter file and returns the detected format and parsed data.
    /// </summary>
    public static ParseResult ParseFile(string contents)
    {
        if (contents.TrimStart().StartsWith("# DSPi Console"))
        {
            var parsed = ParseDSPiFormat(contents);
            if (parsed != null && (parsed.Value.Peq.Count > 0 || parsed.Value.Xover.Count > 0))
            {
                return new ParseResult
                {
                    Format = FilterFileFormat.DSPiConsole,
                    ChannelFilters = parsed.Value.Peq,
                    ChannelXoverFilters = parsed.Value.Xover.Count > 0 ? parsed.Value.Xover : null
                };
            }
        }

        // Try REW format
        var filters = ParseREWFormat(contents);
        if (filters != null && filters.Count > 0)
        {
            return new ParseResult
            {
                Format = FilterFileFormat.REW,
                SingleChannelFilters = filters
            };
        }

        return new ParseResult { Format = FilterFileFormat.Unknown };
    }

    /// <summary>
    /// Parses DSPi Console format (multi-channel). Returns the PEQ filters and the
    /// crossover bands as separate per-channel dictionaries (crossover bands are
    /// only present for output channels written by V11+ exports).
    /// </summary>
    private static (Dictionary<int, List<FilterParams>> Peq, Dictionary<int, List<FilterParams>> Xover)? ParseDSPiFormat(string contents)
    {
        var result = new Dictionary<int, List<FilterParams>>();
        var xoverResult = new Dictionary<int, List<FilterParams>>();
        int? currentChannel = null;

        foreach (var line in contents.Split('\n', '\r'))
        {
            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed)) continue;

            // Check for channel header [Channel Name]
            if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
            {
                var channelName = trimmed[1..^1];
                currentChannel = null;
                foreach (var ch in Channel.All)
                {
                    if (ch.Name.Equals(channelName, StringComparison.OrdinalIgnoreCase))
                    {
                        currentChannel = (int)ch.Id;
                        result[currentChannel.Value] = new List<FilterParams>();
                        break;
                    }
                }
                continue;
            }

            if (currentChannel == null) continue;

            // Crossover band line (output channels, V11+). Checked before the PEQ
            // branch; "Crossover" lines deliberately don't contain "Filter".
            if (trimmed.StartsWith("Crossover", StringComparison.OrdinalIgnoreCase))
            {
                if (!trimmed.Contains(':')) continue;
                var xo = ParseXoverLine(trimmed);
                if (xo != null)
                {
                    if (!xoverResult.TryGetValue(currentChannel.Value, out var xbands))
                        xoverResult[currentChannel.Value] = xbands = new List<FilterParams>();
                    xbands.Add(xo);
                }
                continue;
            }

            // Parse PEQ filter line
            if (!trimmed.Contains("Filter") || !trimmed.Contains(':')) continue;

            var filter = ParseFilterLine(trimmed);
            if (filter != null)
            {
                result[currentChannel.Value].Add(filter);
            }
        }

        return result.Count > 0 || xoverResult.Count > 0 ? (result, xoverResult) : null;
    }

    /// <summary>
    /// Parses a single crossover band line, e.g.
    /// <c>Crossover  1: ON  LR     HP  Fc    80.0 Hz  Slope  24 dB/oct</c>.
    /// Returns a Flat band for OFF lines (to keep band indices aligned), or null
    /// if the family/slope can't be resolved to a real crossover type.
    /// </summary>
    private static FilterParams? ParseXoverLine(string line)
    {
        var upper = line.ToUpperInvariant();

        // Disabled band → flat placeholder so subsequent band indices stay aligned.
        if (upper.Contains(" OFF") || !upper.Contains(" ON "))
        {
            return new FilterParams(FilterType.Flat, 1000, 0.707f, 0);
        }

        // Family tag (LR / BW / Bessel)
        XoverFamily family;
        if (upper.Contains(" LR ")) family = XoverFamily.LinkwitzRiley;
        else if (upper.Contains(" BW ")) family = XoverFamily.Butterworth;
        else if (upper.Contains(" BESSEL ")) family = XoverFamily.Bessel;
        else return null;

        // Shape (HP / LP)
        bool isHighPass;
        if (upper.Contains(" HP ")) isHighPass = true;
        else if (upper.Contains(" LP ")) isHighPass = false;
        else return null;

        // Frequency (Fc XXX Hz)
        float freq = 1000f;
        var fcMatch = Regex.Match(line, @"Fc\s+([\d.,]+)", RegexOptions.IgnoreCase);
        if (fcMatch.Success && TryParseDecimal(fcMatch.Groups[1].Value, out var freqVal))
        {
            freq = freqVal;
        }

        // Slope (NN dB/oct) → filter order = slope / 6
        int order = 4;
        var slopeMatch = Regex.Match(line, @"Slope\s+(\d+)", RegexOptions.IgnoreCase);
        if (slopeMatch.Success && int.TryParse(slopeMatch.Groups[1].Value, out var slope) && slope >= 6)
        {
            order = slope / 6;
        }

        var type = CrossoverFilter.Compose(family, isHighPass, order);
        if (type == null || type == FilterType.Flat) return null;

        return new FilterParams(type.Value, freq, 0.707f, 0);
    }

    /// <summary>
    /// Parses REW format (single-channel).
    /// </summary>
    private static List<FilterParams>? ParseREWFormat(string contents)
    {
        var filters = new List<FilterParams>();

        foreach (var line in contents.Split('\n', '\r'))
        {
            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed)) continue;
            if (!trimmed.Contains("Filter") || !trimmed.Contains(':')) continue;

            var filter = ParseFilterLine(trimmed);
            if (filter != null && filter.Type != FilterType.Flat)
            {
                filters.Add(filter);
            }
        }

        return filters.Count > 0 ? filters : null;
    }

    /// <summary>
    /// Parses a single filter line in REW format.
    /// </summary>
    private static FilterParams? ParseFilterLine(string line)
    {
        var upper = line.ToUpperInvariant();

        // Check if filter is enabled
        if (upper.Contains(" OFF") || !upper.Contains(" ON "))
        {
            return new FilterParams(FilterType.Flat, 1000, 0.707f, 0);
        }

        // Detect filter type
        FilterType filterType;
        if (upper.Contains(" PK ") || upper.Contains(" PEQ "))
            filterType = FilterType.Peaking;
        else if (upper.Contains(" LP ") || upper.Contains(" LPQ "))
            filterType = FilterType.LowPass;
        else if (upper.Contains(" HP ") || upper.Contains(" HPQ "))
            filterType = FilterType.HighPass;
        else if (upper.Contains(" LS ") || upper.Contains(" LSC ") || upper.Contains(" LSQ "))
            filterType = FilterType.LowShelf;
        else if (upper.Contains(" HS ") || upper.Contains(" HSC ") || upper.Contains(" HSQ "))
            filterType = FilterType.HighShelf;
        else if (upper.Contains(" NO ") || upper.Contains(" NOTCH "))
            filterType = FilterType.Notch;
        else if (upper.Contains(" AP ") || upper.Contains(" ALLPASS "))
            filterType = FilterType.AllPass;
        else
            return null;

        // Extract frequency (Fc XXX Hz)
        float freq = 1000f;
        var fcMatch = Regex.Match(line, @"Fc\s+([\d.,]+)", RegexOptions.IgnoreCase);
        if (fcMatch.Success && TryParseDecimal(fcMatch.Groups[1].Value, out var freqVal))
        {
            freq = freqVal;
        }

        // Extract gain (Gain XXX dB)
        float gain = 0f;
        var gainMatch = Regex.Match(line, @"Gain\s+([+-]?[\d.,]+)", RegexOptions.IgnoreCase);
        if (gainMatch.Success && TryParseDecimal(gainMatch.Groups[1].Value, out var gainVal))
        {
            gain = gainVal;
        }

        // Extract Q
        float q = 0.707f;
        var qMatch = Regex.Match(line, @"\sQ\s+([\d.,]+)", RegexOptions.IgnoreCase);
        if (qMatch.Success && TryParseDecimal(qMatch.Groups[1].Value, out var qVal))
        {
            q = qVal;
        }

        return new FilterParams(filterType, freq, q, gain);
    }

    /// <summary>
    /// Parses a numeric token that may use either '.' or ',' as the decimal
    /// separator (REW exports from non-US locales sometimes use commas).
    /// Treats the last separator as the decimal point and strips any thousands
    /// separators before it.
    /// </summary>
    private static bool TryParseDecimal(string token, out float value)
    {
        if (string.IsNullOrEmpty(token))
        {
            value = 0f;
            return false;
        }

        int lastDot = token.LastIndexOf('.');
        int lastComma = token.LastIndexOf(',');
        string normalized;
        if (lastDot < 0 && lastComma < 0)
        {
            normalized = token;
        }
        else
        {
            int sepIndex = Math.Max(lastDot, lastComma);
            char sep = token[sepIndex];
            // Strip the other separator (thousands grouping) and replace the
            // decimal separator with '.' for InvariantCulture parsing.
            normalized = token.Replace(sep == '.' ? "," : ".", string.Empty);
            normalized = normalized.Replace(',', '.');
        }
        return float.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }
}

public enum FilterFileFormat
{
    Unknown,
    DSPiConsole,
    REW
}

public class ParseResult
{
    public FilterFileFormat Format { get; set; }
    public Dictionary<int, List<FilterParams>>? ChannelFilters { get; set; }

    /// <summary>
    /// Per-output-channel crossover bands parsed from a DSPi Console file, or null
    /// when the file contains no crossover sections (e.g. legacy or REW exports).
    /// </summary>
    public Dictionary<int, List<FilterParams>>? ChannelXoverFilters { get; set; }
    public List<FilterParams>? SingleChannelFilters { get; set; }
}
