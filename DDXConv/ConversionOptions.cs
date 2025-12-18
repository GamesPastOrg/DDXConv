namespace DDXConv;

public class ConversionOptions
{
    public bool SaveAtlas { get; set; }
    public bool SaveRaw { get; set; }
    public bool SaveMips { get; set; }
    public bool NoUntileAtlas { get; set; }
    public bool SkipEndianSwap { get; set; }
    public bool NoUntile { get; set; }  // Skip ALL untiling - output raw tiled data

    // Experimental: allow processing 3XDR files instead of failing fast
    public bool Enable3Xdr { get; set; }

    // Emit heuristic decision trace logs alongside output
    public bool TraceHeuristics { get; set; }

    // Save raw atlas chunk before any untiling
    public bool SaveAtlasRaw { get; set; }
}