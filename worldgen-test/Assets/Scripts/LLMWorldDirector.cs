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
            : "    (nessuno ancora — devi inventare nomi nuovi)";

        return
@"You are a Procedural Generation Expert for a Unity Voxel Engine.
Your goal is to translate user descriptions into specific JSON parameters.

AVAILABLE MATERIALS (use these names when they fit the biome):
" + materialList + @"

PARAMETER GUIDELINES:
- perlinScale: Controls terrain frequency. [0.01-0.03] large rolling hills, [0.08-0.12] jagged mountains.
- chunkHeight: Controls max amplitude. [10-30] plains/beaches, [60-80] tall peaks.
- chunkWidth: Always 16.
- waterLevel: Y-coordinate below which liquid fills.
- detailScale: Detail noise frequency. [0.1-0.2] typical.
- octaves: Noise detail layers. [2] smooth, [4] typical varied terrain, [6] very rugged.
- ridgeFactor: Peak sharpness. [0.0] soft rounded hills, [0.5] mixed, [1.0] sharp ridges and narrow valleys.
- warpStrength: Terrain folding/warping. [0.0] regular, [0.3] organic natural look, [0.7] heavily eroded.

TILE ASSIGNMENT:
Assign a material name (string) to each of: surface, middle, deep, liquid, flower, bush, rock.
- Prefer existing material names from AVAILABLE MATERIALS when they fit.
- If no existing material fits, invent a short descriptive English name (e.g. ""volcanic_rock"", ""arctic_snow""). New materials will be generated automatically.

OUTPUT FORMAT — ONLY the JSON object, no prose:
{
  ""perlinScale"": <float>,
  ""chunkHeight"": <int>,
  ""chunkWidth"": 16,
  ""waterLevel"": <int>,
  ""detailScale"": <float>,
  ""octaves"": <int>,
  ""ridgeFactor"": <float>,
  ""warpStrength"": <float>,
  ""surface"": ""<material_name>"",
  ""middle"": ""<material_name>"",
  ""deep"":   ""<material_name>"",
  ""liquid"": ""<material_name>"",
  ""flower"": ""<material_name>"",
  ""bush"":   ""<material_name>"",
  ""rock"":   ""<material_name>""
}

FEW-SHOT EXAMPLES:

User: ""A peaceful green meadow with some flowers""
Output: {
  ""perlinScale"": 0.005, ""chunkHeight"": 25, ""chunkWidth"": 16, ""waterLevel"": 5, ""detailScale"": 0.15,
  ""octaves"": 3, ""ridgeFactor"": 0.0, ""warpStrength"": 0.1,
  ""surface"": ""grass"", ""middle"": ""grass_dirt"", ""deep"": ""soil"",
  ""liquid"": ""water"", ""flower"": ""flower_patch"", ""bush"": ""wet_grass"", ""rock"": ""soil_rocks""
}

User: ""Rugged rocky canyons with deep water at the bottom""
Output: {
  ""perlinScale"": 0.09, ""chunkHeight"": 75, ""chunkWidth"": 16, ""waterLevel"": 20, ""detailScale"": 0.2,
  ""octaves"": 5, ""ridgeFactor"": 0.8, ""warpStrength"": 0.5,
  ""surface"": ""rocky_surface"", ""middle"": ""rock"", ""deep"": ""rock"",
  ""liquid"": ""deep_water"", ""flower"": ""rock_chips"", ""bush"": ""rocky_surface"", ""rock"": ""rock""
}

User: ""Tropical beach with white sand and shallow pools""
Output: {
  ""perlinScale"": 0.008, ""chunkHeight"": 15, ""chunkWidth"": 16, ""waterLevel"": 10, ""detailScale"": 0.1,
  ""octaves"": 2, ""ridgeFactor"": 0.0, ""warpStrength"": 0.1,
  ""surface"": ""white_sand"", ""middle"": ""wet_sand"", ""deep"": ""sand_rocks"",
  ""liquid"": ""shallow_water"", ""flower"": ""shell"", ""bush"": ""wet_grass"", ""rock"": ""sand_rocks""
}";
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
        string systemPrompt = BuildSystemPrompt();
        string cleanSystemPrompt = systemPrompt.Replace("\"", "\\\"").Replace("\n", " ").Replace("\r", " ");
        string cleanUserText = userText.Replace("\"", "\\\"").Replace("\n", " ").Replace("\r", " ");
        string mergedInput = "System: " + cleanSystemPrompt + " User: " + cleanUserText;

        string chatPayload = "{" +
            "\"model\": \"" + modelId + "\"," +
            "\"messages\": [" +
                "{\"role\": \"system\", \"content\": \"" + cleanSystemPrompt + "\"}," +
                "{\"role\": \"user\", \"content\": \"" + cleanUserText + "\"}" +
            "]," +
            "\"temperature\": 0.7," +
            "\"max_tokens\": 2000," +
            "\"stream\": false" +
        "}";

        string responsesPayload = "{" +
            "\"model\": \"" + modelId + "\"," +
            "\"input\": \"" + mergedInput + "\"," +
            "\"temperature\": 0.7," +
            "\"stream\": false" +
        "}";

        bool endpointLooksLikeResponses = lmStudioEndpoint.ToLowerInvariant().Contains("/responses");
        string[] payloads = endpointLooksLikeResponses
            ? new[] { responsesPayload, chatPayload }
            : new[] { chatPayload, responsesPayload };
        string[] payloadKinds = endpointLooksLikeResponses
            ? new[] { "responses(input)", "chat(messages)" }
            : new[] { "chat(messages)", "responses(input)" };

        string lastError = string.Empty;

        for (int i = 0; i < payloads.Length; i++)
        {
            using (UnityWebRequest request = new UnityWebRequest(lmStudioEndpoint, "POST"))
            {
                byte[] bodyRaw = Encoding.UTF8.GetBytes(payloads[i]);
                request.uploadHandler   = new UploadHandlerRaw(bodyRaw);
                request.downloadHandler = new DownloadHandlerBuffer();
                request.SetRequestHeader("Content-Type", "application/json");

                Debug.Log("Inviando richiesta LLM | payload: " + payloadKinds[i]);
                yield return request.SendWebRequest();

                if (request.result == UnityWebRequest.Result.Success)
                {
                    Debug.Log("Risposta raw LLM: " + request.downloadHandler.text);
                    yield return StartCoroutine(ProcessResponseCoroutine(request.downloadHandler.text));
                    yield break;
                }

                lastError = "Errore API: " + request.error + " | Response: " + request.downloadHandler.text;
                Debug.LogWarning("Tentativo fallito con payload " + payloadKinds[i] + ". " + lastError);
            }
        }

        Debug.LogError(lastError);
    }

    IEnumerator ProcessResponseCoroutine(string rawResponse)
    {
        // Estrazione e validazione JSON — senza yield, quindi in try/catch
        string cleanJson = null;
        try
        {
            string assistantText = ExtractAssistantContent(rawResponse);
            if (string.IsNullOrWhiteSpace(assistantText))
                throw new Exception("Impossibile estrarre choices[0].message.content dalla risposta.");

            assistantText = StripMarkdownCodeFence(assistantText).Trim();
            cleanJson = ExtractFirstJsonObject(assistantText);
            if (string.IsNullOrWhiteSpace(cleanJson))
                throw new Exception("Nessun oggetto JSON trovato nel content dell'assistente.");
        }
        catch (Exception e)
        {
            Debug.LogError("L'LLM non ha risposto con un JSON valido: " + e.Message);
            yield break;
        }

        // Risoluzione materiali (con possibili yield per generazione texture)
        var materials = LoadMaterials();
        var resolved  = new Dictionary<string, int>();

        foreach (var (llmField, _) in TileFields)
        {
            string matName = ExtractStringField(cleanJson, llmField);
            if (string.IsNullOrEmpty(matName)) continue;

            matName = matName.Trim().Replace(" ", "_");

            if (!materials.ContainsKey(matName))
            {
                if (generateTexture == null)
                {
                    Debug.LogWarning($"[LLMWorldDirector] GenerateTexture non assegnato — materiale '{matName}' ignorato.");
                    continue;
                }
                Debug.Log($"<color=cyan>LLMWorldDirector:</color> Materiale '{matName}' non trovato, generazione...");
                yield return StartCoroutine(GenerateAndWait(matName));
                materials = LoadMaterials();
            }

            if (materials.TryGetValue(matName, out int id))
                resolved[llmField] = id + 1; // +1: atlas slot 0 → blockID 1
            else
                Debug.LogWarning($"[LLMWorldDirector] Materiale '{matName}' non disponibile dopo generazione, ignorato.");
        }

        // Costruzione e salvataggio JSON finale
        string finalJson = BuildFinalJson(cleanJson, resolved);
        string path = Path.Combine("Assets", "Json", "llm_generated.json");
        File.WriteAllText(path, finalJson);

        Debug.Log("JSON finale salvato: " + finalJson);
        Debug.Log("<color=cyan>Nuovo bioma generato dall'IA e salvato!</color>");
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

    // --- HELPERS ---

    Dictionary<string, int> LoadMaterials()
    {
        var dict = new Dictionary<string, int>();
        string path = Path.Combine("Assets", "Json", "materials.json");
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

        string[] numericFields = { "perlinScale", "chunkHeight", "chunkWidth", "waterLevel", "detailScale", "octaves", "ridgeFactor", "warpStrength" };
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

    // --- PARSING (invariato dall'originale) ---

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
