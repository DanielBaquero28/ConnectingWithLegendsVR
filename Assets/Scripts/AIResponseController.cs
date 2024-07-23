using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.Networking;

public class AIResponseController : MonoBehaviour
{
    public TMP_InputField PromptField;
    public TMP_Text ResponseText;

    private string apiUrl = "https://api.openai.com/v1/chat/completions";
    private string apiKey = null;

    [System.Serializable]
    public class Message
    {
        public string Role;
        public string Content;
    }

    [System.Serializable]
    public class GPTRequest
    {
        public string Model;
        public List<Message> Messages;
        public float Temperature;
    }

    [System.Serializable]
    public class Usage
    {
        public int PromptTokens;
        public int CompletionTokens;
        public int TotalTokens;
    }

    [System.Serializable]
    public class Choice
    {
        public Message Message;
        public object LogProbs;
        public string FinishReason;
        public int Index = 0;
    }


    [System.Serializable]
    public class GPTResponse
    {
        public int Id;
        public string @Object;
        public long Created;
        public string Model;
        public Usage @Usage;
        public List<Choice> Choices;
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
            Model = "gpt-4o-mini",
            Messages = new List<Message> { new Message { Role = "user", Content = userPrompt } },
            Temperature = 0.7f
        };

        string jsonData = JsonUtility.ToJson(gptRequest);

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
            GPTResponse gptResponse = JsonUtility.FromJson<GPTResponse>(request.downloadHandler.text);
            ResponseText.text = gptResponse.Choices[0].Message.Content.Trim();
        }

    }
   
}
