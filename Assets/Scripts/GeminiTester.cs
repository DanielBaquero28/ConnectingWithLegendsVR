using UnityEngine;
using UnityEngine.Networking;
using System.Collections;

public class GeminiTester : MonoBehaviour
{
    // El endpoint del modelo Flash, incluyendo la variable de la clave API
    private const string GEMINI_API_URL =
        "https://generativelanguage.googleapis.com/v1beta/models/gemini-2.5-flash:generateContent?key=";

    private string apiKey;

    private void Start()
    {
        apiKey = System.Environment.GetEnvironmentVariable("GEMINI_API_KEY");

        if (string.IsNullOrEmpty(apiKey))
            Debug.LogError("Gemini API key not found. Set GEMINI_API_KEY in environment variables.");

        string testPrompt = "¿Cuánto es 2 + 4?";

        Debug.Log("--- INICIANDO PRUEBA DE CONEXIÓN GEMINI (REST) ---");

        SendPromptToGemini(testPrompt);
    }

    public void SendPromptToGemini(string prompt)
    {
        if (string.IsNullOrEmpty(apiKey)) return;

        // ¡Iniciamos la corrutina de UnityWebRequest!
        StartCoroutine(PostRequest(prompt));
    }

    private IEnumerator PostRequest(string prompt)
    {
        // 1. CONSTRUIR EL CUERPO JSON (Payload)
        // Este es el formato que la API de Gemini espera
        string jsonPayload = $"{{\"contents\":[{{\"parts\":[{{\"text\":\"{prompt}\"}}]}}]}}";

        // 2. CREAR LA PETICIÓN
        string url = GEMINI_API_URL + apiKey;

        UnityWebRequest request = new UnityWebRequest(url, "POST");

        // Convierte el JSON a bytes para enviarlo
        byte[] bodyRaw = System.Text.Encoding.UTF8.GetBytes(jsonPayload);

        request.uploadHandler = new UploadHandlerRaw(bodyRaw);
        request.downloadHandler = new DownloadHandlerBuffer();

        // Establecer el encabezado Content-Type es CRUCIAL
        request.SetRequestHeader("Content-Type", "application/json");

        // 3. ENVIAR Y ESPERAR
        yield return request.SendWebRequest();

        // 4. MANEJO DE ERRORES
        if (request.result != UnityWebRequest.Result.Success)
        {
            Debug.LogError("Error en la petición Gemini (REST): " + request.error);
            Debug.LogError("Respuesta detallada: " + request.downloadHandler.text);
        }
        else
        {
            // 5. PROCESAR LA RESPUESTA JSON
            string jsonResponse = request.downloadHandler.text;

            // --- ¡Aquí es donde extraemos el texto! ---
            string geminiResponse = ExtractTextFromJson(jsonResponse);

            Debug.Log("Respuesta de Gemini recibida (REST): " + geminiResponse);
            // Ahora puedes mostrar 'geminiResponse' en tu TextMeshPro
        }
    }

    // Función simple para extraer el texto (deberías usar una librería JSON real, pero esto sirve para probar)
    private string ExtractTextFromJson(string json)
    {
        // Busca la etiqueta de texto y la extrae
        int startIndex = json.IndexOf("\"text\": \"") + 9;
        if (startIndex > 8)
        {
            int endIndex = json.IndexOf("\"", startIndex);
            if (endIndex != -1)
            {
                string rawText = json.Substring(startIndex, endIndex - startIndex);
                // Simple limpieza de caracteres de escape comunes
                return rawText.Replace("\\n", "\n").Replace("\\\"", "\"");
            }
        }
        return "Error al parsear la respuesta.";
    }
}