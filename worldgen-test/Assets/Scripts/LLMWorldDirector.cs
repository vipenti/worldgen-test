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
    public string modelId = "qwen/qwen3.5-9b";
    public int maxTokens = 1024;
    [Tooltip("Enable Qwen3 native thinking for Step 2. Keep OFF — prompts use explicit <analysis> tags instead, native thinking wastes context budget.")]
    public bool thinkingForParams = false;
    public GenerateTexture generateTexture;
    public GenerateAsset generateAsset;
    public WorldManager worldManager;

    static readonly (string llm, string config)[] TileFields =
    {
        ("surface", "surfaceTileID"),
        ("middle",  "middleTileID"),
        ("deep",    "deepTileID"),
        ("liquid",  "liquidTileID"),
        ("flower",  "flowerTileID"),
        ("bush",    "bushTileID"),
        ("rock",    "rockTileID"),
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
        // Ci agganciamo all'evento di sblocco della Coda A degli asset critici
        GenerateAsset.OnCriticalAssetsReady += TriggerTerrainTransition;

        inputField.onSelect.AddListener(OnInputSelected);
        inputField.onDeselect.AddListener(OnInputDeselected);
        inputField.onSubmit.AddListener(OnInputSubmit);
    }

    void OnDisable()
    {
        GenerateAsset.OnCriticalAssetsReady -= TriggerTerrainTransition;

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
        yield return StartCoroutine(CallLLM(
            Prompts.DescriptionSystem(),
            userText,
            raw =>
            {
                string content = ExtractAssistantContent(raw);
                content = StripMarkdownCodeFence(content).Trim();
                content = StripThinkingTags(content);
                step1[0] = content;
            }));

        string worldDescription = step1[0];
        if (string.IsNullOrEmpty(worldDescription))
        {
            OnGenerationFailed?.Invoke("World description failed.");
            yield break;
        }

        OnGenerationProgress?.Invoke("Planning biome and assets...", 0.15f);
        var r2 = new string[] { "" };
        var r3 = new string[] { "" };

        // Run sequentially (not concurrently) so the local LLM server only ever has to hold
        // one of these large prompts in its KV cache at a time. Firing both at once used to
        // require two simultaneous inference slots, which overflows the server's context
        // budget once the registries below grow large (see maxExistingObjects note).
        yield return StartCoroutine(CallLLM(BuildSystemPrompt(), worldDescription,
            raw => { r2[0] = raw; }, disableThinking: !thinkingForParams));

        var existingRegistry = LoadObjectRegistry();
        string existingList = BuildExistingObjectsList(existingRegistry);

        yield return StartCoroutine(CallLLM(Prompts.AssetListSystem(existingList), worldDescription,
            raw => { r3[0] = raw; }, disableThinking: true));

        OnGenerationProgress?.Invoke("Generating terrain textures...", 0.25f);

        if (!string.IsNullOrEmpty(r2[0]))
            yield return StartCoroutine(ProcessResponseCoroutine(r2[0]));

        List<string>            newObjects     = null;
        Dictionary<string, int> currentObjects = null;
        if (!string.IsNullOrEmpty(r3[0]))
            ProcessAssetList(r3[0], out newObjects, out currentObjects);

        OnGenerationProgress?.Invoke("Analyzing object properties...", 0.50f);

        if (currentObjects != null && currentObjects.Count > 0)
        {
            OnGenerationProgress?.Invoke("Finalizing world layout...", 0.55f);
            yield return StartCoroutine(SizeFrequencyCoroutine(worldDescription, currentObjects, newObjects));
        }
        else
        {
            TriggerTerrainTransition();
        }
        OnTerrainPipelineDone?.Invoke();
    }

    IEnumerator CallLLM(string systemPrompt, string userText, Action<string> onSuccess, bool disableThinking = false)
    {
        if (disableThinking)
            systemPrompt = "/no_think\n" + systemPrompt;

        string escapedSystem = EscapeStringForJson(systemPrompt);
        string escapedUser = EscapeStringForJson(userText);

        string chatPayload = "{" +
            "\"model\": \"" + modelId + "\"," +
            "\"messages\": [" +
                "{\"role\": \"system\", \"content\": \"" + escapedSystem + "\"}," +
                "{\"role\": \"user\", \"content\": \"" + escapedUser + "\"}" +
            "]," +
            "\"temperature\": 0.3," + 
            "\"max_tokens\": " + maxTokens + "," +
            "\"stream\": false" +
        "}";

        using (UnityWebRequest request = new UnityWebRequest(lmStudioEndpoint, "POST"))
        {
            request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(chatPayload));
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", "application/json");

            yield return request.SendWebRequest();

            if (request.result == UnityWebRequest.Result.Success)
                onSuccess(request.downloadHandler.text);
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

        foreach (var (llmField, _) in TileFields)
        {
            string matName = ExtractStringField(cleanJson, llmField);
            if (string.IsNullOrEmpty(matName)) continue;
            matName = matName.Trim().ToLowerInvariant().Replace(" ", "_");

            if (!materials.ContainsKey(matName))
            {
                if (generateTexture == null) continue;
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
        
        var resSize = new string[] { "" };
        yield return StartCoroutine(CallLLM(Prompts.SizeSystem(nameList), worldDesc, raw => { resSize[0] = raw; }, disableThinking: true));

        var resFreq = new string[] { "" };
        yield return StartCoroutine(CallLLM(Prompts.FrequencySystem(nameList), worldDesc, raw => { resFreq[0] = raw; }, disableThinking: true));

        var resType = new string[] { "" };
        yield return StartCoroutine(CallLLM(Prompts.TypeSystem(nameList), worldDesc, raw => { resType[0] = raw; }, disableThinking: true));

        var resPriority = new string[] { "" };
        yield return StartCoroutine(CallLLM(Prompts.PrioritySystem(nameList), worldDesc, raw => { resPriority[0] = raw; }, disableThinking: true));

        // Salva i dati dei metadati su disco
        ParseAndSaveFourPassMetadata(resSize[0], resFreq[0], resType[0], resPriority[0], currentObjects);

        // MODIFICA PRO-SYNCHRONIZATION: Congeliamo immediatamente gli aggiornamenti dei chunk ed allineiamo la memoria dei prefabbricati
        if (worldManager != null)
        {
            worldManager.holdChunkUpdates = true; 
            worldManager.BackupOldBiome(); // Salva lo stato dei vecchi prefabbricati prima di ricaricare i nuovi
            worldManager.ReloadBiomePrefabs(); // Forza WorldManager ad imparare subito le scale corrette dei nuovi oggetti!
        }

        OnGenerationProgress?.Invoke("Pre-allocating biome guardian meshes...", 0.65f);
        if (currentObjects != null && currentObjects.Count > 0)
            generateAsset?.GenerateNew(new List<string>(currentObjects.Keys));
    }

    /// <summary>
    /// Attiva la ricostruzione e l'interpolazione progressiva dei chunk a livello visivo.
    /// Invocato unicamente quando la Coda A di GenerateAsset ha dato l'ok.
    /// </summary>
    void TriggerTerrainTransition()
    {
        Debug.Log("<color=green>[LLMWorldDirector]</color> Gating superato con successo. Iniezione dati e avvio interpolazione.");
        OnGenerationProgress?.Invoke("Building terrain maps...", 0.75f);

        if (worldManager != null) worldManager.ApplyNewConfig();
        worldManager?.ReloadBiomePrefabs();

        // Sblocco aggiornamenti chunk: senza questo il mondo rimane congelato dopo la transizione
        if (worldManager != null) worldManager.holdChunkUpdates = false;
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

        string json = File.ReadAllText(path).Trim().Trim('{', '}');
        foreach (string line in json.Split(','))
        {
            string trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed)) continue;
            int colon = trimmed.IndexOf(':');
            if (colon < 0) continue;
            string key = trimmed.Substring(0, colon).Trim().Trim('"');
            string val = trimmed.Substring(colon + 1).Trim();
            if (int.TryParse(val, out int id))
                dict[key] = id;
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
        foreach (var (llmField, configField) in TileFields)
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
        int choicesIndex = raw.IndexOf("\"choices\"", StringComparison.Ordinal);
        if (choicesIndex < 0) return string.Empty;
        int messageIndex = raw.IndexOf("\"message\"", choicesIndex, StringComparison.Ordinal);
        if (messageIndex < 0) return string.Empty;
        int contentKeyIndex = raw.IndexOf("\"content\"", messageIndex, StringComparison.Ordinal);
        if (contentKeyIndex < 0) return string.Empty;
        int colonIndex = raw.IndexOf(':', contentKeyIndex);
        if (colonIndex < 0) return string.Empty;
        int startQuote = raw.IndexOf('"', colonIndex + 1);
        if (startQuote < 0) return string.Empty;
        TryReadJsonString(raw, startQuote, out string content);

        if (string.IsNullOrEmpty(content))
        {
            int rcIdx = raw.IndexOf("\"reasoning_content\"", messageIndex, StringComparison.Ordinal);
            if (rcIdx >= 0)
            {
                int rcColon = raw.IndexOf(':', rcIdx);
                if (rcColon >= 0)
                {
                    int rcQuote = raw.IndexOf('"', rcColon + 1);
                    if (rcQuote >= 0 && TryReadJsonString(raw, rcQuote, out string rc) && !string.IsNullOrEmpty(rc))
                        return "<think>" + rc + "</think>";
                }
            }
        }
        return content;
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