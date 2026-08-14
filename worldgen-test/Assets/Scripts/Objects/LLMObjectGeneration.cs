using System;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;
using System.Collections;


public class LLMObjectGeneration : MonoBehaviour
{
    [Header("Object")]
    public string object_name = "house";

    [Header("Prompt")]
    [TextArea(3, 6)]
    public string stylePrompt;
    [TextArea(2, 4)]
    public string negativePrompt = "shadow, background, ground, floor, base, text, watermark, multiple elements";

    [Header("Generation")]
    public string serverAddress = "http://25.18.55.60:5000";
    [Tooltip("Seed fisso per /create_image (immagini oggetto) — letto anche da GenerateAsset.ImageProducer, che riusa questo campo invece di duplicarlo.")]
    public int imageSeed = 46;

    [Header("Output")]
    [Tooltip("Cartella dove salvare le immagini generate. Lascia vuoto (o \"Assets/...\") per usare " +
             "PathSettings.GetImagesFolder() — necessario perché in build \"Assets/...\" non esiste/non " +
             "è scrivibile: la scrittura falliva silenziosamente e bloccava per sempre tutta la coda di " +
             "generazione (la coroutine si fermava su un'eccezione non gestita, senza mai passare all'oggetto successivo).")]
    public string outputFolder = "Assets/GeneratedImages";

    // Percorso realmente usato: se il campo è vuoto o punta ancora al vecchio default valido solo in
    // Editor ("Assets/..."), lo ridirigiamo automaticamente a PathSettings.GetImagesFolder() — così
    // funziona anche su scene già salvate con il vecchio valore, senza dover ritoccare l'Inspector.
    string EffectiveOutputFolder =>
        string.IsNullOrEmpty(outputFolder) || outputFolder.StartsWith("Assets")
            ? PathSettings.GetImagesFolder()
            : outputFolder;

    // Se GameSettings è presente (impostato dal pannello Opzioni), il suo indirizzo server
    // sovrascrive sempre il campo Inspector — altrimenti si usa il valore locale come fallback.
    string EffectiveServerAddress =>
        GameSettings.Instance != null ? GameSettings.Instance.ImageServerBase : serverAddress;

    [Header("3D Generation")]
    public LLM3DGeneration llm3DGeneration;
    public bool autoGenerate = false;

    public bool IsGenerating { get; private set; }

    // Fires as soon as the PNG is ready, before 3D generation starts
    public event Action<string, byte[]> OnImageReady;

    void Start() { if (autoGenerate) StartObjectGeneration(); }

    [ContextMenu("Start Object Generation")]
    public void StartObjectGeneration()
    {
        if (IsGenerating)
        {
            Debug.LogWarning($"LLMObjectGeneration: era ancora in generazione — forzato reset.");
            StopAllCoroutines();
            IsGenerating = false;
        }

        OnValidate(); // rigenera sempre il prompt da object_name corrente
        IsGenerating = true;
        StartCoroutine(PostRequest());
    }

    void OnValidate()
    {
        stylePrompt = BuildStylePrompt(object_name);
    }

    // Estratto da OnValidate così che GenerateAsset possa costruire lo stesso identico prompt per
    // la pipeline batch (produttore Flux/SDXL + doppio consumer Trellis) senza duplicare il
    // template altrove — un'unica fonte di verità per come viene descritto un oggetto.
    public static string BuildStylePrompt(string objectName) =>
        $"A beautiful, high-quality 3D render of a single realistic {objectName}. High definition, soft lighting, vibrant color palette. The object is perfectly centered against a clean, plain, neutral background (like pastel blue or cream). Studio lighting, sharp details, a touch of cozy game aesthetic.";

    IEnumerator PostRequest()
    {
        //string paletteColorDescription = LoadPaletteColorDescription();
        string finalPrompt = stylePrompt;
        /*if (!string.IsNullOrEmpty(paletteColorDescription))
        {
            finalPrompt += $" Coherent with this color palette: {paletteColorDescription}.";
        }*/

        // Endpoint e schema allineati a GUIDA_UNITY_INTEGRAZIONE_PIPELINE.md: /create_image (non
        // più /generate_edit), "quality":"high" fisso — requisito del server, non un'opzione — e
        // niente più campo "transparent" (non fa parte dello schema richiesta attuale).
        string effectiveServer = EffectiveServerAddress;
        Debug.Log($"LLMObjectGeneration: invio richiesta immagine → {effectiveServer}/create_image | oggetto: {object_name} | prompt finale: {finalPrompt}");
        string json = $"{{\"prompt\": \"{EscapeJson(finalPrompt)}\", \"quality\": \"high\", \"negative_prompt\": \"{EscapeJson(negativePrompt)}\", \"seed\": {imageSeed}}}";
        byte[] bodyRaw = Encoding.UTF8.GetBytes(json);
        using var req = new UnityWebRequest(effectiveServer + "/create_image", "POST");
        req.uploadHandler   = new UploadHandlerRaw(bodyRaw);
        req.downloadHandler = new DownloadHandlerBuffer();
        req.SetRequestHeader("Content-Type", "application/json");

        yield return req.SendWebRequest();

        if (req.result != UnityWebRequest.Result.Success)
        {
            Debug.LogError($"LLMObjectGeneration: request failed — {req.responseCode} {req.error}\n{req.downloadHandler.text}");
            IsGenerating = false;
            yield break;
        }

        var imageData = req.downloadHandler.data;
        if (imageData == null || imageData.Length == 0)
        {
            Debug.LogError("LLMObjectGeneration: empty response from server");
            IsGenerating = false;
            yield break;
        }

        // Il PNG salvato qui è solo un debug/cache artifact (non serve al resto della pipeline: la
        // mesh 3D parte comunque da imageData già in memoria pochi righe sotto). Se la scrittura
        // fallisse per qualunque motivo (permessi, disco pieno, cartella non valida) non deve
        // bloccare tutta la generazione — prima di questo try/catch un'eccezione qui lasciava
        // IsGenerating bloccato a true per sempre, e con esso l'intera coda che segue in
        // GenerateAsset.GenerateList (sequenziale: un item bloccato blocca tutti i successivi).
        try
        {
            string folder = EffectiveOutputFolder;
            if (!Directory.Exists(folder))
                Directory.CreateDirectory(folder);
            string outputPath = Path.Combine(folder, $"{object_name}.png");
            File.WriteAllBytes(outputPath, imageData);
            Debug.Log($"LLMObjectGeneration: saved to {outputPath}");
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"LLMObjectGeneration: impossibile salvare il PNG di debug per '{object_name}' ({ex.Message}), proseguo comunque con la generazione 3D.");
        }

        OnImageReady?.Invoke(object_name, imageData);
        llm3DGeneration?.GenerateFromBytes(imageData, object_name);

        if (llm3DGeneration != null)
            yield return new WaitUntil(() => !llm3DGeneration.IsGenerating);

        IsGenerating = false;
    }

    // Skips the image HTTP request — starts 3D generation directly from pre-fetched bytes.
    // Used by GenerateAsset's pipeline mode so image[n+1] can overlap with 3D[n].
    public void StartFromImageBytes(string name, byte[] imageBytes)
    {
        if (IsGenerating)
        {
            Debug.LogWarning("LLMObjectGeneration: era ancora in generazione — forzato reset.");
            StopAllCoroutines();
            IsGenerating = false;
        }
        object_name  = name;
        IsGenerating = true;
        StartCoroutine(Run3DOnly(imageBytes));
    }

    IEnumerator Run3DOnly(byte[] imageBytes)
    {
        // Vedi commento in PostRequest: PNG di debug non essenziale, un fallimento qui non deve
        // bloccare per sempre IsGenerating (e con esso il resto della coda).
        try
        {
            string folder = EffectiveOutputFolder;
            if (!Directory.Exists(folder))
                Directory.CreateDirectory(folder);
            File.WriteAllBytes(Path.Combine(folder, $"{object_name}.png"), imageBytes);
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"LLMObjectGeneration: impossibile salvare il PNG di debug per '{object_name}' ({ex.Message}), proseguo comunque con la generazione 3D.");
        }

        llm3DGeneration?.GenerateFromBytes(imageBytes, object_name);
        if (llm3DGeneration != null)
            yield return new WaitUntil(() => !llm3DGeneration.IsGenerating);

        IsGenerating = false;
    }

    string LoadPaletteColorDescription()
    {
        try
        {
            string path = Path.Combine(PathSettings.GetJsonFolder(), "llm_generated.json");
            if (File.Exists(path))
            {
                var root = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(path));
                return root["palette_color_description"]?.ToString();
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[LLMObjectGeneration] Non è stato possibile caricare la palette dal file JSON: {ex.Message}");
        }
        return null;
    }

    static string EscapeJson(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n");
}
