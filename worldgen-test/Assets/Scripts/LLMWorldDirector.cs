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

public class LLMWorldDirector : MonoBehaviour
{
    public TMP_InputField inputField;
    public string lmStudioEndpoint = "http://25.18.55.60:1234/v1/chat/completions";
    public string modelId = "qwen/qwen3.5-9b";
    public int maxTokens = 8192;
    [Tooltip("Enable Qwen3 thinking for Step 2 (JSON params). Disable if context window is small (<=4096).")]
    public bool thinkingForParams = true; // Impostato a true per forzare l'uso del tag <analysis> ottimizzato
    public GenerateTexture generateTexture;

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

        if (Keyboard.current.periodKey.wasPressedThisFrame && inputField.isFocused)
        {
            if (!string.IsNullOrEmpty(inputField.text))
            {
                SendRequestToLLM(inputField.text);
                inputField.text = "";
                EventSystem.current.SetSelectedGameObject(null);
            }
        }
    }

    public void SendRequestToLLM(string userPrompt)
    {
        StartCoroutine(PostRequestCoroutine(userPrompt));
    }

    IEnumerator PostRequestCoroutine(string userText)
    {
        // ── STEP 1: expand user concept into a rich world description ──────────
        Debug.Log("<color=cyan>LLMWorldDirector:</color> Step 1 — generazione descrizione mondo...");

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
            Debug.LogError("LLMWorldDirector: Step 1 fallito — nessuna descrizione ricevuta.");
            yield break;
        }
        Debug.Log("<color=cyan>LLMWorldDirector:</color> Descrizione mondo generata:\n" + worldDescription);

        // ── STEP 2: derive noise parameters + materials from description ───────
        Debug.Log("<color=cyan>LLMWorldDirector:</color> Step 2 — generazione parametri bioma...");
        var step2 = new string[] { "" };
        yield return StartCoroutine(CallLLM(
            BuildSystemPrompt(),
            worldDescription,
            raw => step2[0] = raw,
            disableThinking: !thinkingForParams));

        string rawParamResponse = step2[0];
        if (string.IsNullOrEmpty(rawParamResponse))
        {
            Debug.LogError("LLMWorldDirector: Step 2 fallito — nessuna risposta parametri.");
            yield break;
        }
        
        yield return StartCoroutine(ProcessResponseCoroutine(rawParamResponse));
    }

    IEnumerator CallLLM(string systemPrompt, string userText, Action<string> onSuccess, bool disableThinking = false)
    {
        if (disableThinking)
            systemPrompt = "/no_think\n" + systemPrompt;

        // CORREZIONE CRUCIALE: Usiamo JsonUtility escape o rimpiazziamo i caratteri preservando i ritorni a capo (\n validi nel JSON)
        string escapedSystem = EscapeStringForJson(systemPrompt);
        string escapedUser = EscapeStringForJson(userText);

        string chatPayload = "{" +
            "\"model\": \"" + modelId + "\"," +
            "\"messages\": [" +
                "{\"role\": \"system\", \"content\": \"" + escapedSystem + "\"}," +
                "{\"role\": \"user\", \"content\": \"" + escapedUser + "\"}" +
            "]," +
            "\"temperature\": 0.3," + // Abbassata la temperatura a 0.3 per renderlo più matematico e meno incline a inventare
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
            {
                onSuccess(request.downloadHandler.text);
            }
            else
            {
                Debug.LogError($"Errore API di chiamata LLM: {request.error} | Content: {request.downloadHandler.text}");
            }
        }
    }

    // Trasforma i ritorni a capo reali in \n testuali leggibili dalla stringa JSON senza rompere la formattazione
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
                case '\n': sb.Append("\\n"); break; // Preserva l'andata a capo nel JSON!
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
            if (string.IsNullOrWhiteSpace(assistantText))
                throw new Exception("Impossibile estrarre content logico.");

            assistantText = StripMarkdownCodeFence(assistantText).Trim();
            assistantText = StripThinkingTags(assistantText);
            cleanJson = ExtractFirstJsonObject(assistantText);
            
            if (string.IsNullOrWhiteSpace(cleanJson))
                throw new Exception("Nessun oggetto JSON trovato nella risposta pulita dell'LLM.");
        }
        catch (Exception e)
        {
            Debug.LogError("Errore estrazione JSON dall'LLM: " + e.Message + " -> Risposta Raw ricevuta: " + rawResponse);
            yield break;
        }

        var materials = LoadMaterials();
        var resolved  = new Dictionary<string, int>();

        foreach (var (llmField, _) in TileFields)
        {
            string matName = ExtractStringField(cleanJson, llmField);
            if (string.IsNullOrEmpty(matName)) continue;

            matName = matName.Trim().ToLowerInvariant().Replace(" ", "_");

            if (!materials.ContainsKey(matName))
            {
                if (generateTexture == null) {continue;}
                Debug.Log($"<color=orange>LLMWorldDirector:</color> Generazione texture per nuovo materiale: '{matName}'...");
                yield return StartCoroutine(GenerateAndWait(matName));
                materials = LoadMaterials();
            }

            if (materials.TryGetValue(matName, out int id))
                resolved[llmField] = id + 1; 
        }

        string finalJson = BuildFinalJson(cleanJson, resolved);
        string path = Path.Combine(Application.dataPath, "Json", "llm_generated.json");
        
        // Assicuriamoci che la cartella esista
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllText(path, finalJson);

        Debug.Log("<color=green>LLMWorldDirector: Configurazione Bioma e Texture salvata con successo!</color>");
    }

    IEnumerator GenerateAndWait(string matName)
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
        generateTexture.Generate(matName);
        yield return new WaitUntil(() => done);
    }

    Dictionary<string, int> LoadMaterials()
    {
        var dict = new Dictionary<string, int>();
        string path = Path.Combine(Application.dataPath, "Json", "materials.json");
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
        string[] numericFields = {
            "scaleMacro", "macroInfluence",
            "scaleBillow", "ampBillow", "billowExponent",
            "scaleRidged", "ampRidged", "ridgedExponent",
            "warpStrength", "warpScale",
            "baseTerrainHeight", "waterLevel",
            "chunkHeight", "chunkWidth",
            "scaleLayout", "objectDensityThreshold", "slopeMaxLimit"
        };
        foreach (string field in numericFields)
        {
            string val = ExtractRawField(llmJson, field);
            if (val != null) entries.Add($"  \"{field}\": {val}");
        }
        foreach (var (llmField, configField) in TileFields)
        {
            if (resolved.TryGetValue(llmField, out int id))
                entries.Add($"  \"{configField}\": {id}");
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