using UnityEngine;
using UnityEngine.Networking;
using TMPro;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System;
using System.Text;
using UnityEngine.InputSystem;
using UnityEngine.EventSystems;
using Newtonsoft.Json.Linq;

#if UNITY_EDITOR
using UnityEditor;
#endif

public class LLMWorldDirector : MonoBehaviour
{
    public TMP_InputField inputField;
    public string lmStudioEndpoint = "http://25.18.55.60:1234/v1/chat/completions";

    // Se GameSettings è presente (pannello Opzioni), il suo indirizzo server sovrascrive sempre
    // il campo Inspector qui sopra.
    string EffectiveLmStudioEndpoint =>
        GameSettings.Instance != null ? GameSettings.Instance.LlmEndpoint : lmStudioEndpoint;

    public string modelId = "google/gemma-4-26b-a4b-qat";

    // Budget token per le chiamate SENZA thinking (step 3 - asset list, e il pass di
    // classificazione fuso size/frequency/type/priority): sono risposte JSON brevi, ma con la
    // finestra di contesto ora a 124k teniamo comunque margine ampio per non rischiare troncamenti
    // quando la lista oggetti è lunga.
    public int maxTokens = 4096;

    // Budget token per le chiamate CON thinking (step 1 - world description, step 2 - parametri):
    // deve coprire sia il ragionamento nascosto del modello sia la risposta finale. Con 124k di
    // contesto disponibili sul server, 12288 è ancora una frazione minima — meglio abbondare che
    // rischiare che il thinking consumi tutto il budget e lasci la risposta vuota (vedi il fallback
    // in ExtractAssistantContent, pensato apposta per quel caso).
    public int maxTokensThinking = 12288;

    [Tooltip("Enable native thinking per Step 1 (world description): è il primo step, da cui dipendono tutti gli altri — beneficia del ragionamento per una descrizione più coerente. ON di default (comportamento storico: prima non era nemmeno selezionabile, era sempre acceso incondizionatamente).")]
    public bool thinkingForDescription = true;
    [Tooltip("Enable native thinking for Step 2 (parametri/noise). Ora ON di default: con 124k di contesto sul server il thinking non è più un problema di budget, e migliora la coerenza dei parametri generati.")]
    public bool thinkingForParams = true;
    [Tooltip("Enable native thinking per Step 3 (asset list): è uno step generativo/creativo (decide quali oggetti popolano il bioma), non una classificazione a regole fisse come il pass Size/Frequency/Type/Priority — beneficia del ragionamento allo stesso modo di Step 1/2. ON di default.")]
    public bool thinkingForAssetList = true;
    [Tooltip("Logga in console il 'reasoning_content' restituito dal modello (il pensiero nascosto prima della risposta vera), quando la risposta lo contiene. Solo debug/verifica — utile ora per capire se e quanto il modello in uso sta effettivamente ragionando su ciascuno step. Nessun impatto sulla pipeline.")]
    public bool logThinkingToConsole = true;

    [Tooltip("Esegue Step 2 (parametri/noise) e Step 3 (asset list) in parallelo invece che in sequenza. Sono indipendenti tra loro (dipendono solo dallo Step 1) ma girano sullo STESSO server LLM: se il server ha un solo slot di inferenza le richieste si accodano comunque (nessun danno, guadagno minimo); se ha più slot ma budget di contesto insufficiente per tenere due prompt di queste dimensioni in KV cache insieme, rischi risposte troncate/degradate invece di un errore esplicito. Prova ON, controlla i tempi e che size/frequency/type/priority e i parametri del bioma restino coerenti — se noti problemi, torna a OFF.")]
    public bool parallelizeStep2And3 = false;

    [Tooltip("Testo anteposto al system prompt per gli step con thinking ON (1/2/3). Gemma 4 ha il thinking OFF di default e va attivato esplicitamente col token <|think|> — qui, subito dopo il token, puoi anche istruire il modello su QUANTO ragionare: Gemma non ha un parametro numerico tipo 'reasoning_effort' (quello esiste solo per modelli openai/gpt-oss via LM Studio), la profondità si regola solo così, in linguaggio naturale. Riduci/allunga il testo per tarare l'effort. Lascia vuoto per non forzare nulla (utile se torni a un modello Qwen, che gestisce il thinking in altro modo tramite /no_think).")]
    [TextArea(2, 4)]
    public string thinkingDirective =
        "<|think|> Think briefly and efficiently. Focus only on the key decisions that affect the output — skip restating the instructions or listing every rule you're checking.";
    public GenerateTexture generateTexture;
    public GenerateAsset generateAsset;
    public WorldManager worldManager;

    static readonly (string llm, string config, string footstepField)[] TileFields =
    {
        ("surface", "surfaceTileID", "surface_footstep"),
        ("middle",  "middleTileID",  "middle_footstep"),
        ("deep",    "deepTileID",    "deep_footstep"),
        ("liquid",  "liquidTileID",  "liquid_footstep"),
        ("flower",  "flowerTileID",  "flower_footstep"),
        ("bush",    "bushTileID",    "bush_footstep"),
        ("rock",    "rockTileID",    "rock_footstep"),
    };

    public static event Action<string, float> OnGenerationProgress;
    public static event Action                OnTerrainPipelineDone;
    public static event Action<string>        OnGenerationFailed;

    // True while the world-description input field has focus. Checked by PlayerMovement/MouseLook
    // to freeze player control while the user is typing.
    public static bool IsUIFocused;

    void Awake()
    {
        if (generateAsset == null) generateAsset = FindAnyObjectByType<GenerateAsset>();
        if (worldManager == null) worldManager = FindAnyObjectByType<WorldManager>();
        if (generateTexture == null) generateTexture = FindAnyObjectByType<GenerateTexture>();
    }

    void OnEnable()
    {
        // Ci agganciamo all'evento di sblocco: scatta solo quando TUTTI gli asset del nuovo bioma
        // sono pronti (niente più Coda A/B — vedi GenerateAsset.OnBiomeAssetsReady).
        GenerateAsset.OnBiomeAssetsReady += TriggerTerrainTransition;

        // inputField è null nella scena di prima generazione (lì il testo arriva già via
        // SendRequestToLLM chiamato direttamente da FirstGeneration, senza passare da un campo di
        // testo in scena) — senza questo controllo andava in NullReferenceException ad ogni
        // OnEnable/Update/OnDisable per tutta la durata della prima generazione (build e Editor).
        if (inputField == null) return;

        inputField.onSelect.AddListener(OnInputSelected);
        inputField.onDeselect.AddListener(OnInputDeselected);
        inputField.onSubmit.AddListener(OnInputSubmit);
    }

    void OnDisable()
    {
        GenerateAsset.OnBiomeAssetsReady -= TriggerTerrainTransition;

        if (inputField == null) return;

        inputField.onSelect.RemoveListener(OnInputSelected);
        inputField.onDeselect.RemoveListener(OnInputDeselected);
        inputField.onSubmit.RemoveListener(OnInputSubmit);
    }

    void OnInputSelected(string _) => IsUIFocused = true;

    void OnInputDeselected(string _) => IsUIFocused = false;

    void OnInputSubmit(string text)
    {
        if (!string.IsNullOrEmpty(text)) SendRequestToLLM(text);
        inputField.text = "";
        EventSystem.current.SetSelectedGameObject(null); // scatena OnInputDeselected -> IsUIFocused = false
    }

    string BuildSystemPrompt()
    {
        var materials = LoadMaterials();
        string materialList = materials.Count > 0
            ? string.Join("\n", new List<string>(materials.Keys).ConvertAll(k => $"    - {k}"))
            : "    (none yet — invent new short English names)";
        return Prompts.ParametricSystem(materialList);
    }

    void Update()
    {
        if (inputField == null) return;
        if (Keyboard.current.tKey.wasPressedThisFrame && !inputField.isFocused)
        {
            inputField.ActivateInputField();
            inputField.text = "";
        }
    }

    public void SendRequestToLLM(string userPrompt)
    {
        StartCoroutine(PostRequestCoroutine(userPrompt));
    }

    IEnumerator PostRequestCoroutine(string userText)
    {
        OnGenerationProgress?.Invoke("Designing your world...", 0.05f);
        var step1 = new string[] { "" };
        // Controllato da thinkingForDescription (vedi campo Inspector) — è lo step più a monte da
        // cui dipendono tutti gli altri, e con 124k di contesto sul server possiamo permetterci un
        // budget ampio (maxTokensThinking) invece di rischiare che il ragionamento nascosto
        // consumi tutto lo spazio disponibile prima di arrivare alla risposta vera.
        yield return StartCoroutine(CallLLM(
            Prompts.DescriptionSystem(),
            userText,
            raw =>
            {
                string content = ExtractAssistantContent(raw);
                content = StripMarkdownCodeFence(content).Trim();
                content = StripThinkingTags(content);
                step1[0] = content;
            },
            disableThinking: !thinkingForDescription,
            maxTokensOverride: thinkingForDescription ? maxTokensThinking : maxTokens,
            callLabel: "Step 1 — World Description"));

        string worldDescription = step1[0];
        if (string.IsNullOrEmpty(worldDescription))
        {
            OnGenerationFailed?.Invoke("World description failed.");
            yield break;
        }

        OnGenerationProgress?.Invoke("Planning biome and assets...", 0.15f);
        var r2 = new string[] { "" };
        var r3 = new string[] { "" };

        var existingRegistry = LoadObjectRegistry();
        string existingList = BuildExistingObjectsList(existingRegistry);

        var step2Call = CallLLM(BuildSystemPrompt(), worldDescription,
            raw => { r2[0] = raw; },
            disableThinking: !thinkingForParams,
            maxTokensOverride: thinkingForParams ? maxTokensThinking : maxTokens,
            callLabel: "Step 2 — Parametric/Noise");

        var step3Call = CallLLM(Prompts.AssetListSystem(existingList), worldDescription,
            raw => { r3[0] = raw; },
            disableThinking: !thinkingForAssetList,
            maxTokensOverride: thinkingForAssetList ? maxTokensThinking : maxTokens,
            callLabel: "Step 3 — Asset List");

        if (parallelizeStep2And3)
        {
            // Indipendenti tra loro (dipendono solo dallo Step 1) ma sullo STESSO server LLM —
            // vedi tooltip su parallelizeStep2And3. Se il server ha un solo slot di inferenza le
            // richieste si accodano comunque lì (nessun danno, solo niente guadagno); il rischio
            // vero è con più slot configurati ma budget di contesto insufficiente per tenerne due
            // di queste dimensioni in KV cache insieme (risposte troncate/degradate anziché un
            // errore esplicito — motivo per cui questo resta opt-in e non il default).
            Coroutine c2 = StartCoroutine(step2Call);
            Coroutine c3 = StartCoroutine(step3Call);
            yield return c2;
            yield return c3;
        }
        else
        {
            yield return StartCoroutine(step2Call);
            yield return StartCoroutine(step3Call);
        }

        OnGenerationProgress?.Invoke("Generating terrain textures...", 0.25f);

        List<string>            newObjects     = null;
        Dictionary<string, int> currentObjects = null;
        if (!string.IsNullOrEmpty(r3[0]))
            ProcessAssetList(r3[0], out newObjects, out currentObjects);

        // Texture materiali (server immagini, porta diversa da quello chat) e classificazione
        // size/frequency/type/priority (server chat LLM) sono indipendenti e non condividono slot
        // d'inferenza — a differenza di Step2/Step3 qui non c'è motivo di aspettare l'una prima di
        // iniziare l'altra, quindi partono sempre insieme (non serve un toggle).
        // NOTA: SizeFrequencyCoroutine, una volta ricevuta la risposta, lancia in background anche
        // generateAsset.GenerateNew (generazione immagini/mesh dei singoli oggetti) SENZA aspettarlo
        // — quello sì torna a toccare il server immagini, quindi se ProcessResponseCoroutine non ha
        // ancora finito le texture dei materiali in quel momento, per un breve tratto le due code
        // di generazione immagini possono sovrapporsi sullo stesso server. Stesso ragionamento del
        // caso Step2/Step3: se il server processa una richiesta alla volta si accoda senza danni.
        OnGenerationProgress?.Invoke("Analyzing object properties...", 0.50f);
        Coroutine textureCoroutine = !string.IsNullOrEmpty(r2[0])
            ? StartCoroutine(ProcessResponseCoroutine(r2[0]))
            : null;

        Coroutine classificationCoroutine = null;
        if (currentObjects != null && currentObjects.Count > 0)
        {
            OnGenerationProgress?.Invoke("Finalizing world layout...", 0.55f);
            classificationCoroutine = StartCoroutine(SizeFrequencyCoroutine(worldDescription, currentObjects, newObjects));
        }

        if (textureCoroutine != null)
            yield return textureCoroutine;

        if (classificationCoroutine != null)
            yield return classificationCoroutine;
        else
            TriggerTerrainTransition();
        OnTerrainPipelineDone?.Invoke();
    }

    IEnumerator CallLLM(string systemPrompt, string userText, Action<string> onSuccess, bool disableThinking = false, int? maxTokensOverride = null, string callLabel = null)
    {
        if (disableThinking)
        {
            // Sintassi Qwen — su Gemma è testo inerte (il thinking lì è già OFF di default),
            // ma non fa danni se in futuro si torna a un modello Qwen.
            systemPrompt = "/no_think\n" + systemPrompt;
        }
        else if (!string.IsNullOrEmpty(thinkingDirective))
        {
            // Gemma 4 ha il thinking OFF di default: senza il token <|think|> qui dentro non
            // ragiona affatto, a prescindere dal budget di maxTokensThinking assegnato alla
            // chiamata. thinkingDirective porta sia l'attivazione sia l'istruzione di "quanto"
            // ragionare (vedi tooltip in Inspector).
            systemPrompt = thinkingDirective + "\n" + systemPrompt;
        }

        string escapedSystem = EscapeStringForJson(systemPrompt);
        string escapedUser = EscapeStringForJson(userText);
        int tokenBudget = maxTokensOverride ?? maxTokens;

        string chatPayload = "{" +
            "\"model\": \"" + modelId + "\"," +
            "\"messages\": [" +
                "{\"role\": \"system\", \"content\": \"" + escapedSystem + "\"}," +
                "{\"role\": \"user\", \"content\": \"" + escapedUser + "\"}" +
            "]," +
            "\"temperature\": 0.3," +
            "\"max_tokens\": " + tokenBudget + "," +
            "\"stream\": false" +
        "}";

        using (UnityWebRequest request = new UnityWebRequest(EffectiveLmStudioEndpoint, "POST"))
        {
            request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(chatPayload));
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", "application/json");

            yield return request.SendWebRequest();

            if (request.result == UnityWebRequest.Result.Success)
            {
                string raw = request.downloadHandler.text;

                // Il reasoning_content (quando il modello fa native thinking) arriva sempre nella
                // risposta accanto a "content", nello stesso oggetto message — prima veniva letto
                // solo come fallback silenzioso quando "content" risultava vuoto (vedi
                // ExtractAssistantContent). Qui lo logghiamo sempre, così è visibile in console per
                // ogni chiamata anche quando la risposta vera arriva regolarmente.
                if (logThinkingToConsole)
                {
                    string reasoning = ExtractReasoningContent(raw);
                    if (!string.IsNullOrEmpty(reasoning))
                    {
                        string label = string.IsNullOrEmpty(callLabel) ? "" : $" — {callLabel}";
                        Debug.Log($"<color=yellow>[LLM thinking{label}]</color>\n{reasoning}");
                    }
                }

                onSuccess(raw);
            }
        }
    }

    string EscapeStringForJson(string str)
    {
        if (string.IsNullOrEmpty(str)) return "";
        StringBuilder sb = new StringBuilder();
        foreach (char c in str)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\n': sb.Append("\\n"); break; 
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default: sb.Append(c); break;
            }
        }
        return sb.ToString();
    }

    IEnumerator ProcessResponseCoroutine(string rawResponse)
    {
        string cleanJson = null;
        try
        {
            string assistantText = ExtractAssistantContent(rawResponse);
            assistantText = StripMarkdownCodeFence(assistantText).Trim();
            assistantText = StripThinkingTags(assistantText);
            cleanJson = ExtractFirstJsonObject(assistantText);
        }
        catch (Exception) { yield break; }

        var materials = LoadMaterials();
        var resolved  = new Dictionary<string, int>();

        string paletteColorDescription = ExtractStringField(cleanJson, "palette_color_description") ?? "";

        foreach (var (llmField, _, footstepField) in TileFields)
        {
            string matName = ExtractStringField(cleanJson, llmField);
            if (string.IsNullOrEmpty(matName)) continue;
            matName = matName.Trim().ToLowerInvariant().Replace(" ", "_");

            if (!materials.ContainsKey(matName))
            {
                if (generateTexture == null) continue;

                // La stessa risposta LLM porta anche il tipo di suono passi per questo materiale
                // (grass/hard/soft/fluid/snow). Se manca o non è uno dei valori validi, lasciamo
                // che TextureAtlasManager classifichi dal nome come fallback (vedi AppendAlbedo).
                string footstepType = ExtractStringField(cleanJson, footstepField)?.Trim().ToLowerInvariant();
                if (!string.IsNullOrEmpty(footstepType) && worldManager != null && worldManager.atlasManager != null)
                    worldManager.atlasManager.SetPendingFootstepType(matName, footstepType);

                yield return StartCoroutine(GenerateAndWait(matName, paletteColorDescription));
                materials = LoadMaterials();
            }

            if (materials.TryGetValue(matName, out int id))
                resolved[llmField] = id + 1;
        }

        string finalJson = BuildFinalJson(cleanJson, resolved);
        string path = Path.Combine(PathSettings.GetJsonFolder(), "llm_generated.json");
        File.WriteAllText(path, finalJson);
    }

    void ProcessAssetList(string raw, out List<string> newObjects, out Dictionary<string, int> currentObjects)
    {
        newObjects     = new List<string>();
        currentObjects = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        string text = StripThinkingTags(StripMarkdownCodeFence(ExtractAssistantContent(raw)).Trim());
        List<string> names = ParseStringArray(text);

        var allObjects = LoadObjectRegistry();
        int nextId = allObjects.Count;

        foreach (string rawName in names)
        {
            string name = rawName.Trim().ToLowerInvariant().Replace('_', ' ');
            if (string.IsNullOrEmpty(name)) continue;

            if (!allObjects.ContainsKey(name))
            {
                allObjects[name] = nextId++;
                newObjects.Add(name);
            }
            currentObjects[name] = allObjects[name];
        }

        string baseDir = PathSettings.GetJsonFolder();
        SaveObjectRegistry(allObjects, Path.Combine(baseDir, "all_objects.json"));
        SaveObjectRegistry(currentObjects, Path.Combine(baseDir, "current_objects.json"));
#if UNITY_EDITOR
        AssetDatabase.ImportAsset("Assets/Json/current_objects.json", ImportAssetOptions.ForceSynchronousImport);
#endif
    }

    IEnumerator SizeFrequencyCoroutine(string worldDesc, Dictionary<string, int> currentObjects, List<string> newObjects)
    {
        string nameList = string.Join(", ", new List<string>(currentObjects.Keys).ConvertAll(k => $"\"{k}\""));

        // Un'unica chiamata al posto delle 4 sequenziali (Size/Frequency/Type/Priority erano lo
        // stesso identico input, 4 assi indipendenti dello stesso oggetto — vedi
        // Prompts.ClassificationSystem). ExtractTagValues sa già leggere il formato annidato
        // { "oggetto": { "size": ..., "frequency": ..., "type": ..., "priority": ... } }, quindi
        // riusiamo la stessa risposta grezza 4 volte, una per campo, senza cambiare
        // ParseAndSaveFourPassMetadata.
        var resClassification = new string[] { "" };
        yield return StartCoroutine(CallLLM(Prompts.ClassificationSystem(nameList), worldDesc,
            raw => { resClassification[0] = raw; },
            disableThinking: true,
            maxTokensOverride: maxTokens,
            callLabel: "Step 4 — Classification"));

        string resSize = resClassification[0];
        string resFreq = resClassification[0];
        string resType = resClassification[0];
        string resPriority = resClassification[0];

        // Salva i dati dei metadati su disco (current_objects.json diventa il registro del NUOVO
        // bioma). Da qui in poi la pipeline asset lavora su questo registro in background, ma il
        // mondo attivo (WorldManager._readyPrefabs/_pendingObjectNames/ecc.) resta quello VECCHIO,
        // invariato, finché GenerateAsset non segnala che gli asset critici sono pronti — vedi
        // TriggerTerrainTransition, che fa lo swap atomico in un colpo solo. Nessun ricaricamento
        // anticipato qui: era questo a causare mondo vuoto/oggetti sparenti vicino al giocatore
        // durante la generazione (lo stato "vivo" passava al nuovo registro molto prima che i
        // relativi asset esistessero davvero).
        ParseAndSaveFourPassMetadata(resSize, resFreq, resType, resPriority, currentObjects);

        OnGenerationProgress?.Invoke("Pre-allocating biome guardian meshes...", 0.65f);
        if (currentObjects != null && currentObjects.Count > 0)
            generateAsset?.GenerateNew(new List<string>(currentObjects.Keys));
    }

    /// <summary>
    /// Attiva la ricostruzione e l'interpolazione progressiva dei chunk a livello visivo.
    /// Invocato unicamente quando TUTTI gli asset del nuovo bioma sono pronti (GenerateAsset.OnBiomeAssetsReady).
    /// </summary>
    void TriggerTerrainTransition()
    {
        Debug.Log("<color=green>[LLMWorldDirector]</color> Gating superato con successo. Iniezione dati e avvio interpolazione.");
        OnGenerationProgress?.Invoke("Building terrain maps...", 0.75f);

        // Unico punto di swap: ApplyNewConfig congela il bioma vecchio (ancora quello attivo fino
        // a questo momento) e carica quello nuovo in un colpo solo, poi avvia l'interpolazione
        // visiva. Vedi il commento su ApplyNewConfig in WorldManager.
        worldManager?.ApplyNewConfig();
    }

    void ParseAndSaveFourPassMetadata(string rawSize, string rawFreq, string rawType, string rawPriority, Dictionary<string, int> currentObjects)
    {
        var sizes = ExtractTagValues(rawSize, "size");
        var frequencies = ExtractTagValues(rawFreq, "frequency");
        var types = ExtractTagValues(rawType, "type");
        var priorities = ExtractTagValues(rawPriority, "priority");

        var sb = new StringBuilder("{\n");
        int i = 0;
        foreach (var kvp in currentObjects)
        {
            string sz = sizes.ContainsKey(kvp.Key) ? sizes[kvp.Key] : "big";
            string fr = frequencies.ContainsKey(kvp.Key) ? frequencies[kvp.Key] : "common";
            string tp = types.ContainsKey(kvp.Key) ? types[kvp.Key] : "vegetation";
            
            int pr = 3;
            if (priorities.ContainsKey(kvp.Key) && int.TryParse(priorities[kvp.Key], out int parsedPr))
                pr = parsedPr;

            sb.Append($"  \"{kvp.Key}\": {{ \"id\": {kvp.Value}, \"size\": \"{sz}\", \"frequency\": \"{fr}\", \"type\": \"{tp}\", \"priority\": {pr} }}");
            if (i < currentObjects.Count - 1) sb.Append(",");
            sb.AppendLine();
            i++;
        }
        sb.Append("}");

        string path = Path.Combine(PathSettings.GetJsonFolder(), "current_objects.json");
        File.WriteAllText(path, sb.ToString());
#if UNITY_EDITOR
        AssetDatabase.ImportAsset("Assets/Json/current_objects.json", ImportAssetOptions.ForceSynchronousImport);
#endif
    }

    Dictionary<string, string> ExtractTagValues(string raw, string fieldName)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string text = StripThinkingTags(StripMarkdownCodeFence(ExtractAssistantContent(raw)).Trim());
        string jsonObj = ExtractFirstJsonObject(text);
        if (string.IsNullOrEmpty(jsonObj)) return dict;

        try
        {
            var root = JObject.Parse(jsonObj);
            foreach (var entry in root)
            {
                string key = entry.Key.Trim().ToLowerInvariant().Replace('_', ' ');
                if (entry.Value?.Type == JTokenType.Object)
                {
                    var val = entry.Value[fieldName];
                    if (val != null) dict[key] = val.ToString();
                }
                else if (entry.Value != null)
                {
                    dict[key] = entry.Value.ToString();
                }
            }
        }
        catch (Exception) { }
        return dict;
    }

    List<string> ParseStringArray(string text)
    {
        var list  = new List<string>();
        int start = text.IndexOf('[');
        int end   = text.LastIndexOf(']');
        if (start < 0 || end <= start) return list;
        string inner = text.Substring(start + 1, end - start - 1);
        foreach (string token in inner.Split(','))
        {
            string t = token.Trim().Trim('"');
            if (!string.IsNullOrEmpty(t)) list.Add(t);
        }
        return list;
    }

    string BuildExistingObjectsList(Dictionary<string, int> existingRegistry)
    {
        if (existingRegistry.Count == 0) return "(none yet)";

        var keys = new List<string>(existingRegistry.Keys);
        return string.Join(", ", keys.ConvertAll(k => $"\"{k}\""));
    }

    Dictionary<string, int> LoadObjectRegistry()
    {
        var    dict = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        string path = Path.Combine(PathSettings.GetJsonFolder(), "all_objects.json");
        if (!File.Exists(path)) return dict;
        string json = File.ReadAllText(path).Trim().Trim('{', '}');
        foreach (string line in json.Split(','))
        {
            string t = line.Trim();
            if (string.IsNullOrEmpty(t)) continue;
            int colon = t.IndexOf(':');
            if (colon < 0) continue;
            string key = t.Substring(0, colon).Trim().Trim('"').Trim().ToLowerInvariant().Replace('_', ' ');
            string val = t.Substring(colon + 1).Trim();
            if (int.TryParse(val, out int id)) dict[key] = id;
        }
        return dict;
    }

    void SaveObjectRegistry(Dictionary<string, int> dict, string path)
    {
        var sb = new StringBuilder("{\n");
        int i = 0;
        foreach (var kvp in dict)
        {
            sb.Append($"  \"{kvp.Key}\": {kvp.Value}");
            if (i < dict.Count - 1) sb.Append(",");
            sb.AppendLine();
            i++;
        }
        sb.Append("}");
        File.WriteAllText(path, sb.ToString());
    }

    IEnumerator GenerateAndWait(string matName, string paletteColorDescription)
    {
        bool done = false;
        Action<string> onDone = null;
        onDone = completedName =>
        {
            if (completedName == matName)
            {
                done = true;
                GenerateTexture.OnGenerationComplete -= onDone;
            }
        };
        GenerateTexture.OnGenerationComplete += onDone;
        generateTexture.Generate(matName, paletteColorDescription);
        yield return new WaitUntil(() => done);
    }

    Dictionary<string, int> LoadMaterials()
    {
        var dict = new Dictionary<string, int>();
        string path = Path.Combine(PathSettings.GetJsonFolder(), "materials.json");
        if (!File.Exists(path)) return dict;

        // materials.json ora salva { "name": { "id": N, "footstepType": "..." } } (vedi
        // TextureAtlasManager), ma resta compatibile col vecchio formato flat { "name": N }
        // per file non ancora migrati.
        JObject root;
        try { root = JObject.Parse(File.ReadAllText(path)); }
        catch (Exception) { return dict; }

        foreach (var prop in root.Properties())
        {
            int id = prop.Value.Type == JTokenType.Object
                ? (prop.Value.Value<int?>("id") ?? -1)
                : prop.Value.Value<int>();
            if (id >= 0) dict[prop.Name] = id;
        }
        return dict;
    }

    string ExtractStringField(string json, string fieldName)
    {
        string searchKey = "\"" + fieldName + "\"";
        int keyIndex = json.IndexOf(searchKey, StringComparison.Ordinal);
        if (keyIndex < 0) return null;
        int colon = json.IndexOf(':', keyIndex + searchKey.Length);
        if (colon < 0) return null;
        int i = colon + 1;
        while (i < json.Length && (json[i] == ' ' || json[i] == '\t' || json[i] == '\n' || json[i] == '\r')) i++;
        if (i >= json.Length || json[i] != '"') return null;
        TryReadJsonString(json, i, out string value);
        return value;
    }

    string ExtractRawField(string json, string fieldName)
    {
        string searchKey = "\"" + fieldName + "\"";
        int keyIndex = json.IndexOf(searchKey, StringComparison.Ordinal);
        if (keyIndex < 0) return null;
        int colon = json.IndexOf(':', keyIndex + searchKey.Length);
        if (colon < 0) return null;
        int start = colon + 1;
        while (start < json.Length && (json[start] == ' ' || json[start] == '\t')) start++;
        int end = start;
        while (end < json.Length && json[end] != ',' && json[end] != '}' && json[end] != '\n' && json[end] != '\r') end++;
        return json.Substring(start, end - start).Trim();
    }

    string BuildFinalJson(string llmJson, Dictionary<string, int> resolved)
    {
        var entries = new List<string>();
        // Fields copied verbatim (as raw JSON literals) from the LLM's response — covers
        // both numbers and the isStructuredBiome boolean, since ExtractRawField just grabs
        // whatever literal follows the key.
        string[] rawLiteralFields = {
            "scaleMacro", "macroInfluence",
            "scaleBillow", "ampBillow", "billowExponent",
            "scaleRidged", "ampRidged", "ridgedExponent",
            "warpStrength", "warpScale",
            "baseTerrainHeight", "waterLevel",
            "chunkHeight", "chunkWidth",
            "scaleLayout", "objectDensityThreshold", "slopeMaxLimit",
            "isStructuredBiome"
        };
        foreach (string field in rawLiteralFields)
        {
            string val = ExtractRawField(llmJson, field);
            if (val != null) entries.Add($"  \"{field}\": {val}");
        }
        foreach (var (llmField, configField, _) in TileFields)
        {
            if (resolved.TryGetValue(llmField, out int id))
                entries.Add($"  \"{configField}\": {id}");
        }
        string palette = ExtractStringField(llmJson, "palette_color_description");
        if (palette != null)
        {
            string escapedPalette = palette.Replace("\"", "\\\"");
            entries.Add($"  \"palette_color_description\": \"{escapedPalette}\"");
        }
        return "{\n" + string.Join(",\n", entries) + "\n}";
    }

    string ExtractAssistantContent(string raw)
    {
        string content = ExtractMessageField(raw, "content");
        if (!string.IsNullOrEmpty(content)) return content;

        // Se "content" è vuoto (es. il modello ha speso tutto il budget di max_tokens nel
        // reasoning senza arrivare alla risposta finale), recuperiamo almeno il reasoning così la
        // pipeline non riceve una stringa vuota — vedi anche il log esplicito in CallLLM, che
        // mostra il reasoning_content ad ogni chiamata (non solo in questo caso limite).
        string reasoning = ExtractReasoningContent(raw);
        return string.IsNullOrEmpty(reasoning) ? string.Empty : "<think>" + reasoning + "</think>";
    }

    string ExtractReasoningContent(string raw) => ExtractMessageField(raw, "reasoning_content");

    // Legge un campo stringa dentro choices[0].message.* (es. "content" o "reasoning_content" —
    // i modelli con native thinking li restituiscono entrambi nello stesso oggetto message).
    string ExtractMessageField(string raw, string fieldName)
    {
        int choicesIndex = raw.IndexOf("\"choices\"", StringComparison.Ordinal);
        if (choicesIndex < 0) return null;
        int messageIndex = raw.IndexOf("\"message\"", choicesIndex, StringComparison.Ordinal);
        if (messageIndex < 0) return null;
        int keyIndex = raw.IndexOf("\"" + fieldName + "\"", messageIndex, StringComparison.Ordinal);
        if (keyIndex < 0) return null;
        int colonIndex = raw.IndexOf(':', keyIndex);
        if (colonIndex < 0) return null;
        int startQuote = raw.IndexOf('"', colonIndex + 1);
        if (startQuote < 0) return null;
        return TryReadJsonString(raw, startQuote, out string value) ? value : null;
    }

    bool TryReadJsonString(string source, int startQuoteIndex, out string value)
    {
        value = string.Empty;
        if (startQuoteIndex < 0 || startQuoteIndex >= source.Length || source[startQuoteIndex] != '"')
            return false;

        var sb = new StringBuilder();
        for (int i = startQuoteIndex + 1; i < source.Length; i++)
        {
            char c = source[i];
            if (c == '"') { value = sb.ToString(); return true; }
            if (c == '\\')
            {
                if (i + 1 >= source.Length) return false;
                char next = source[i + 1];
                switch (next)
                {
                    case '"':  sb.Append('"');  i++; break;
                    case '\\': sb.Append('\\'); i++; break;
                    case '/':  sb.Append('/');  i++; break;
                    case 'b':  sb.Append('\b'); i++; break;
                    case 'f':  sb.Append('\f'); i++; break;
                    case 'n':  sb.Append('\n'); i++; break;
                    case 'r':  sb.Append('\r'); i++; break;
                    case 't':  sb.Append('\t'); i++; break;
                    case 'u':
                        if (i + 5 >= source.Length) return false;
                        string hex = source.Substring(i + 2, 4);
                        if (!int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out int cp)) return false;
                        sb.Append((char)cp); i += 5; break;
                    default: sb.Append(next); i++; break;
                }
            }
            else sb.Append(c);
        }
        return false;
    }

    string StripThinkingTags(string text)
    {
        int start = text.IndexOf("<think>", StringComparison.OrdinalIgnoreCase);
        while (start >= 0)
        {
            int end = text.IndexOf("</think>", start, StringComparison.OrdinalIgnoreCase);
            if (end < 0) { text = text.Substring(0, start).Trim(); break; }
            text  = (text.Substring(0, start) + text.Substring(end + 8)).Trim();
            start = text.IndexOf("<think>", StringComparison.OrdinalIgnoreCase);
        }
        return text.Trim();
    }

    string StripMarkdownCodeFence(string text)
    {
        string trimmed = text.Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal)) return trimmed;
        int firstNewLine = trimmed.IndexOf('\n');
        if (firstNewLine < 0) return trimmed;
        int lastFence = trimmed.LastIndexOf("```", StringComparison.Ordinal);
        if (lastFence <= firstNewLine) return trimmed;
        return trimmed.Substring(firstNewLine + 1, lastFence - firstNewLine - 1).Trim();
    }

    string ExtractFirstJsonObject(string text)
    {
        int start = text.IndexOf('{');
        if (start < 0) return string.Empty;
        bool inString = false, escaping = false;
        int depth = 0;
        for (int i = start; i < text.Length; i++)
        {
            char c = text[i];
            if (escaping) { escaping = false; continue; }
            if (c == '\\') { escaping = true; continue; }
            if (c == '"') { inString = !inString; continue; }
            if (inString) continue;
            if (c == '{') depth++;
            if (c == '}') { depth--; if (depth == 0) return text.Substring(start, i - start + 1); }
        }
        return string.Empty;
    }
}