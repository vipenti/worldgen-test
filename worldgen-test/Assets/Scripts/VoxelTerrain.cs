using UnityEngine;

public class VoxelTerrain : MonoBehaviour
{
    public GameObject voxelPrefab; // Il cubo da istanziare
    public int width = 50;         // Larghezza della griglia
    public int depth = 50;         // Profondità della griglia
    
    [Header("Parametri Perlin")]
    public float scale = 0.1f;      // "Zoom" del rumore (Parametro Utente)
    public int heightMultiplier = 15; // Altezza massima delle montagne
    public float offsetX = 100f;    // Offset per variare il seed
    public float offsetZ = 100f;

    void Start()
    {
        GenerateTerrain();
    }

    void GenerateTerrain()
    {
        for (int x = 0; x < width; x++)
        {
            for (int z = 0; z < depth; z++)
            {
                // 1. Calcola il valore di Perlin Noise 2D
                // Aggiungiamo gli offset per poter cambiare il "seed" del mondo
                float xCoord = (float)x * scale + offsetX;
                float zCoord = (float)z * scale + offsetZ;
                
                float noiseValue = Mathf.PerlinNoise(xCoord, zCoord);

                // 2. Trasforma il valore (0-1) in un'altezza intera
                int yHeight = Mathf.FloorToInt(noiseValue * heightMultiplier);

                // 3. Istanzia i cubi verticalmente
                for (int y = 0; y <= yHeight; y++)
                {
                    Vector3 pos = new Vector3(x, y, z);
                    Instantiate(voxelPrefab, pos, Quaternion.identity, transform);
                }
            }
        }
    }
}