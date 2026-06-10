using System.Collections;
using UnityEngine;

/// <summary>
/// Ottimizzatore di rendering asincrono a livello di singolo asset.
/// Attiva o disattiva i MeshRenderer del modello basandosi sulla distanza dal giocatore.
/// </summary>
public class ObjectDistanceCuller : MonoBehaviour
{
    private Transform player;
    private float maxDistanceSqr;
    private Renderer[] renderers;
    private bool isCurrentlyVisible = true;
    private float checkInterval = 0.25f; // Esegue il controllo solo 4 volte al secondo

    public void Initialize(Transform playerTransform, float maxDistance)
    {
        player = playerTransform;
        maxDistanceSqr = maxDistance * maxDistance; // Usiamo la distanza al quadrato per evitare costosi calcoli di radice quadrata
        renderers = GetComponentsInChildren<Renderer>();
        
        // Eseguiamo un primo controllo immediato
        UpdateVisibility();
        
        // Avviamo la coroutine asincrona distribuita
        StartCoroutine(CullRoutine());
    }

    IEnumerator CullRoutine()
    {
        // Applichiamo un piccolo ritardo iniziale casuale (Dithering) per evitare 
        // che migliaia di oggetti esguano il calcolo nello stesso identico frame
        yield return new WaitForSeconds(Random.Range(0f, checkInterval));

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
    }
}