using System.Collections;
using UnityEngine;

/// <summary>
/// Ottimizzatore di rendering asincrono a livello di singolo asset.
/// Attiva o disattiva i MeshRenderer del modello basandosi sulla distanza dal giocatore.
/// </summary>
public class ObjectDistanceCuller : MonoBehaviour
{
    private Transform player;
    private Renderer[] renderers;
    private bool isCurrentlyVisible = true;

    [Header("LOD Distances (Set manually or left at 0 for auto-calculation)")]
    public float highDistance = 0f;
    public float mediumDistance = 0f;
    public float maxDistance = 0f;

    private float maxDistanceSqr;
    private float highDistanceSqr;
    private float medDistanceSqr;

    private string objectName;
    private float highPercent;
    private float medPercent;
    private MeshFilter meshFilter;
    private Mesh highMesh;
    private Mesh mediumMesh;
    private Mesh lowMesh;

    public void Initialize(Transform playerTransform, float maxDistance, string objectName = "", float highPercent = 0.25f, float medPercent = 0.50f, bool useSuperHighForClosest = false)
    {
        player = playerTransform;
        this.objectName = objectName;
        this.highPercent = highPercent;
        this.medPercent = medPercent;

        // Se la maxDistance non è impostata nell'inspector, usa quella passata
        if (this.maxDistance <= 0f)
        {
            this.maxDistance = maxDistance;
        }

        // Se le distanze di soglia non sono impostate, calcolale usando le percentuali
        if (this.highDistance <= 0f)
        {
            this.highDistance = this.maxDistance * highPercent;
        }
        if (this.mediumDistance <= 0f)
        {
            this.mediumDistance = this.maxDistance * medPercent;
        }

        maxDistanceSqr = this.maxDistance * this.maxDistance;
        highDistanceSqr = this.highDistance * this.highDistance;
        medDistanceSqr = this.mediumDistance * this.mediumDistance;

        renderers = GetComponentsInChildren<Renderer>();
        meshFilter = GetComponentInChildren<MeshFilter>();

        // Risolviamo le mesh dalla cache se disponibile (forziamo l'uso di _high invece dell'originale)
        if (!string.IsNullOrEmpty(objectName))
        {
            var wm = FindFirstObjectByType<WorldManager>();
            if (wm != null)
            {
                wm.EnsureMeshCached(objectName);
            }

            if (VoxelRenderer.RuntimeMeshCache.TryGetValue(objectName + "_high", out Mesh hm) && hm != null)
                highMesh = hm;

            if (VoxelRenderer.RuntimeMeshCache.TryGetValue(objectName + "_medium", out Mesh mm) && mm != null)
                mediumMesh = mm;

            if (VoxelRenderer.RuntimeMeshCache.TryGetValue(objectName + "_low", out Mesh lm) && lm != null)
                lowMesh = lm;

            // --- CARICAMENTO DI EMERGENZA DA DISCO PER PREFAB CACHATI ---
#if UNITY_EDITOR
            if (highMesh == null)
            {
                string highPrefabPath = $"Assets/GeneratedAsset/{objectName}_high.prefab";
                var highPrefab = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(highPrefabPath);
                if (highPrefab != null)
                {
                    var mf = highPrefab.GetComponentInChildren<MeshFilter>();
                    if (mf != null) highMesh = mf.sharedMesh;
                }
            }
            if (mediumMesh == null)
            {
                string medPrefabPath = $"Assets/GeneratedAsset/{objectName}_medium.prefab";
                var medPrefab = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(medPrefabPath);
                if (medPrefab != null)
                {
                    var mf = medPrefab.GetComponentInChildren<MeshFilter>();
                    if (mf != null) mediumMesh = mf.sharedMesh;
                }
            }
            if (lowMesh == null)
            {
                string lowPrefabPath = $"Assets/GeneratedAsset/{objectName}_low.prefab";
                var lowPrefab = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(lowPrefabPath);
                if (lowPrefab != null)
                {
                    var mf = lowPrefab.GetComponentInChildren<MeshFilter>();
                    if (mf != null) lowMesh = mf.sharedMesh;
                }
            }
#endif
            // Fallback finale all'originale (meshFilter.sharedMesh) se non troviamo i file LOD _high, _medium, _low
            if (highMesh == null && meshFilter != null) highMesh = meshFilter.sharedMesh;
            if (mediumMesh == null) mediumMesh = highMesh;
            if (lowMesh == null) lowMesh = mediumMesh;

            // Per gli oggetti "big"/"huge" la tier "_high" è comunque limitata a HighGridSize
            // (32 voxel sul lato più lungo, vedi GenerateResolutions), che su un edificio grande
            // risulta troppo grezza anche da vicino. In questo caso usiamo la tier "_superhigh"
            // (un downsampling più fitto, es. 64 voxel — vedi GenerateResolutions.SuperHighGridSize)
            // come livello più dettagliato, al posto di "_high". Non usiamo più la mesh originale
            // non decimata: su un edificio molto grande può arrivare a milioni di voxel/poligoni
            // ed era troppo pesante da renderizzare da vicino.
            if (useSuperHighForClosest)
            {
                Mesh superHigh = null;
                if (VoxelRenderer.RuntimeMeshCache.TryGetValue(objectName + "_superhigh", out Mesh shm) && shm != null)
                    superHigh = shm;
#if UNITY_EDITOR
                if (superHigh == null)
                {
                    string superHighPrefabPath = $"Assets/GeneratedAsset/{objectName}_superhigh.prefab";
                    var superHighPrefab = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(superHighPrefabPath);
                    if (superHighPrefab != null)
                    {
                        var mf = superHighPrefab.GetComponentInChildren<MeshFilter>();
                        if (mf != null) superHigh = mf.sharedMesh;
                    }
                }
#endif
                // Se la tier "_superhigh" non è (ancora) disponibile — es. oggetto generato prima
                // di questa modifica e non ancora rigenerato — restiamo su "_high" invece di
                // ripiegare sulla mesh originale non decimata.
                if (superHigh != null) highMesh = superHigh;
            }
        }
        else if (meshFilter != null)
        {
            highMesh = meshFilter.sharedMesh;
            mediumMesh = meshFilter.sharedMesh;
            lowMesh = meshFilter.sharedMesh;
        }

        // Registrazione immediata nel gestore LOD se siamo già attivi
        if (gameObject.activeInHierarchy)
        {
            RegisterToLODManager();
        }

        // Eseguiamo un primo controllo immediato
        if (player != null)
        {
            UpdateVisibilityAndLOD(player.position);
        }
    }

    void OnEnable()
    {
        RegisterToLODManager();

        // NOTA: niente più UpdateVisibilityAndLOD() sincrono qui. Quando un chunk si riattiva
        // (rientra nel cono visivo — vedi WorldManager.UpdateChunks) OnEnable scatta su OGNI
        // decorazione figlia contemporaneamente: forzare qui un ricalcolo immediato per ciascuna
        // significava fare in un solo frame tutto il lavoro (distanza + eventuale cambio mesh LOD +
        // toggle renderer) che il sliced update di LODManager è pensato apposta per spalmare su più
        // frame. È sicuro saltarlo: renderer.enabled e la mesh LOD restano quelli dell'ultimo
        // UpdateVisibilityAndLOD eseguito prima della disattivazione (SetActive(false) sul chunk non
        // li tocca), quindi lo stato mostrato resta corretto o al più stale di pochi frame finché
        // LODManager.Update() non ripassa da questo oggetto — impercettibile, dato che il player
        // difficilmente si sposta abbastanza in 1-4 frame da rendere quello stato visibilmente
        // sbagliato. La primissima inizializzazione (spawn dell'oggetto) resta invariata: qui
        // player è ancora null in quel momento (Initialize() non è ancora stata chiamata), e
        // Initialize() chiama comunque UpdateVisibilityAndLOD() esplicitamente alla fine.
    }

    void OnDisable()
    {
        UnregisterFromLODManager();
    }

    void OnDestroy()
    {
        UnregisterFromLODManager();
    }

    private void RegisterToLODManager()
    {
        if (LODManager.Instance == null)
        {
            // Crea un LODManager al volo se non esiste in scena. Serve anche gestire il caso in
            // cui il GameObject "LODManager" esiste già ma Instance è comunque null — succede dopo
            // un domain reload a caldo (es. ricompilazione di uno script mentre si è in Play Mode):
            // i campi static vengono azzerati, ma gli oggetti già in scena sopravvivono senza che
            // il loro Awake() (l'unico punto che ripopola Instance) rigiri. Prima qui si assumeva
            // ciecamente che Instance fosse valorizzato dopo questo blocco, con NullReferenceException
            // a valle se il GameObject esisteva già ma orfano di Instance.
            var go = GameObject.Find("LODManager");
            if (go == null)
            {
                go = new GameObject("LODManager");
                go.AddComponent<LODManager>();
            }
            else
            {
                var existing = go.GetComponent<LODManager>();
                if (existing == null)
                    go.AddComponent<LODManager>();
                else
                    // Awake() gira una sola volta per istanza — se il componente esisteva già da
                    // prima del reload, non ripartirà mai da solo. Ripristiniamo Instance a mano,
                    // esattamente quello che avrebbe fatto Awake() la prima volta.
                    LODManager.Instance = existing;
            }

            if (LODManager.Instance == null)
            {
                Debug.LogWarning($"[ObjectDistanceCuller] LODManager.Instance still null after the restore attempt on '{go.name}' — registration of '{name}' skipped for this frame.");
                return;
            }
        }

        LODManager.Instance.Register(this);
    }

    private void UnregisterFromLODManager()
    {
        if (LODManager.Instance != null)
        {
            LODManager.Instance.Unregister(this);
        }
    }

    // Questa funzione viene chiamata periodicamente a frame alternati da LODManager
    public void UpdateVisibilityAndLOD(Vector3 playerPos)
    {
        if (renderers == null)
        {
            renderers = GetComponentsInChildren<Renderer>();
        }
        if (meshFilter == null)
        {
            meshFilter = GetComponentInChildren<MeshFilter>();
        }
        if (renderers == null) return;

        // Calcolo della distanza vettoriale quadrata (ultra-performante)
        float currentDistSqr = (transform.position - playerPos).sqrMagnitude;
        bool shouldBeVisible = currentDistSqr <= maxDistanceSqr;

        if (shouldBeVisible != isCurrentlyVisible)
        {
            isCurrentlyVisible = shouldBeVisible;
            for (int i = 0; i < renderers.Length; i++)
            {
                if (renderers[i] != null)
                {
                    renderers[i].enabled = isCurrentlyVisible;
                }
            }
        }

        // Se l'oggetto è visibile e abbiamo un MeshFilter valido, aggiorniamo il livello di dettaglio (LOD)
        if (isCurrentlyVisible && meshFilter != null)
        {
            Mesh targetMesh = null;
            if (currentDistSqr <= highDistanceSqr)
            {
                targetMesh = highMesh;
            }
            else if (currentDistSqr <= medDistanceSqr)
            {
                targetMesh = mediumMesh;
            }
            else
            {
                targetMesh = lowMesh;
            }

            if (targetMesh != null && meshFilter.sharedMesh != targetMesh)
            {
                // Nota: il MeshCollider (big/huge) NON viene più riallineato qui ai cambi di LOD
                // visivo. È bakato una volta sola allo spawn in WorldManager.ApplySizeCollider,
                // sempre dalla tier "_high" capped — un ribake fisico sincrono ogni volta che il
                // player entra/esce da una soglia di distanza (specialmente verso la mesh originale
                // non decimata usata qui come LOD visivo più vicino per gli huge) era un costo
                // pesante e ricorrente proprio durante il cammino. La leggera imprecisione tra
                // collisione (sempre "_high") e mesh visiva più vicina (originale, per gli huge) è
                // accettabile: il collider non ha bisogno della stessa fedeltà del rendering.
                meshFilter.sharedMesh = targetMesh;
            }
        }
    }
}