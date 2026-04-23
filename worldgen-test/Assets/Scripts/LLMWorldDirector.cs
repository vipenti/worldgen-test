using UnityEngine;
using UnityEngine.Networking;
using TMPro;
using System.Collections;
using System.IO;
using System;
using System.Text;
using UnityEngine.InputSystem; 
using UnityEngine.EventSystems;

public class LLMWorldDirector : MonoBehaviour
{
    public TMP_InputField inputField;
    public string lmStudioEndpoint = "http://127.0.0.1:1234/v1/chat/completions";
    public string modelId = "qwen/qwen3.5-9b";

   string systemPrompt = @"You are a Procedural Generation Expert for a Unity Voxel Engine. 
    Your goal is to translate user descriptions into specific JSON parameters.

    TEXTURE ATLAS LEGEND (16 tiles, 4x4 grid):
    1: grass | 2: grass with dirt patch | 3: soil | 4: soil with small rocks
    5: flower patch | 6: asphalt | 7: sandy rocks | 8: rocks
    9: sand | 10: wet sand | 11: sand with small rocks | 12: sand with bigger rocks
    13: water | 14: deep water | 15: water and sand | 16: wet grass

    PARAMETER GUIDELINES:
    - perlinScale: Controls frequency. [0.01-0.03] for large rolling hills, [0.08-0.12] for jagged mountains.
    - chunkHeight: Controls max amplitude. [10-30] for plains/beaches, [60-80] for tall peaks.
    - waterLevel: Any y-coordinate below this will be filled with liquidTileID.

    FEW-SHOT EXAMPLES:

    User: ""A peaceful green meadow with some flowers""
    Output: {
        ""perlinScale"": 0.005,
        ""chunkHeight"": 25,
        ""chunkWidth"": 16,
        ""surfaceTileID"": 1,
        ""middleTileID"": 2,
        ""deepTileID"": 3,
        ""liquidTileID"": 13,
        ""waterLevel"": 5,
        ""detailScale"": 0.15,
        ""flowerTileID"": 5,
        ""bushTileID"": 16,
        ""rockTileID"": 4
    }

    User: ""Rugged rocky canyons with deep canyons and water at the bottom""
    Output: {
        ""perlinScale"": 0.09,
        ""chunkHeight"": 75,
        ""chunkWidth"": 16,
        ""surfaceTileID"": 7,
        ""middleTileID"": 8,
        ""deepTileID"": 8,
        ""liquidTileID"": 14,
        ""waterLevel"": 20,
        ""detailScale"": 0.2,
        ""flowerTileID"": 4,
        ""bushTileID"": 7,
        ""rockTileID"": 8
    }

    User: ""Tropical beach with white sand and shallow pools""
    Output: {
        ""perlinScale"": 0.008,
        ""chunkHeight"": 15,
        ""chunkWidth"": 16,
        ""surfaceTileID"": 9,
        ""middleTileID"": 10,
        ""deepTileID"": 11,
        ""liquidTileID"": 15,
        ""waterLevel"": 10,
        ""detailScale"": 0.1,
        ""flowerTileID"": 15,
        ""bushTileID"": 16,
        ""rockTileID"": 11
    }

    Final Rule: Response must be ONLY the JSON object. No prose, no explanations.";

    void Update()
{
    // 1. Se premi T e NON stai già scrivendo nell'input field
    if (Keyboard.current.tKey.wasPressedThisFrame && !inputField.isFocused)
    {
        inputField.ActivateInputField();
        inputField.text = ""; 
    }

    // 2. Logica per l'invio (già vista, la manteniamo qui per comodità)
    if (Keyboard.current.periodKey.wasPressedThisFrame && inputField.isFocused)
    {
        if (!string.IsNullOrEmpty(inputField.text))
        {
            SendRequestToLLM(inputField.text);
            inputField.text = "";
            
            // Dopo l'invio, togliamo il focus così puoi tornare a camminare
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
                byte[] bodyRaw = System.Text.Encoding.UTF8.GetBytes(payloads[i]);
                request.uploadHandler = new UploadHandlerRaw(bodyRaw);
                request.downloadHandler = new DownloadHandlerBuffer();
                request.SetRequestHeader("Content-Type", "application/json");

                Debug.Log("Inviando richiesta LLM a endpoint: " + lmStudioEndpoint + " | payload: " + payloadKinds[i]);
                yield return request.SendWebRequest();

                if (request.result == UnityWebRequest.Result.Success)
                {
                    Debug.Log("Risposta raw LLM: " + request.downloadHandler.text);
                    ProcessResponse(request.downloadHandler.text);
                    yield break;
                }

                lastError = "Errore API: " + request.error + " | Response: " + request.downloadHandler.text;
                Debug.LogWarning("Tentativo fallito con payload " + payloadKinds[i] + ". " + lastError);
            }
        }

        Debug.LogError(lastError);
    }

    void ProcessResponse(string rawResponse)
    {
        try
        {
            string assistantText = ExtractAssistantContent(rawResponse);
            if (string.IsNullOrWhiteSpace(assistantText))
            {
                throw new Exception("Impossibile estrarre choices[0].message.content dalla risposta.");
            }

            assistantText = StripMarkdownCodeFence(assistantText).Trim();
            string cleanJson = ExtractFirstJsonObject(assistantText);
            if (string.IsNullOrWhiteSpace(cleanJson))
            {
                throw new Exception("Nessun oggetto JSON trovato nel content dell'assistente.");
            }

            string path = Path.Combine(Application.dataPath, "Json", "llm_generated.json");
            File.WriteAllText(path, cleanJson);

            Debug.Log("JSON finale estratto: " + cleanJson);
            Debug.Log("<color=cyan>Nuovo bioma generato dall'IA e salvato!</color>");
        }
        catch (Exception e)
        {
            Debug.LogError("L'LLM non ha risposto con un JSON valido: " + e.Message);
        }
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

        if (!TryReadJsonString(raw, startQuote, out string content))
        {
            return string.Empty;
        }

        return content;
    }

    bool TryReadJsonString(string source, int startQuoteIndex, out string value)
    {
        value = string.Empty;
        if (startQuoteIndex < 0 || startQuoteIndex >= source.Length || source[startQuoteIndex] != '"')
        {
            return false;
        }

        StringBuilder sb = new StringBuilder();
        for (int i = startQuoteIndex + 1; i < source.Length; i++)
        {
            char c = source[i];
            if (c == '"')
            {
                value = sb.ToString();
                return true;
            }

            if (c == '\\')
            {
                if (i + 1 >= source.Length) return false;

                char next = source[i + 1];
                switch (next)
                {
                    case '"': sb.Append('"'); i++; break;
                    case '\\': sb.Append('\\'); i++; break;
                    case '/': sb.Append('/'); i++; break;
                    case 'b': sb.Append('\b'); i++; break;
                    case 'f': sb.Append('\f'); i++; break;
                    case 'n': sb.Append('\n'); i++; break;
                    case 'r': sb.Append('\r'); i++; break;
                    case 't': sb.Append('\t'); i++; break;
                    case 'u':
                        if (i + 5 >= source.Length) return false;
                        string hex = source.Substring(i + 2, 4);
                        if (!int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out int codePoint))
                        {
                            return false;
                        }
                        sb.Append((char)codePoint);
                        i += 5;
                        break;
                    default:
                        sb.Append(next);
                        i++;
                        break;
                }
            }
            else
            {
                sb.Append(c);
            }
        }

        return false;
    }

    string StripMarkdownCodeFence(string text)
    {
        string trimmed = text.Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            return trimmed;
        }

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

        bool inString = false;
        bool escaping = false;
        int depth = 0;

        for (int i = start; i < text.Length; i++)
        {
            char c = text[i];

            if (escaping)
            {
                escaping = false;
                continue;
            }

            if (c == '\\')
            {
                escaping = true;
                continue;
            }

            if (c == '"')
            {
                inString = !inString;
                continue;
            }

            if (inString) continue;

            if (c == '{') depth++;
            if (c == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return text.Substring(start, i - start + 1);
                }
            }
        }

        return string.Empty;
    }
}