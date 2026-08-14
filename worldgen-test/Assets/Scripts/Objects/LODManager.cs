using UnityEngine;
using System.Collections.Generic;

public class LODManager : MonoBehaviour
{
    public static LODManager Instance;
    public Transform player;

    // cullers è la lista "master" iterata a fette ad ogni frame; cullerIndex mappa ogni culler
    // registrato al suo indice in cullers, per poter fare l'equivalente di Contains/Remove in O(1)
    // invece di O(n) (List<T>.Contains/Remove sono entrambi scan lineari sull'intera lista — con
    // centinaia/migliaia di oggetti registrati e una cascata di OnEnable/OnDisable che ne
    // (de)registra molti nello stesso frame, vedi ObjectDistanceCuller e il flip dei chunk in/out
    // dal cono visivo in WorldManager, erano un costo quadratico nascosto proprio nei momenti in
    // cui il frame è già sotto pressione). La rimozione è uno swap-with-last: sposta l'ultimo
    // elemento nello slot liberato invece di fare lo shift di tutto quello che segue — l'ordine
    // della lista non ha nessun significato per l'algoritmo (il bucket "i % 4" sotto è comunque
    // arbitrario), quindi non c'è nessuna perdita di correttezza.
    private List<ObjectDistanceCuller> cullers = new List<ObjectDistanceCuller>();
    private Dictionary<ObjectDistanceCuller, int> cullerIndex = new Dictionary<ObjectDistanceCuller, int>();

    private List<ObjectDistanceCuller> pendingRegister = new List<ObjectDistanceCuller>();
    private HashSet<ObjectDistanceCuller> pendingRegisterSet = new HashSet<ObjectDistanceCuller>();
    private List<ObjectDistanceCuller> pendingUnregister = new List<ObjectDistanceCuller>();
    private HashSet<ObjectDistanceCuller> pendingUnregisterSet = new HashSet<ObjectDistanceCuller>();
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
            if (!cullerIndex.ContainsKey(culler) && pendingRegisterSet.Add(culler))
            {
                pendingRegister.Add(culler);
            }
            if (pendingUnregisterSet.Remove(culler))
            {
                // Raro (un oggetto che si disabilita e riabilita nello stesso frame di update): la
                // lista pendente resta comunque piccola (bounded dalle transizioni di QUESTO solo
                // frame), quindi un Remove O(n) qui è accettabile.
                pendingUnregister.Remove(culler);
            }
        }
        else
        {
            AddCullerImmediate(culler);
        }
    }

    public void Unregister(ObjectDistanceCuller culler)
    {
        if (isUpdating)
        {
            if (pendingUnregisterSet.Add(culler))
            {
                pendingUnregister.Add(culler);
            }
            if (pendingRegisterSet.Remove(culler))
            {
                pendingRegister.Remove(culler); // vedi nota sopra
            }
        }
        else
        {
            RemoveCullerImmediate(culler);
        }
    }

    void AddCullerImmediate(ObjectDistanceCuller culler)
    {
        if (culler == null || cullerIndex.ContainsKey(culler)) return;
        cullerIndex[culler] = cullers.Count;
        cullers.Add(culler);
    }

    void RemoveCullerImmediate(ObjectDistanceCuller culler)
    {
        if (culler == null || !cullerIndex.TryGetValue(culler, out int idx)) return;
        RemoveAtSwap(idx);
    }

    // Rimuove l'elemento all'indice idx spostandoci sopra l'ultimo elemento della lista (O(1)
    // invece di List.RemoveAt(idx), che fa lo shift di tutto quello che sta dopo — O(n)).
    void RemoveAtSwap(int idx)
    {
        int lastIdx = cullers.Count - 1;
        var removed = cullers[idx];
        cullerIndex.Remove(removed);

        if (idx != lastIdx)
        {
            var moved = cullers[lastIdx];
            cullers[idx] = moved;
            cullerIndex[moved] = idx;
        }
        cullers.RemoveAt(lastIdx);
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

            // Esegue il controllo su un quarto degli oggetti registrati ad ogni frame (Sliced Update).
            // Iteriamo dal fondo: se un elemento nullo viene rimosso con lo swap qui sotto, l'elemento
            // spostato al suo posto (che veniva dal fondo, quindi già processato in questo giro) non
            // verrà rivisitato in questo stesso frame — nessun problema di correttezza, solo un frame
            // di ritardo sul suo prossimo controllo, del tutto trascurabile.
            for (int i = count - 1; i >= 0; i--)
            {
                // Verifica di sicurezza sull'indice se la lista viene modificata esternamente
                if (i >= cullers.Count) continue;

                if (i % 4 == frameCount % 4)
                {
                    var c = cullers[i];
                    if (c != null)
                    {
                        c.UpdateVisibilityAndLOD(playerPos);
                    }
                    else
                    {
                        RemoveAtSwap(i);
                    }
                }
            }
        }

        isUpdating = false;

        // Applica le modifiche in sospeso alla fine del ciclo.
        if (pendingUnregister.Count > 0)
        {
            for (int i = 0; i < pendingUnregister.Count; i++)
                RemoveCullerImmediate(pendingUnregister[i]);
            pendingUnregister.Clear();
            pendingUnregisterSet.Clear();
        }

        if (pendingRegister.Count > 0)
        {
            for (int i = 0; i < pendingRegister.Count; i++)
                AddCullerImmediate(pendingRegister[i]);
            pendingRegister.Clear();
            pendingRegisterSet.Clear();
        }
    }
}
