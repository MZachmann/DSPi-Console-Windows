namespace DSPiConsole.Core.Models;

/// <summary>
/// Crossover filter family. Mirrors the firmware XoverFamily enum
/// (crossover.h). <see cref="None"/> covers FILTER_FLAT and any non-crossover
/// type — i.e. a disabled crossover band.
/// </summary>
public enum XoverFamily
{
    None = 0,
    LinkwitzRiley = 1,
    Butterworth = 2,
    Bessel = 3
}

/// <summary>
/// Decodes/encodes the crossover <see cref="FilterType"/> values (8-39), which
/// pack family + order + shape (HP/LP) into a single enum value. The UI exposes
/// these as three separate pickers (Family / Type / Slope), so it needs to map
/// between (family, isHighPass, order) and the wire <see cref="FilterType"/>.
///
/// Each crossover band addresses a wire band index of
/// <see cref="XoverBandBase"/> + localBand (20..23). See
/// Documentation/Features/crossover_filters_spec.md.
/// </summary>
public static class CrossoverFilter
{
    /// <summary>Wire band index of crossover band 0. Bands run 20..23.</summary>
    public const int XoverBandBase = 20;

    /// <summary>Number of crossover bands per output channel.</summary>
    public const int MaxXoverBands = 4;

    /// <summary>Decoded metadata for a crossover filter type.</summary>
    public readonly record struct Meta(XoverFamily Family, int Order, bool IsHighPass)
    {
        /// <summary>Slope in dB/octave (order × 6).</summary>
        public int SlopeDbPerOct => Order * 6;
    }

    // Single source of truth: every crossover FilterType → (family, order, shape).
    private static readonly IReadOnlyDictionary<FilterType, Meta> Table = new Dictionary<FilterType, Meta>
    {
        [FilterType.Lr2Lp] = new(XoverFamily.LinkwitzRiley, 2, false),
        [FilterType.Lr2Hp] = new(XoverFamily.LinkwitzRiley, 2, true),
        [FilterType.Lr4Lp] = new(XoverFamily.LinkwitzRiley, 4, false),
        [FilterType.Lr4Hp] = new(XoverFamily.LinkwitzRiley, 4, true),
        [FilterType.Lr6Lp] = new(XoverFamily.LinkwitzRiley, 6, false),
        [FilterType.Lr6Hp] = new(XoverFamily.LinkwitzRiley, 6, true),
        [FilterType.Lr8Lp] = new(XoverFamily.LinkwitzRiley, 8, false),
        [FilterType.Lr8Hp] = new(XoverFamily.LinkwitzRiley, 8, true),
        [FilterType.Bw1Lp] = new(XoverFamily.Butterworth, 1, false),
        [FilterType.Bw1Hp] = new(XoverFamily.Butterworth, 1, true),
        [FilterType.Bw2Lp] = new(XoverFamily.Butterworth, 2, false),
        [FilterType.Bw2Hp] = new(XoverFamily.Butterworth, 2, true),
        [FilterType.Bw3Lp] = new(XoverFamily.Butterworth, 3, false),
        [FilterType.Bw3Hp] = new(XoverFamily.Butterworth, 3, true),
        [FilterType.Bw4Lp] = new(XoverFamily.Butterworth, 4, false),
        [FilterType.Bw4Hp] = new(XoverFamily.Butterworth, 4, true),
        [FilterType.Bw5Lp] = new(XoverFamily.Butterworth, 5, false),
        [FilterType.Bw5Hp] = new(XoverFamily.Butterworth, 5, true),
        [FilterType.Bw6Lp] = new(XoverFamily.Butterworth, 6, false),
        [FilterType.Bw6Hp] = new(XoverFamily.Butterworth, 6, true),
        [FilterType.Bw7Lp] = new(XoverFamily.Butterworth, 7, false),
        [FilterType.Bw7Hp] = new(XoverFamily.Butterworth, 7, true),
        [FilterType.Bw8Lp] = new(XoverFamily.Butterworth, 8, false),
        [FilterType.Bw8Hp] = new(XoverFamily.Butterworth, 8, true),
        [FilterType.Bes2Lp] = new(XoverFamily.Bessel, 2, false),
        [FilterType.Bes2Hp] = new(XoverFamily.Bessel, 2, true),
        [FilterType.Bes4Lp] = new(XoverFamily.Bessel, 4, false),
        [FilterType.Bes4Hp] = new(XoverFamily.Bessel, 4, true),
        [FilterType.Bes6Lp] = new(XoverFamily.Bessel, 6, false),
        [FilterType.Bes6Hp] = new(XoverFamily.Bessel, 6, true),
        [FilterType.Bes8Lp] = new(XoverFamily.Bessel, 8, false),
        [FilterType.Bes8Hp] = new(XoverFamily.Bessel, 8, true),
    };

    // Reverse lookup built from the same table.
    private static readonly IReadOnlyDictionary<(XoverFamily, int, bool), FilterType> Reverse =
        Table.ToDictionary(kv => (kv.Value.Family, kv.Value.Order, kv.Value.IsHighPass), kv => kv.Key);

    /// <summary>Orders supported by each family (used to build the Slope picker).</summary>
    private static readonly IReadOnlyDictionary<XoverFamily, int[]> FamilyOrders =
        new Dictionary<XoverFamily, int[]>
        {
            [XoverFamily.LinkwitzRiley] = new[] { 2, 4, 6, 8 },
            [XoverFamily.Butterworth] = new[] { 1, 2, 3, 4, 5, 6, 7, 8 },
            [XoverFamily.Bessel] = new[] { 2, 4, 6, 8 },
        };

    /// <summary>The three selectable crossover families (excludes None/Off).</summary>
    public static readonly XoverFamily[] Families =
        { XoverFamily.LinkwitzRiley, XoverFamily.Butterworth, XoverFamily.Bessel };

    /// <summary>Decode a crossover type. Returns false for FLAT/PEQ/unknown types.</summary>
    public static bool TryGetMeta(FilterType type, out Meta meta) => Table.TryGetValue(type, out meta);

    /// <summary>Family of a crossover type, or <see cref="XoverFamily.None"/> if not a crossover type.</summary>
    public static XoverFamily GetFamily(FilterType type) =>
        Table.TryGetValue(type, out var m) ? m.Family : XoverFamily.None;

    /// <summary>
    /// Compose (family, isHighPass, order) into a crossover <see cref="FilterType"/>.
    /// Returns null when the combination doesn't exist (e.g. a 3rd-order
    /// Linkwitz-Riley). Pass <see cref="XoverFamily.None"/> to get FLAT (off).
    /// </summary>
    public static FilterType? Compose(XoverFamily family, bool isHighPass, int order)
    {
        if (family == XoverFamily.None) return FilterType.Flat;
        return Reverse.TryGetValue((family, order, isHighPass), out var t) ? t : null;
    }

    /// <summary>Orders available for a family, ascending. Empty for None.</summary>
    public static IReadOnlyList<int> OrdersFor(XoverFamily family) =>
        FamilyOrders.TryGetValue(family, out var o) ? o : Array.Empty<int>();

    /// <summary>Human-readable family name, e.g. "Linkwitz-Riley".</summary>
    public static string FamilyName(XoverFamily family) => family switch
    {
        XoverFamily.LinkwitzRiley => "Linkwitz-Riley",
        XoverFamily.Butterworth => "Butterworth",
        XoverFamily.Bessel => "Bessel",
        _ => "Off"
    };

    /// <summary>Short family tag, e.g. "LR" / "BW" / "Bessel".</summary>
    public static string FamilyShortName(XoverFamily family) => family switch
    {
        XoverFamily.LinkwitzRiley => "LR",
        XoverFamily.Butterworth => "BW",
        XoverFamily.Bessel => "Bessel",
        _ => "Off"
    };

    /// <summary>
    /// Parse a short family tag ("LR" / "BW" / "Bessel", case-insensitive) back
    /// into a family. Returns null for unrecognised tokens. Inverse of
    /// <see cref="FamilyShortName"/>; used by the filter file import.
    /// </summary>
    public static XoverFamily? ParseShortFamily(string tag) => tag.Trim().ToUpperInvariant() switch
    {
        "LR" => XoverFamily.LinkwitzRiley,
        "BW" => XoverFamily.Butterworth,
        "BESSEL" => XoverFamily.Bessel,
        _ => null
    };

    /// <summary>Slope label for an order, e.g. "24 dB/oct".</summary>
    public static string SlopeLabel(int order) => $"{order * 6} dB/oct";

    /// <summary>A compact one-line description, e.g. "LR 24 dB/oct HP".</summary>
    public static string Describe(FilterType type) =>
        Table.TryGetValue(type, out var m)
            ? $"{FamilyShortName(m.Family)} {SlopeLabel(m.Order)} {(m.IsHighPass ? "HP" : "LP")}"
            : "Off";
}
