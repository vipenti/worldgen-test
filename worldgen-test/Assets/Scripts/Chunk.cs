using UnityEngine;
using System.Collections.Generic;

// La definizione della classe rimane per compatibilità nello stesso file 
// se non hai un file BiomeConfig.cs separato.
[System.Serializable]
public class BiomeConfig
{
    public float perlinScale;
    public int chunkHeight;
    public int chunkWidth;
    public byte surfaceTileID;
    public byte middleTileID;
    public byte deepTileID;
    public byte liquidTileID;
    public int waterLevel;
    public float detailScale;
    public byte flowerTileID;
    public byte bushTileID;
    public byte rockTileID;

    // Noise avanzato
    public int   octaves      = 4;
    public float ridgeFactor  = 0f;
    public float warpStrength = 0.2f;
}

[RequireComponent(typeof(MeshFilter), typeof(MeshRenderer), typeof(MeshCollider))]
public class Chunk : MonoBehaviour
{
    private List<Vector3> vertices = new List<Vector3>();
    private List<int> triangles = new List<int>();
    private List<Vector2> uvs = new List<Vector2>();
    private byte[,,] map;
    private Mesh mesh;

    private int chunkWidth;
    private int chunkHeight;
    private const float texW = 1f / 8f; // 8 colonne nell'atlas
    private const float texH = 1f / 4f; // 4 righe nell'atlas
    private BiomeConfig currentConfig;

    // Adesso Initialize accetta direttamente l'oggetto BiomeConfig passato dal WorldManager
    public void Initialize(Vector2Int pos, BiomeConfig oldBinary, BiomeConfig targetBinary, Vector2 transitionCenter, float innerRadius, float blendDist, float pathHalfWidth, float pathBlendWidth, int pathLevel)
    {
        chunkWidth  = targetBinary.chunkWidth;
        chunkHeight = Mathf.Max(oldBinary.chunkHeight, targetBinary.chunkHeight);

        map = new byte[chunkWidth, 256, chunkWidth];
        mesh = new Mesh();
        GetComponent<MeshFilter>().mesh = mesh;

        GenerateMapDataInterpolated(pos, oldBinary, targetBinary, transitionCenter, innerRadius, blendDist, pathHalfWidth, pathBlendWidth, pathLevel);
        GenerateMesh();
    }

    // fBm + ridged noise + domain warping
    static float SampleNoise(float worldX, float worldZ, BiomeConfig cfg)
    {
        float wx = worldX, wz = worldZ;
        if (cfg.warpStrength > 0f)
        {
            float s = cfg.perlinScale * 0.5f;
            float ox = cfg.warpStrength * 60f * (Mathf.PerlinNoise(worldX * s + 31.7f, worldZ * s + 17.3f) * 2f - 1f);
            float oz = cfg.warpStrength * 60f * (Mathf.PerlinNoise(worldX * s + 83.1f, worldZ * s + 59.4f) * 2f - 1f);
            wx += ox; wz += oz;
        }

        int   octaves   = Mathf.Max(1, cfg.octaves);
        float amplitude = 1f, frequency = 1f, value = 0f, maxValue = 0f;

        for (int o = 0; o < octaves; o++)
        {
            float sample = Mathf.PerlinNoise(wx * cfg.perlinScale * frequency, wz * cfg.perlinScale * frequency);

            if (cfg.ridgeFactor > 0f)
            {
                float ridge = 1f - Mathf.Abs(sample * 2f - 1f);
                ridge *= ridge;
                sample = Mathf.Lerp(sample, ridge, cfg.ridgeFactor);
            }

            value    += sample * amplitude;
            maxValue += amplitude;
            amplitude *= 0.5f;
            frequency *= 2f;
        }

        return maxValue > 0f ? value / maxValue : 0f;
    }

    void GenerateMapDataInterpolated(Vector2Int pos, BiomeConfig old, BiomeConfig target, Vector2 transitionCenter, float innerRadius, float blendDist, float pathHalfWidth, float pathBlendWidth, int pathLevel)
    {
        for (int x = 0; x < chunkWidth; x++)
        {
            for (int z = 0; z < chunkWidth; z++)
            {
                float worldX = pos.x * chunkWidth + x;
                float worldZ = pos.y * chunkWidth + z;

                float colX        = pos.x + (float)x / chunkWidth;
                float colZ        = pos.y + (float)z / chunkWidth;
                float dist        = Vector2.Distance(new Vector2(colX, colZ), transitionCenter);
                float adjustedDist = Mathf.Max(0f, dist - innerRadius);
                float t           = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(adjustedDist / blendDist));

                float noiseOld    = SampleNoise(worldX, worldZ, old);
                float noiseTarget = SampleNoise(worldX, worldZ, target);
                int finalHeight   = Mathf.FloorToInt(Mathf.Lerp(noiseOld * old.chunkHeight, noiseTarget * target.chunkHeight, t));

                // Appiattimento del path lungo Z (centro a worldX=0)
                float distFromPath = Mathf.Abs(worldX);
                if (distFromPath < pathHalfWidth + pathBlendWidth)
                {
                    float blendT       = Mathf.Clamp01((distFromPath - pathHalfWidth) / pathBlendWidth);
                    float pathInfluence = 1f - Mathf.SmoothStep(0f, 1f, blendT);
                    finalHeight = Mathf.RoundToInt(Mathf.Lerp(finalHeight, pathLevel, pathInfluence));
                }

                float dither = Mathf.PerlinNoise(worldX * 0.25f + 7777f, worldZ * 0.25f + 7777f);
                bool useNew  = dither < t;
                byte surface = useNew ? target.surfaceTileID : old.surfaceTileID;
                byte middle  = useNew ? target.middleTileID  : old.middleTileID;
                byte deep    = useNew ? target.deepTileID    : old.deepTileID;
                byte liquid  = useNew ? target.liquidTileID  : old.liquidTileID;
                int waterLevel = Mathf.RoundToInt(Mathf.Lerp(old.waterLevel, target.waterLevel, t));

                for (int y = 0; y < 256; y++)
                {
                    if (y > finalHeight)
                    {
                        map[x, y, z] = (y <= waterLevel) ? liquid : (byte)0;
                    }
                    else if (y == finalHeight) map[x, y, z] = surface;
                    else if (y > finalHeight - 3) map[x, y, z] = middle;
                    else map[x, y, z] = deep;
                }
            }
        }
    }

    void GenerateMapData(Vector2Int pos, float scale)
    {
        float detailOffset = 5000f; 

        for (int x = 0; x < chunkWidth; x++)
        {
            for (int z = 0; z < chunkWidth; z++)
            {
                // Mappa 1: Altezza Terreno (usa lo scale passato)
                float worldX = (pos.x * chunkWidth + x) * scale;
                float worldZ = (pos.y * chunkWidth + z) * scale;
                float noiseValue = Mathf.PerlinNoise(worldX, worldZ);
                int terrainHeight = Mathf.FloorToInt(noiseValue * chunkHeight);

                // Mappa 2: Dettagli
                float detX = (pos.x * chunkWidth + x) * currentConfig.detailScale + detailOffset;
                float detZ = (pos.y * chunkWidth + z) * currentConfig.detailScale + detailOffset;
                float detailValue = Mathf.PerlinNoise(detX, detZ);

                for (int y = 0; y < chunkHeight; y++)
                {
                    if (y > terrainHeight)
                    {
                        // Logica Liquidi e Dettagli superficiali
                        if (y == terrainHeight + 1 && y > currentConfig.waterLevel)
                        {
                            if (detailValue > 0.85f)
                                map[x, y, z] = currentConfig.bushTileID;
                            else if (detailValue > 0.70f)
                                map[x, y, z] = currentConfig.flowerTileID;
                            else if (detailValue < 0.15f)
                                map[x, y, z] = currentConfig.rockTileID;
                            else
                                map[x, y, z] = 0;
                        }
                        else if (y <= currentConfig.waterLevel)
                        {
                            map[x, y, z] = currentConfig.liquidTileID;
                        }
                        else
                        {
                            map[x, y, z] = 0;
                        }
                    }
                    else if (y == terrainHeight)
                    {
                        map[x, y, z] = currentConfig.surfaceTileID;
                    }
                    else if (y > terrainHeight - 3)
                    {
                        map[x, y, z] = currentConfig.middleTileID;
                    }
                    else
                    {
                        map[x, y, z] = currentConfig.deepTileID;
                    }
                }
            }
        }
    }

    // --- LOGICA DI RENDERING ---
    void GenerateMesh()
    {
        vertices.Clear(); triangles.Clear(); uvs.Clear();

        for (int x = 0; x < chunkWidth; x++)
        {
            for (int y = 0; y < chunkHeight; y++)
            {
                for (int z = 0; z < chunkWidth; z++)
                {
                    if (map[x, y, z] == 0) continue;

                    byte blockID = map[x, y, z];
                    Vector3 blockPos = new Vector3(x, y, z);

                    if (IsTransparent(x, y + 1, z)) CreateFace(blockPos, Vector3.up, blockID);
                    if (IsTransparent(x, y - 1, z)) CreateFace(blockPos, Vector3.down, blockID);
                    if (IsTransparent(x, y, z + 1)) CreateFace(blockPos, Vector3.forward, blockID);
                    if (IsTransparent(x, y, z - 1)) CreateFace(blockPos, Vector3.back, blockID);
                    if (IsTransparent(x + 1, y, z)) CreateFace(blockPos, Vector3.right, blockID);
                    if (IsTransparent(x - 1, y, z)) CreateFace(blockPos, Vector3.left, blockID);
                }
            }
        }
        UpdateMesh();
    }

    bool IsTransparent(int x, int y, int z)
    {
        if (y < 0 || y >= chunkHeight) return true;
        if (x < 0 || x >= chunkWidth || z < 0 || z >= chunkWidth) return true;
        return map[x, y, z] == 0;
    }

    void CreateFace(Vector3 pos, Vector3 direction, byte blockID)
    {
        int vCount = vertices.Count;

        if (direction == Vector3.up) {
            vertices.Add(pos + new Vector3(0, 1, 1)); vertices.Add(pos + new Vector3(1, 1, 1));
            vertices.Add(pos + new Vector3(1, 1, 0)); vertices.Add(pos + new Vector3(0, 1, 0));
        }
        else if (direction == Vector3.down) {
            vertices.Add(pos + new Vector3(0, 0, 0)); vertices.Add(pos + new Vector3(1, 0, 0));
            vertices.Add(pos + new Vector3(1, 0, 1)); vertices.Add(pos + new Vector3(0, 0, 1));
        }
        else if (direction == Vector3.forward) {
            vertices.Add(pos + new Vector3(1, 0, 1)); vertices.Add(pos + new Vector3(1, 1, 1));
            vertices.Add(pos + new Vector3(0, 1, 1)); vertices.Add(pos + new Vector3(0, 0, 1));
        }
        else if (direction == Vector3.back) {
            vertices.Add(pos + new Vector3(0, 0, 0)); vertices.Add(pos + new Vector3(0, 1, 0));
            vertices.Add(pos + new Vector3(1, 1, 0)); vertices.Add(pos + new Vector3(1, 0, 0));
        }
        else if (direction == Vector3.right) {
            vertices.Add(pos + new Vector3(1, 0, 0)); vertices.Add(pos + new Vector3(1, 1, 0));
            vertices.Add(pos + new Vector3(1, 1, 1)); vertices.Add(pos + new Vector3(1, 0, 1));
        }
        else if (direction == Vector3.left) {
            vertices.Add(pos + new Vector3(0, 0, 1)); vertices.Add(pos + new Vector3(0, 1, 1));
            vertices.Add(pos + new Vector3(0, 1, 0)); vertices.Add(pos + new Vector3(0, 0, 0));
        }

        int textureIndex = blockID - 1;
        float xOffset = (textureIndex % 8) * texW;
        float yOffset = (3 - (textureIndex / 8)) * texH;

        uvs.Add(new Vector2(xOffset,        yOffset));
        uvs.Add(new Vector2(xOffset,        yOffset + texH));
        uvs.Add(new Vector2(xOffset + texW, yOffset + texH));
        uvs.Add(new Vector2(xOffset + texW, yOffset));

        triangles.Add(vCount); triangles.Add(vCount + 1); triangles.Add(vCount + 2);
        triangles.Add(vCount); triangles.Add(vCount + 2); triangles.Add(vCount + 3);
    }

    void UpdateMesh()
    {
        mesh.Clear();
        mesh.vertices = vertices.ToArray();
        mesh.uv = uvs.ToArray();
        mesh.triangles = triangles.ToArray();
        mesh.RecalculateNormals();
        GetComponent<MeshCollider>().sharedMesh = mesh;
    }
}