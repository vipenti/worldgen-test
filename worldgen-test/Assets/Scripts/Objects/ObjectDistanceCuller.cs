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

    public void Initialize(Transform playerTransform, float maxDistance, string objectName = "", float highPercent = 0.25f, float medPercent = 0.50f)
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
        if (player != null)
        {
            UpdateVisibilityAndLOD(player.position);
        }
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
        if (LODManager.Instance != null)
        {
            LODManager.Instance.Register(this);
        }
        else
        {
            // Crea un LODManager al volo se non esiste in scena
            var go = GameObject.Find("LODManager");
            if (go == null)
            {
                go = new GameObject("LODManager");
                go.AddComponent<LODManager>();
            }
            LODManager.Instance.Register(this);
        }
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
                meshFilter.sharedMesh = targetMesh;
                
                // Aggiorniamo anche il MeshCollider se presente per garantire la coerenza della fisica
                MeshCollider mc = GetComponent<MeshCollider>();
                if (mc != null)
                {
                    mc.sharedMesh = targetMesh;
                }
            }
        }
    }
}