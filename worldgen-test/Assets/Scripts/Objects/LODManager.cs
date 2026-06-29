using UnityEngine;
using System.Collections.Generic;

public class LODManager : MonoBehaviour
{
    public static LODManager Instance;
    public Transform player;

    private List<ObjectDistanceCuller> cullers = new List<ObjectDistanceCuller>();
    private List<ObjectDistanceCuller> pendingRegister = new List<ObjectDistanceCuller>();
    private List<ObjectDistanceCuller> pendingUnregister = new List<ObjectDistanceCuller>();
    private bool isUpdating = false;

    void Awake()
    {
        if (Instance == null)
        {
            Instance = this;
        }
        else if (Instance != this)
        {
            Destroy(gameObject);
        }
    }

    public void Register(ObjectDistanceCuller culler)
    {
        if (isUpdating)
        {
            if (!pendingRegister.Contains(culler) && !cullers.Contains(culler))
            {
                pendingRegister.Add(culler);
            }
            if (pendingUnregister.Contains(culler))
            {
                pendingUnregister.Remove(culler);
            }
        }
        else
        {
            if (!cullers.Contains(culler))
            {
                cullers.Add(culler);
            }
        }
    }

    public void Unregister(ObjectDistanceCuller culler)
    {
        if (isUpdating)
        {
            if (!pendingUnregister.Contains(culler))
            {
                pendingUnregister.Add(culler);
            }
            if (pendingRegister.Contains(culler))
            {
                pendingRegister.Remove(culler);
            }
        }
        else
        {
            cullers.Remove(culler);
        }
    }

    void Update()
    {
        if (player == null)
        {
            var wm = FindAnyObjectByType<WorldManager>();
            if (wm != null)
            {
                player = wm.player;
            }
            if (player == null) return;
        }

        isUpdating = true;

        int count = cullers.Count;
        if (count > 0)
        {
            Vector3 playerPos = player.position;
            int frameCount = Time.frameCount;

            // Esegue il controllo su un quarto degli oggetti registrati ad ogni frame (Sliced Update)
            for (int i = count - 1; i >= 0; i--)
            {
                // Verifica di sicurezza sull'indice se la lista viene modificata esternamente
                if (i >= cullers.Count) continue;

                if (i % 4 == frameCount % 4)
                {
                    if (cullers[i] != null)
                    {
                        cullers[i].UpdateVisibilityAndLOD(playerPos);
                    }
                    else
                    {
                        cullers.RemoveAt(i);
                    }
                }
            }
        }

        isUpdating = false;

        // Applica le modifiche in sospeso alla fine del ciclo
        if (pendingUnregister.Count > 0)
        {
            for (int i = 0; i < pendingUnregister.Count; i++)
            {
                cullers.Remove(pendingUnregister[i]);
            }
            pendingUnregister.Clear();
        }

        if (pendingRegister.Count > 0)
        {
            for (int i = 0; i < pendingRegister.Count; i++)
            {
                if (pendingRegister[i] != null && !cullers.Contains(pendingRegister[i]))
                {
                    cullers.Add(pendingRegister[i]);
                }
            }
            pendingRegister.Clear();
        }
    }
}
