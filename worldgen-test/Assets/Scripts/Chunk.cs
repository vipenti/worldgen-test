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

    // Layer 2 – ridge overlay (legacy). ridgeLayerAmplitude=0 disables it.
    public float ridgeLayerScale     = 0f;
    public float ridgeLayerAmplitude = 0f;

    // Layer 3 – billow overlay (legacy). billowLayerAmplitude=0 disables it.
    public float billowLayerScale     = 0f;
    public float billowLayerAmplitude = 0f;

    // Layout map (legacy scatter)
    public float objectDensityThreshold = 0.65f;
    public float detailScaleForLayout   = 0.15f;

    // === Python-schema noise system (active when scaleMacro > 0) ===
    // 1. Continental / Macro noise
    public float scaleMacro        = 0f;   // size of macro regions [30-100]
    public float macroInfluence    = 1.0f; // how strongly macro mixes billow vs ridged [0-1]
    // 2. Billow – rounded hills / dunes
    public float scaleBillow       = 25f;  // horizontal scale [10-60]
    public float ampBillow         = 20f;  // height contribution [0-50]
    public float billowExponent    = 1.0f; // shape: <1 flat tops, >1 steeper [0.5-2]
    // 3. Ridged – sharp peaks / canyons
    public float scaleRidged       = 15f;  // horizontal scale [5-30]
    public float ampRidged         = 40f;  // height contribution [0-80]
    public float ridgedExponent    = 2.0f; // sharpness: higher = knife-edge [1-3]
    // 4. Domain warping  (warpStrength already exists; warpScale is new)
    public float warpScale         = 20f;  // scale of warp noise [10-40]
    // 5. Base & layout
    public float baseTerrainHeight = 15f;  // Y floor before noise layers [5-25]
    public float scaleLayout       = 30f;  // spawn-zone size [10-60]
    public float slopeMaxLimit     = 1.2f; // max slope for object placement [0.5-3]
    // 6. Layout archetype
    // True for human-made settlements (city, town, village, urban district) that need a
    // structured, grid/street-based layout instead of the organic noise-scatter placement
    // used for natural biomes. Placement logic doesn't branch on this yet — it's the first
    // step toward a dedicated urban layout system.
    public bool isStructuredBiome = false;
    public string palette_color_description;
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

    public float[,] LayoutMap { get; private set; }
    public int[,]   HeightMap { get; private set; }

    // Fascia [min,max] di Y realmente riempita per ogni colonna (x,z), calcolata già in
    // GenerateMapDataInterpolated (Pass 2) — GenerateMesh la riusa per scandire solo quella fascia
    // invece dell'intera colonna 0..chunkHeight, che con chunkHeight fino a 150 (vedi
    // Prompts.ParametricSystem) significava iterare aria vuota per la stragrande maggioranza dei
    // livelli Y di ogni colonna.
    private int[,] _columnMinY;
    private int[,] _columnMaxY;

    // Saved properties for dynamic interpolation updating
    private Vector2Int chunkPos;
    private BiomeConfig oldConfig;
    private BiomeConfig targetConfig;
    private float pathHalfWidth;
    private float pathBlendWidth;
    private int pathLevel;
    // Urban street grid (isStructuredBiome), mirrored from WorldManager so terrain flattening
    // and texturing agree with where WorldManager.SpawnStructuredLayoutObjects puts the lots.
    private UrbanGrid.Params urbanGrid;

    private bool isTransitioning = false;
    // Blend radiale: centro (mondo, XZ) della transizione + raggio interno (già coperto dalla vista,
    // nessun blend) + larghezza della fascia oltre cui il blend arriva a 1. Sostituisce il vecchio
    // modello direzionale (start pos + direzione + lunghezza corridoio), che dipendeva dal player
    // che continuasse a camminare dritto nella direzione catturata al momento del trigger.
    private Vector2 transitionCenter2D;
    private float innerRadius;
    private float blendWidth;
    private bool hasGeneratedOnce = false;
    private bool hasColliderBuilt = false;

    // Coordinata chunk, esposta per il logging diagnostico in WorldManager (piazzamento/
    // reperimento decorazioni).
    public Vector2Int ChunkCoord => chunkPos;

    // Numero di decorazioni ancora in attesa che la loro generazione IA finisca (billboard
    // placeholder invisibili piazzati da WorldManager.InstantiateAssetPrimitive). Finché > 0,
    // WorldManager.RemoveFarChunks evita di distruggere questo chunk: il placeholder in attesa
    // è figlio del chunk, quindi morirebbe con lui prima che l'asset sia pronto, perdendo per
    // sempre quella decorazione anche se la generazione poi va a buon fine (vedi
    // ReplacePlaceholders — non c'è più nessun placeholder a cui agganciare la mesh risolta).
    public int PendingDecorCount { get; private set; }
    public void NotePendingDecorAdded() => PendingDecorCount++;
    public void NotePendingDecorResolved() => PendingDecorCount = Mathf.Max(0, PendingDecorCount - 1);

    // Adesso Initialize accetta direttamente l'oggetto BiomeConfig passato dal WorldManager
    public void Initialize(Vector2Int pos, BiomeConfig oldBinary, BiomeConfig targetBinary,
                           bool isTransitioning, Vector2 transitionCenter2D, float innerRadius, float blendWidth,
                           float pathHalfWidth, float pathBlendWidth, int pathLevel,
                           UrbanGrid.Params urbanGrid, bool updateCollider)
    {
        chunkWidth  = targetBinary.chunkWidth;
        chunkHeight = Mathf.Max(oldBinary.chunkHeight, targetBinary.chunkHeight);

        // Dimensionato sulla vera chunkHeight del bioma (prima era fisso a 256 indipendentemente dal
        // valore configurato, es. 55-64 nei biomi tipici) — meno memoria e un Array.Clear più corto
        // ad ogni rigenerazione (vedi GenerateMapDataInterpolated). Un'istanza di Chunk non viene mai
        // riutilizzata per un'altra coordinata con chunkHeight diverso (WorldManager ne crea sempre
        // una nuova via Instantiate), quindi non serve gestire un ridimensionamento successivo.
        map       = new byte[chunkWidth, chunkHeight, chunkWidth];
        HeightMap = new int[chunkWidth, chunkWidth];
        LayoutMap = new float[chunkWidth, chunkWidth];
        _columnMinY = new int[chunkWidth, chunkWidth];
        _columnMaxY = new int[chunkWidth, chunkWidth];
        
        if (mesh == null)
        {
            mesh = new Mesh();
            GetComponent<MeshFilter>().mesh = mesh;
        }

        // Save parameters for runtime interpolation updating
        this.chunkPos = pos;
        this.oldConfig = oldBinary;
        this.targetConfig = targetBinary;
        this.pathHalfWidth = pathHalfWidth;
        this.pathBlendWidth = pathBlendWidth;
        this.pathLevel = pathLevel;
        this.urbanGrid = urbanGrid;

        this.isTransitioning = isTransitioning;
        this.transitionCenter2D = transitionCenter2D;
        this.innerRadius = innerRadius;
        this.blendWidth = blendWidth;
        hasGeneratedOnce = true;

        GenerateMapDataInterpolated(pos, oldBinary, targetBinary, pathHalfWidth, pathBlendWidth, pathLevel);
        ComputeLayoutMap(pos, targetBinary);
        GenerateMesh(updateCollider);
    }

    public void UpdateInterpolation(bool isTransitioning, Vector2 transitionCenter2D, float innerRadius, float blendWidth, bool updateCollider)
    {
        if (hasGeneratedOnce &&
            this.isTransitioning == isTransitioning &&
            this.transitionCenter2D == transitionCenter2D &&
            Mathf.Abs(this.innerRadius - innerRadius) < 0.01f &&
            Mathf.Abs(this.blendWidth - blendWidth) < 0.01f)
        {
            EnsureColliderState(updateCollider);
            return;
        }

        this.isTransitioning = isTransitioning;
        this.transitionCenter2D = transitionCenter2D;
        this.innerRadius = innerRadius;
        this.blendWidth = blendWidth;
        hasGeneratedOnce = true;

        GenerateMapDataInterpolated(chunkPos, oldConfig, targetConfig, pathHalfWidth, pathBlendWidth, pathLevel);
        GenerateMesh(updateCollider);
    }

    public void EnsureColliderState(bool closeToPlayer)
    {
        var collider = GetComponent<MeshCollider>();
        if (closeToPlayer)
        {
            if (!hasColliderBuilt || collider.sharedMesh != mesh)
            {
                collider.sharedMesh = mesh;
                collider.enabled = true;
                hasColliderBuilt = true;
            }
        }
        else
        {
            if (collider.enabled)
            {
                collider.enabled = false;
                hasColliderBuilt = false;
            }
        }
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

    public static float SampleNoisePublic(float worldX, float worldZ, BiomeConfig cfg) => SampleNoise(worldX, worldZ, cfg);

    public static float SampleRidgedNoise(float worldX, float worldZ, float scale, int octaves)
    {
        float value = 0f, amplitude = 1f, frequency = 1f, maxValue = 0f;
        for (int o = 0; o < Mathf.Max(1, octaves); o++)
        {
            float s     = Mathf.PerlinNoise(worldX * scale * frequency, worldZ * scale * frequency);
            float ridge = 1f - Mathf.Abs(s * 2f - 1f);
            ridge *= ridge;
            value    += ridge * amplitude;
            maxValue += amplitude;
            amplitude *= 0.5f; frequency *= 2f;
        }
        return maxValue > 0f ? value / maxValue : 0f;
    }

    public static float SampleBillowNoise(float worldX, float worldZ, float scale, int octaves)
    {
        float value = 0f, amplitude = 1f, frequency = 1f, maxValue = 0f;
        for (int o = 0; o < Mathf.Max(1, octaves); o++)
        {
            float s      = Mathf.PerlinNoise(worldX * scale * frequency, worldZ * scale * frequency);
            float billow = Mathf.Abs(s * 2f - 1f);
            value    += billow * amplitude;
            maxValue += amplitude;
            amplitude *= 0.5f; frequency *= 2f;
        }
        return maxValue > 0f ? value / maxValue : 0f;
    }

    // Sistema di calcolo dell'altezza stratificato e modulato (Minecraft-Style)
    public static float ComputeHeightNew(float wx, float wz, BiomeConfig cfg)
    {
        // 1. Domain warping (Mantiene le coste e le forme organiche ed erose)
        float warpedX = wx, warpedZ = wz;
        if (cfg.warpStrength > 0f && cfg.warpScale > 0f)
        {
            float s = 1f / cfg.warpScale;
            warpedX += (Mathf.PerlinNoise(wx * s + 501f, wz * s + 1f)   * 2f - 1f) * cfg.warpStrength;
            warpedZ += (Mathf.PerlinNoise(wx * s + 2f,   wz * s + 502f) * 2f - 1f) * cfg.warpStrength;
        }

        // 2. Macro Noise (Regista continentale: definisce dove nascono le regioni)
        float macro = cfg.scaleMacro > 0f
            ? Mathf.PerlinNoise(wx / cfg.scaleMacro + 101f, wz / cfg.scaleMacro + 4917.3f)
            : 0.5f;

        // 3. Billow Noise (Genera colline morbide, dune o avvallamenti calpestabili)
        float billow = 0f;
        if (cfg.ampBillow > 0f && cfg.scaleBillow > 0f)
        {
            float raw = Mathf.PerlinNoise(warpedX / cfg.scaleBillow + 202f, warpedZ / cfg.scaleBillow + 7631.1f);
            billow = Mathf.Pow(Mathf.Abs(raw * 2f - 1f), cfg.billowExponent) * cfg.ampBillow;
        }

        // 4. Ridged Noise (Genera montagne aguzze e pareti di canyon)
        float ridged = 0f;
        if (cfg.ampRidged > 0f && cfg.scaleRidged > 0f)
        {
            float raw = Mathf.PerlinNoise(warpedX / cfg.scaleRidged + 303f, warpedZ / cfg.scaleRidged + 2813.9f);
            ridged = Mathf.Pow(1f - Mathf.Abs(raw * 2f - 1f), cfg.ridgedExponent) * cfg.ampRidged;
        }

        // 5. MISCELAZIONE AVANZATA (ZONING):
        // Calcoliamo un fattore non lineare: se macro < 0.4 le montagne sono VIETATE (valore 0).
        // Se macro sale verso 0.8, le montagne iniziano a svettare in modo maestoso.
        float macroMountainFactor = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.42f, 0.78f, macro));
        
        // Applichiamo la fusione: le pianure useranno solo il Billow, le vette useranno il Ridged
        float height = cfg.baseTerrainHeight
                     + billow * (1f - macroMountainFactor * cfg.macroInfluence)
                     + ridged * macroMountainFactor * cfg.macroInfluence;

        // 6. MASCHERA DEI LAGHI DINAMICA (Previene l'effetto groviera costante):
        // Scava i bacini acquatici dolcemente solo nelle depressioni più profonde del macro (sotto lo 0.18)
        // e solo se il bioma prevede effettivamente una quota d'acqua utilizzabile.
        if (macro < 0.18f && cfg.waterLevel > 0)
        {
            float lakeT = Mathf.SmoothStep(0f, 1f, macro / 0.18f); // 0 al centro del lago, 1 a riva
            float targetLakeFloor = cfg.waterLevel - 3f;
            height = Mathf.Lerp(targetLakeFloor, height, lakeT);
        }

        return height;
    }

    void ComputeLayoutMap(Vector2Int pos, BiomeConfig cfg)
    {
        int   w          = chunkWidth;
        float slopeLimit = cfg.slopeMaxLimit > 0f ? cfg.slopeMaxLimit : 4f;
        float scaleL     = cfg.scaleLayout   > 0f ? cfg.scaleLayout   : 30f;

        for (int x = 0; x < w; x++)
        for (int z = 0; z < w; z++)
        {
            int h = HeightMap[x, z];
            if (h <= cfg.waterLevel) { LayoutMap[x, z] = 0f; continue; }

            // Central-difference gradient (matches Python's np.gradient)
            int hn = (z + 1 < w) ? HeightMap[x, z + 1] : h;
            int hs = (z - 1 >= 0) ? HeightMap[x, z - 1] : h;
            int he = (x + 1 < w) ? HeightMap[x + 1, z] : h;
            int hw = (x - 1 >= 0) ? HeightMap[x - 1, z] : h;
            float gx    = (he - hw) * 0.5f;
            float gz    = (hn - hs) * 0.5f;
            float slope = Mathf.Sqrt(gx * gx + gz * gz);

            if (slope > slopeLimit) { LayoutMap[x, z] = 0f; continue; }

            float worldX = pos.x * w + x;
            float worldZ = pos.y * w + z;
            LayoutMap[x, z] = Mathf.PerlinNoise(worldX / scaleL + 404f, worldZ / scaleL + 5591.7f);
        }
    }

    void GenerateMapDataInterpolated(Vector2Int pos, BiomeConfig old, BiomeConfig target, float pathHalfWidth, float pathBlendWidth, int pathLevel)
    {
        // MODIFICA PRESTAZIONALE: Pulisce nativamente l'array pre-allocato invece di ri-inizializzarlo
        System.Array.Clear(map, 0, map.Length);

        // Pass 1: Calcola l'altezza per ciascuna colonna (x, z)
        for (int x = 0; x < chunkWidth; x++)
        {
            for (int z = 0; z < chunkWidth; z++)
            {
                float worldX = pos.x * chunkWidth + x;
                float worldZ = pos.y * chunkWidth + z;

                float voxelT = 1f;
                if (isTransitioning)
                {
                    // Blend radiale: quanto questo voxel è "dentro" il nuovo bioma dipende dalla
                    // sua distanza dal centro della transizione, non da una direzione catturata al
                    // momento del trigger.
                    float dx = worldX - transitionCenter2D.x;
                    float dz = worldZ - transitionCenter2D.y;
                    float distFromCenter = Mathf.Sqrt(dx * dx + dz * dz);
                    voxelT = Mathf.Clamp01((distFromCenter - innerRadius) / blendWidth);
                }

                // rawTarget serve sempre; rawOld serve SOLO quando isTransitioning è true — fuori
                // transizione voxelT resta 1f per l'intera colonna (vedi sopra), quindi
                // Mathf.Lerp(rawOld, rawTarget, 1f) restituisce esattamente rawTarget a prescindere
                // da rawOld. Calcolare comunque rawOld (2°-3° chiamata a ComputeHeightNew/SampleNoise
                // per colonna, ciascuna con più campionamenti Perlin) era quindi lavoro sprecato per
                // la stragrande maggioranza dei chunk, che non sono mai in transizione.
                float rawTarget;
                if (target.scaleMacro > 0f)
                {
                    rawTarget = ComputeHeightNew(worldX, worldZ, target);
                }
                else
                {
                    rawTarget = SampleNoise(worldX, worldZ, target) * target.chunkHeight;
                    if (target.ridgeLayerAmplitude > 0f)
                        rawTarget += SampleRidgedNoise(worldX, worldZ, target.ridgeLayerScale, target.octaves)
                                     * target.ridgeLayerAmplitude * target.chunkHeight;
                    if (target.billowLayerAmplitude > 0f)
                        rawTarget += SampleBillowNoise(worldX, worldZ, target.billowLayerScale, target.octaves)
                                     * target.billowLayerAmplitude * target.chunkHeight;
                }

                float rawOld = 0f;
                if (isTransitioning)
                {
                    rawOld = target.scaleMacro > 0f
                        ? (old.scaleMacro > 0f ? ComputeHeightNew(worldX, worldZ, old) : SampleNoise(worldX, worldZ, old) * old.chunkHeight)
                        : SampleNoise(worldX, worldZ, old) * old.chunkHeight;
                }

                int finalHeight = Mathf.FloorToInt(Mathf.Lerp(rawOld, rawTarget, voxelT));

                // Appiattimento del path lungo Z (centro a worldX=0)
                float distFromPath = Mathf.Abs(worldX);
                if (distFromPath < pathHalfWidth + pathBlendWidth)
                {
                    float blendT       = Mathf.Clamp01((distFromPath - pathHalfWidth) / pathBlendWidth);
                    float pathInfluence = 1f - Mathf.SmoothStep(0f, 1f, blendT);
                    finalHeight = Mathf.RoundToInt(Mathf.Lerp(finalHeight, pathLevel, pathInfluence));
                }

                // NOTA: qui prima c'era un appiattimento fisico dell'altezza sotto la griglia
                // stradale urbana (isStructuredBiome) — tirava finalHeight verso un livello piatto
                // (target.baseTerrainHeight) in una banda attorno a ogni strada. Il blend tra
                // terreno naturale (rumoroso) e banda piatta creava dislivelli/buche visibili
                // proprio al bordo strada-lotto, specie su terreno collinare. La griglia (UrbanGrid)
                // resta usata da WorldManager per piazzare lotti/edifici e qui sotto per la
                // texturizzazione della strada (vedi "Rivestimento delle strade urbane" in
                // Pass 2) — è diventata puramente concettuale per l'altezza: le strade seguono il
                // terreno naturale, si riconoscono solo dalla texture uniforme, non da un piano
                // scavato nel terreno.

                HeightMap[x, z] = finalHeight;
            }
        }

        // Pass 2: Popola la voxel map usando le altezze calcolate e calcolando la pendenza per il texturing patchato
        for (int x = 0; x < chunkWidth; x++)
        {
            for (int z = 0; z < chunkWidth; z++)
            {
                float worldX = pos.x * chunkWidth + x;
                float worldZ = pos.y * chunkWidth + z;

                float voxelT = 1f;
                if (isTransitioning)
                {
                    // Blend radiale: quanto questo voxel è "dentro" il nuovo bioma dipende dalla
                    // sua distanza dal centro della transizione, non da una direzione catturata al
                    // momento del trigger.
                    float dx = worldX - transitionCenter2D.x;
                    float dz = worldZ - transitionCenter2D.y;
                    float distFromCenter = Mathf.Sqrt(dx * dx + dz * dz);
                    voxelT = Mathf.Clamp01((distFromCenter - innerRadius) / blendWidth);
                }

                int finalHeight = HeightMap[x, z];

                // Fuori transizione voxelT è sempre 1f (vedi sopra), e Mathf.PerlinNoise restituisce
                // sempre un valore < 1 — quindi "dither < voxelT" sarebbe comunque sempre vero: il
                // sample Perlin è superfluo e lo saltiamo, evitando ~una chiamata per colonna per la
                // stragrande maggioranza dei chunk (non in transizione).
                bool useNew;
                if (isTransitioning)
                {
                    float dither = Mathf.PerlinNoise(worldX * 0.25f + 7777f, worldZ * 0.25f + 7777f);
                    useNew = dither < voxelT;
                }
                else
                {
                    useNew = true;
                }

                byte surface = useNew ? target.surfaceTileID : old.surfaceTileID;
                byte middle  = useNew ? target.middleTileID  : old.middleTileID;
                byte liquid  = useNew ? target.liquidTileID  : old.liquidTileID;
                byte flower  = useNew ? target.flowerTileID  : old.flowerTileID;
                byte bush    = useNew ? target.bushTileID    : old.bushTileID;
                byte rock    = useNew ? (target.rockTileID != 0 ? target.rockTileID : target.deepTileID) 
                                      : (old.rockTileID != 0 ? old.rockTileID : old.deepTileID);

                int waterLevel = Mathf.RoundToInt(Mathf.Lerp(old.waterLevel, target.waterLevel, voxelT));

                // Calcolo della pendenza (gradient) locale
                int hn = (z + 1 < chunkWidth) ? HeightMap[x, z + 1] : finalHeight;
                int hs = (z - 1 >= 0) ? HeightMap[x, z - 1] : finalHeight;
                int he = (x + 1 < chunkWidth) ? HeightMap[x + 1, z] : finalHeight;
                int hw = (x - 1 >= 0) ? HeightMap[x - 1, z] : finalHeight;
                float gx = (he - hw) * 0.5f;
                float gz = (hn - hs) * 0.5f;
                float slope = Mathf.Sqrt(gx * gx + gz * gz);

                // Sample patch noise (medio-bassa frequenza per patch estese)
                float patchNoise = Mathf.PerlinNoise(worldX * 0.08f + 123.4f, worldZ * 0.08f + 567.8f);

                byte surfaceTile = surface;

                if (slope > 1.2f)
                {
                    // Pendenza alta (pareti, dirupi): usa pietra o terra
                    surfaceTile = (rock != 0) ? rock : middle;
                }
                else
                {
                    // Pianura/Pendenza dolce: crea variazioni organiche
                    if (patchNoise < 0.20f)
                    {
                        // Patch di terreno scoperto (terra marrone)
                        surfaceTile = middle;
                    }
                    else if (patchNoise > 0.80f)
                    {
                        // Patch fiorita o muschio
                        surfaceTile = (flower != 0) ? flower : surface;
                    }
                    else if (patchNoise > 0.70f && patchNoise <= 0.80f)
                    {
                        // Patch vegetazione/cespugliosa alternativa
                        surfaceTile = (bush != 0) ? bush : surface;
                    }
                }

                // Rivestimento delle strade urbane (isStructuredBiome): forza il tile "rock"
                // (pietra/pavimentazione — con fallback a deepTileID, stesso pattern usato sopra
                // per la variabile "rock"), non "surface". "surface" era la stessa identica tile
                // già usata di default per TUTTO il terreno non patchato (vedi "byte surfaceTile =
                // surface;" sopra) — quindi la strada risultava visivamente indistinguibile dal
                // resto del terreno, si toglievano solo le patch fiori/cespugli senza introdurre
                // un materiale davvero diverso. "middle" resta scartato: è il materiale di
                // sotto-superficie del bioma (spesso terra/erba), non pensato per essere visto in
                // superficie.
                // Gating su voxelT >= 0.5: stessa soglia usata da WorldManager per decidere se un
                // chunk è "abbastanza nuovo" da ricevere decorazioni del bioma nuovo — sotto quella
                // soglia il punto è ancora considerato vecchio bioma, niente pavimentazione.
                if (target.isStructuredBiome && voxelT >= 0.5f)
                {
                    bool onStreet = UrbanGrid.SampleAxis(worldX, urbanGrid).isStreet
                                 || UrbanGrid.SampleAxis(worldZ, urbanGrid).isStreet;
                    if (onStreet) surfaceTile = rock;
                }

                int minHeight = Mathf.Max(0, finalHeight - 3);
                int maxHeight = Mathf.Max(finalHeight, waterLevel);
                _columnMinY[x, z] = minHeight;
                _columnMaxY[x, z] = maxHeight;
                for (int y = minHeight; y <= maxHeight; y++)
                {
                    if (y > finalHeight)
                    {
                        map[x, y, z] = liquid;
                    }
                    else if (y == finalHeight)
                    {
                        map[x, y, z] = surfaceTile;
                    }
                    else
                    {
                        map[x, y, z] = middle;
                    }
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
    void GenerateMesh(bool updateCollider)
    {
        vertices.Clear(); triangles.Clear(); uvs.Clear();

        // Scandisce solo la fascia [_columnMinY, _columnMaxY] già nota per ogni colonna (calcolata
        // in GenerateMapDataInterpolated, Pass 2) invece di 0..chunkHeight per intero — con
        // chunkHeight fino a 150 e una fascia riempita tipicamente di 4-10 blocchi, questo taglia le
        // iterazioni della stragrande maggioranza dell'aria vuota sopra/sotto il terreno. L'ordine
        // x,z,y (invece di x,y,z) non cambia nulla: CreateFace non dipende dall'ordine di visita.
        for (int x = 0; x < chunkWidth; x++)
        {
            for (int z = 0; z < chunkWidth; z++)
            {
                int yMin = Mathf.Max(0, _columnMinY[x, z]);
                int yMax = Mathf.Min(_columnMaxY[x, z], chunkHeight - 1);

                for (int y = yMin; y <= yMax; y++)
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
        UpdateMesh(updateCollider);
    }

    bool IsTransparent(int x, int y, int z)
    {
        if (y < 0 || y >= chunkHeight) return true;
        if (x < 0 || x >= chunkWidth || z < 0 || z >= chunkWidth) return true;
        return map[x, y, z] == 0;
    }

    // Usato dal sistema di passi (FootstepAudio): restituisce il tile ID del blocco calpestabile
    // (la superficie, non l'aria sopra) sotto una qualunque posizione mondo che ricada dentro
    // questo chunk. HeightMap[x,z] è già la colonna dell'altezza di superficie generata in
    // GenerateMapDataInterpolated, quindi non serve un raycast fisico.
    public byte GetSurfaceTileIdAtWorld(float worldX, float worldZ)
    {
        int lx = Mathf.Clamp(Mathf.FloorToInt(worldX - transform.position.x), 0, chunkWidth - 1);
        int lz = Mathf.Clamp(Mathf.FloorToInt(worldZ - transform.position.z), 0, chunkWidth - 1);
        int y  = Mathf.Clamp(HeightMap[lx, lz], 0, chunkHeight - 1);
        return map[lx, y, lz];
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

    void UpdateMesh(bool updateCollider)
    {
        mesh.Clear();
        mesh.vertices = vertices.ToArray();
        mesh.uv = uvs.ToArray();
        mesh.triangles = triangles.ToArray();
        mesh.RecalculateNormals();
        EnsureColliderState(updateCollider);
    }
}