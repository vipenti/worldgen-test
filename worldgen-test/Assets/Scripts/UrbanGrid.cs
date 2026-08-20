using System.Collections.Generic;
using UnityEngine;

// Two-tier street grid shared by WorldManager (object placement) and Chunk (terrain flattening +
// texturing) for isStructuredBiome layouts: a fine grid of lots separated by minor streets, with
// a wider main avenue inserted every `majorInterval` lots along each axis. Both axes are sampled
// independently with the same 1D layout; a world cell counts as street if either axis says so.
//
// Layout of one "superblock" span (repeats every SuperPeriod units along an axis):
//   [main avenue: majorStreetWidth][lot][minor street][lot][minor street]...[lot]
// with exactly majorInterval lots and (majorInterval - 1) minor streets between two avenues.
public static class UrbanGrid
{
    public struct Params
    {
        public int blockSize;
        public int minorStreetWidth;
        public int majorStreetWidth;
        public int majorInterval; // lots between main avenues, clamped to >= 1

        public int MinorPeriod => blockSize + minorStreetWidth;
        public int SuperPeriod => Mathf.Max(1, majorInterval) * MinorPeriod - minorStreetWidth + majorStreetWidth;
    }

    public struct StreetSample
    {
        public bool isStreet;
        public bool isMajor;
        public float distToEdge; // 0 = inside the street band, grows outward otherwise
    }

    // Classifies a single world coordinate along one axis: is it inside a street band (and if
    // so, which tier), or inside a lot. Used by Chunk.cs for terrain flattening and texturing.
    public static StreetSample SampleAxis(float coord, Params p)
    {
        int minorPeriod = p.MinorPeriod;
        int superPeriod = p.SuperPeriod;
        int interval = Mathf.Max(1, p.majorInterval);

        int superIndex = Mathf.FloorToInt(coord / superPeriod);
        float s = coord - superIndex * superPeriod; // wraps into [0, superPeriod)

        if (s < p.majorStreetWidth)
            return new StreetSample { isStreet = true, isMajor = true, distToEdge = Mathf.Min(s, p.majorStreetWidth - s) };

        float r = s - p.majorStreetWidth;
        int k = Mathf.Min(interval - 1, Mathf.FloorToInt(r / minorPeriod));
        float withinPeriod = r - k * minorPeriod;

        if (withinPeriod < p.blockSize)
            return new StreetSample { isStreet = false };

        float d = withinPeriod - p.blockSize;
        return new StreetSample { isStreet = true, isMajor = false, distToEdge = Mathf.Min(d, minorPeriod - withinPeriod) };
    }

    // Enumerates every lot center that could overlap [from, from + size) along one axis, paired
    // with a deterministic global lot index for hashing. Mirrors SampleAxis's exact geometry so
    // lots and streets never disagree. Includes a small margin so lots centered just outside the
    // range but with a footprint crossing into it aren't missed.
    public static IEnumerable<(int lotIndex, float center)> LotsInRange(float from, float size, Params p)
    {
        int minorPeriod = p.MinorPeriod;
        int superPeriod = p.SuperPeriod;
        int interval = Mathf.Max(1, p.majorInterval);
        float margin = p.blockSize;

        int superIndex0 = Mathf.FloorToInt((from - margin) / superPeriod);
        int superIndex1 = Mathf.FloorToInt((from + size + margin) / superPeriod);

        for (int superIndex = superIndex0; superIndex <= superIndex1; superIndex++)
        for (int k = 0; k < interval; k++)
        {
            float lotStart = superIndex * superPeriod + p.majorStreetWidth + k * minorPeriod;
            float center = lotStart + p.blockSize * 0.5f;
            if (center < from - margin || center > from + size + margin) continue;
            yield return (superIndex * interval + k, center);
        }
    }
}
