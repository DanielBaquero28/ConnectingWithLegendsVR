using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.Networking;
using Newtonsoft.Json;

public class AIResponseController : MonoBehaviour
{
    public TMP_InputField PromptField;
    public TMP_Text ResponseText;

    private string apiUrl = "https://api.openai.com/v1/chat/completions";
    private string apiKey;

    private void Start()
    {
        apiKey = System.Environment.GetEnvironmentVariable("OPENAI_API_KEY");

        if (string.IsNullOrEmpty(apiKey))
        {
            Debug.LogError("API key is not found in the list of environment variables");
        }
    }

    [System.Serializable]
    public class Message
    {
        public string role { get; set; }
        public string content { get; set; }
    }

    [System.Serializable]
    public class GPTRequest
    {
        public string model { get; set; }
        public List<Message> messages { get; set; }
        public float temperature { get; set; }
    }

    [System.Serializable]
    public class Usage
    {
        public int promptTokens { get; set; }
        public int completionTokens { get; set; }
        public int totalTokens { get; set; }
    }

    [System.Serializable]
    public class Choice
    {
        public Message message { get; set; }
        public object logProbs { get; set; }
        public string finishReason { get; set; }
        public int index { get; set; }
    }


    [System.Serializable]
    public class GPTResponse
    {
        public string id { get; set; }
        public string @object { get; set; }
        public long created { get; set; }
        public string model { get; set; }
        public Usage usage { get; set; }
        public List<Choice> choices { get; set; }
        public string systemFingerprint { get; set; }
    }

    public void OnSubmit()
    {
        string promptText = PromptField.text;        
        StartCoroutine(SendRequest(promptText)); 
    }

    private IEnumerator SendRequest(string userPrompt)
    {
        // Instantiating and initializing the GPT request
        GPTRequest gptRequest = new GPTRequest
        {
            model = "gpt-4o-mini",
            messages = new List<Message> { new Message { role = "user", content = userPrompt } },
            temperature = 0.7f
        };

        //string jsonData = JsonUtility.ToJson(gptRequest);
        string jsonData = JsonConvert.SerializeObject(gptRequest);

        UnityWebRequest request = new UnityWebRequest(apiUrl, "POST");
        byte[] bodyRaw = System.Text.Encoding.UTF8.GetBytes(jsonData);

        request.uploadHandler = new UploadHandlerRaw(bodyRaw);
        request.downloadHandler = new DownloadHandlerBuffer();
        request.SetRequestHeader("Content-Type", "application/json");
        request.SetRequestHeader("Authorization", "Bearer " + apiKey);

        yield return request.SendWebRequest();

        if (request.result == UnityWebRequest.Result.ConnectionError || request.result == UnityWebRequest.Result.ProtocolError)
        {
            Debug.LogError(request.error);
            ResponseText.text = "Error: " + request.error;
        }
        else
        {
            //GPTResponse gptResponse = JsonUtility.FromJson<GPTResponse>(request.downloadHandler.text);
            GPTResponse gptResponse = JsonConvert.DeserializeObject<GPTResponse>(request.downloadHandler.text);

            Debug.Log(request.downloadHandler.text);
            Debug.Log(gptResponse.choices[0].message.content.ToString());
            ResponseText.text = gptResponse.choices[0].message.content.Trim();   
        }

    }
   
}
