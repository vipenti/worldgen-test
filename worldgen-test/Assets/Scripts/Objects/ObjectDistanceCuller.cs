using System.Collections;
using UnityEngine;

/// <summary>
/// Ottimizzatore di rendering asincrono a livello di singolo asset.
/// Attiva o disattiva i MeshRenderer del modello basandosi sulla distanza dal giocatore.
/// </summary>
public class ObjectDistanceCuller : MonoBehaviour
{
    private Transform player;
    private float maxDistance;
    private float maxDistanceSqr;
    private Renderer[] renderers;
    private bool isCurrentlyVisible = true;
    private float checkInterval = 0.25f; // Esegue il controllo solo 4 volte al secondo

    private string objectName;
    private float highPercent;
    private float medPercent;
    private MeshFilter meshFilter;
    private Mesh highMesh;
    private Mesh mediumMesh;
    private Mesh lowMesh;
    private float highDistanceSqr;
    private float medDistanceSqr;

    private Coroutine cullCoroutine;

    public void Initialize(Transform playerTransform, float maxDistance, string objectName = "", float highPercent = 0.25f, float medPercent = 0.50f)
    {
        player = playerTransform;
        this.maxDistance = maxDistance;
        maxDistanceSqr = maxDistance * maxDistance; // Usiamo la distanza al quadrato per evitare costosi calcoli di radice quadrata
        this.objectName = objectName;
        this.highPercent = highPercent;
        this.medPercent = medPercent;

        float highDist = maxDistance * highPercent;
        float medDist = maxDistance * medPercent;
        highDistanceSqr = highDist * highDist;
        medDistanceSqr = medDist * medDist;

        renderers = GetComponentsInChildren<Renderer>();
        meshFilter = GetComponentInChildren<MeshFilter>();

        // Risolviamo le mesh dalla cache se disponibile
        if (!string.IsNullOrEmpty(objectName))
        {
            if (VoxelRenderer.RuntimeMeshCache.TryGetValue(objectName + "_high", out Mesh hm) && hm != null)
                highMesh = hm;
            else if (VoxelRenderer.RuntimeMeshCache.TryGetValue(objectName, out Mesh om) && om != null)
                highMesh = om;
            else if (meshFilter != null)
                highMesh = meshFilter.sharedMesh;

            if (VoxelRenderer.RuntimeMeshCache.TryGetValue(objectName + "_medium", out Mesh mm) && mm != null)
                mediumMesh = mm;
            else
                mediumMesh = highMesh;

            if (VoxelRenderer.RuntimeMeshCache.TryGetValue(objectName + "_low", out Mesh lm) && lm != null)
                lowMesh = lm;
            else
                lowMesh = mediumMesh;
        }
        else if (meshFilter != null)
        {
            highMesh = meshFilter.sharedMesh;
            mediumMesh = meshFilter.sharedMesh;
            lowMesh = meshFilter.sharedMesh;
        }
        
        // Eseguiamo un primo controllo immediato
        UpdateVisibility();
        
        // Avviamo la coroutine solo se il GameObject è attivo in gerarchia
        if (gameObject.activeInHierarchy)
        {
            if (cullCoroutine != null) StopCoroutine(cullCoroutine);
            cullCoroutine = StartCoroutine(CullRoutine());
        }
    }

    void OnEnable()
    {
        if (player != null && gameObject.activeInHierarchy)
        {
            if (cullCoroutine != null) StopCoroutine(cullCoroutine);
            cullCoroutine = StartCoroutine(CullRoutine());
        }
    }

    void OnDisable()
    {
        if (cullCoroutine != null)
        {
            StopCoroutine(cullCoroutine);
            cullCoroutine = null;
        }
    }

    IEnumerator CullRoutine()
    {
        // Applichiamo un piccolo ritardo iniziale casuale (Dithering) per evitare 
        // che migliaia di oggetti esguano il calcolo nello stesso identico frame
        yield return new WaitForSeconds(UnityEngine.Random.Range(0f, checkInterval));

        while (true)
        {
            UpdateVisibility();
            yield return new WaitForSeconds(checkInterval);
        }
    }

    void UpdateVisibility()
    {
        if (player == null) return;

        // Calcolo della distanza vettoriale quadrata (ultra-performante)
        float currentDistSqr = (transform.position - player.position).sqrMagnitude;
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